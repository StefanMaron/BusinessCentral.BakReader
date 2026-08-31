namespace BcBak;

/// <summary>One readable table of a source, named the way SQL Server names the object.</summary>
public sealed class SourceTable
{
    public required string Name { get; init; }
    /// <summary>Storage compression of the rows in the file ("none"/"row"/"page"), or "-" where the container has none.</summary>
    public required string Compression { get; init; }
    /// <summary>Row count, computed on demand — a bacpac has to count rows to know.</summary>
    public required Func<long> RowCount { get; init; }
    /// <summary>The source's own handle for this table.</summary>
    public required object Handle { get; init; }
}

/// <summary>
/// What the query surface (tables / companies / describe / read / serve) needs of a
/// container: the tables, their columns, their clustered key, and their rows already
/// decoded. Everything above this line is BC semantics — company and app-id name parsing,
/// $ext companions, AL symbols — and is shared by every container.
///
/// The abstraction is deliberately at "enumerate rows and column metadata for a table",
/// not at pages: a .bacpac has no pages, no compression and no LOB indirection, so an
/// abstraction over storage would have nothing to say about it.
/// </summary>
public interface IBcSource : IDisposable
{
    IReadOnlyList<SourceTable> Tables { get; }
    IReadOnlyList<SysColumn> Columns(SourceTable t);
    /// <summary>
    /// The columns a table's rows are identified by, in key order; empty when it has none.
    /// The two containers answer this from what they actually carry — a .bak from the
    /// clustered index, a .bacpac from the primary-key constraint — and those are not
    /// always the same key: BC clusters a table on whichever AL key carries Clustered = 1.
    /// The one caller that joins on this key asks a $ext companion, where the two coincide
    /// (PROVENANCE.md, "Extension companion join key"). Do not use it as a primary key.
    /// </summary>
    IReadOnlyList<string> RowKeyColumns(SourceTable t);
    /// <summary>
    /// Rows as decoded values, keyed by SQL column name. Only <paramref name="columns"/> are
    /// decoded — everything else is skipped, so selecting one column does not pay for a
    /// table's blobs. Lazy: a --top stops the read.
    /// </summary>
    IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(SourceTable t, IReadOnlyList<SysColumn> columns);
    /// <summary>
    /// Do whatever up-front work makes a many-request session cheaper. What that is
    /// depends on the container, and for a .bak it is now nothing.
    /// </summary>
    void PreloadMetadata();
    /// <summary>One line about what was opened, for the tables command's stderr note.</summary>
    string Banner { get; }
}

public static class BcSource
{
    /// <summary>A .bacpac is an Open Packaging zip; a .bak starts with an MTF descriptor block.</summary>
    public static bool IsBacpac(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[4];
        return s.Read(magic) == 4 && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4;
    }

    public static IBcSource Open(string path, bool prefetch = false)
        => IsBacpac(path) ? new BacpacSource(path) : new BakSource(path, prefetch);
}

/// <summary>The SQL Server backup path: page map, system catalog, IAM chains, storage decoding.</summary>
public sealed class BakSource : IBcSource
{
    readonly PageFile _pf;
    readonly Catalog _cat;
    readonly TableReader _tr;
    readonly LobReader _lob;
    List<SourceTable>? _tables;

    public BakSource(string path, bool prefetch = false)
    {
        _pf = new PageFile(path, prefetch);
        _cat = new Catalog(_pf);
        _tr = new TableReader(_pf, _cat);
        _lob = new LobReader(_pf);
    }

    /// <summary>The underlying page file, for the page-map commands that only a backup has.</summary>
    public PageFile PageFile => _pf;

    public IReadOnlyList<SourceTable> Tables => _tables ??= BuildTables();

    List<SourceTable> BuildTables()
    {
        var list = new List<SourceTable>();
        foreach (var o in _cat.Objects.Values)
        {
            if (o.Type != "U") continue;
            RowSet rs;
            try { rs = _cat.RowsetFor(o.ObjectId, 1, 0); } catch (InvalidDataException) { continue; }
            list.Add(new SourceTable
            {
                Name = o.Name,
                Compression = rs.CompressionLevel switch { 0 => "none", 1 => "row", 2 => "page", var x => x.ToString() },
                RowCount = () => rs.Rows,
                Handle = (o, rs),
            });
        }
        return list;
    }

    static (SysObject Obj, RowSet RowSet) H(SourceTable t) => ((SysObject, RowSet))t.Handle;

    public IReadOnlyList<SysColumn> Columns(SourceTable t)
    {
        var (o, _) = H(t);
        _cat.LoadColumnMetadata(o.ObjectId);
        return _cat.Columns.TryGetValue(o.ObjectId, out var cols) ? cols : Array.Empty<SysColumn>();
    }

    /// <summary>The clustered index key — see <see cref="IBcSource.RowKeyColumns"/>.</summary>
    public IReadOnlyList<string> RowKeyColumns(SourceTable t)
    {
        var (o, _) = H(t);
        _cat.LoadColumnMetadata(o.ObjectId);
        if (!_cat.IndexColumns.TryGetValue(o.ObjectId, out var idx)) return Array.Empty<string>();
        var cols = _cat.Columns[o.ObjectId];
        return idx.Where(i => i.IndexId == 1).OrderBy(i => i.KeyOrdinal)
            .Select(i => cols.FirstOrDefault(c => c.ColId == i.ColId)?.Name
                ?? throw new InvalidDataException($"clustered key column id {i.ColId} of {o.Name} is not in syscolpars"))
            .ToList();
    }

    public IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(SourceTable t, IReadOnlyList<SysColumn> columns)
    {
        var (o, rs) = H(t);
        bool compressed = rs.CompressionLevel > 0;
        foreach (var row in _tr.ReadRows(o.ObjectId))
        {
            var outRow = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            foreach (var c in columns) outRow[c.Name] = SqlTypes.Decode(row[c.Name], c, compressed, _lob);
            yield return outRow;
        }
    }

    /// <summary>
    /// Nothing to do. This used to load every object's column metadata, because a
    /// per-object load still scanned both heaps end to end. Now that one object's columns
    /// are a clustered-index descent, the scan is pure cost: measured on the BC 28.1 demo
    /// backup through serve, spawn to first answered read went 47.9 ms with the preload to
    /// 25.4 ms without it, and even a session reading 20 different tables finished in
    /// 51.1 ms rather than 84.5 ms.
    /// </summary>
    public void PreloadMetadata() { }

    public string Banner => $"[{_cat.TotalObjectCount} objects, {_pf.PageCount} pages, {_pf.SupersededPageCount} pages superseded by the changed-extent re-read]";

    public void Dispose() => _pf.Dispose();
}

/// <summary>The bacpac path: zip container, model.xml schema, native-BCP rows.</summary>
public sealed class BacpacSource : IBcSource
{
    readonly BacpacFile _file;
    readonly Dictionary<string, long> _rowCounts = new(StringComparer.Ordinal);
    List<SourceTable>? _tables;

    public BacpacSource(string path) => _file = new BacpacFile(path);

    public IReadOnlyList<SourceTable> Tables => _tables ??= _file.Tables
        .Select(kv => new SourceTable
        {
            Name = kv.Key,
            Compression = "-",          // a bacpac stores logical rows: no storage compression to report
            RowCount = () => CountRows(kv.Key, kv.Value),
            Handle = kv.Value,
        }).ToList();

    long CountRows(string name, BacpacTable t)
    {
        if (_rowCounts.TryGetValue(name, out var n)) return n;
        return _rowCounts[name] = _file.CountRows(t);
    }

    static BacpacTable H(SourceTable t) => (BacpacTable)t.Handle;

    public IReadOnlyList<SysColumn> Columns(SourceTable t)
        => H(t).Columns.Select((c, i) => c.ToSysColumn(i + 1)).ToList();

    /// <summary>The primary-key constraint model.xml declares — see <see cref="IBcSource.RowKeyColumns"/>.</summary>
    public IReadOnlyList<string> RowKeyColumns(SourceTable t) => H(t).KeyColumns;

    public IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(SourceTable t, IReadOnlyList<SysColumn> columns)
    {
        var table = H(t);
        var want = columns.Select(c => table.Columns.FirstOrDefault(bc => bc.Name == c.Name)
            ?? throw new InvalidDataException($"bacpac table {table.Name} has no column {c.Name}")).ToList();
        foreach (var cells in _file.ReadRows(table, want))
        {
            var row = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            for (int i = 0; i < columns.Count; i++)
                row[columns[i].Name] = SqlTypes.Decode(cells[i], columns[i], compressed: false, lob: null, textIsUtf16: true);
            yield return row;
        }
    }

    public void PreloadMetadata() { _ = _file.Tables; }

    public string Banner
    {
        get
        {
            var t = _file.Tables;
            int withData = t.Values.Count(x => x.DataEntries.Count > 0);
            return $"[bacpac \"{_file.DatabaseName}\" exported by DacFx {_file.ProductVersion}: {t.Count} tables in model.xml, {withData} with data]";
        }
    }

    public void Dispose() => _file.Dispose();
}
