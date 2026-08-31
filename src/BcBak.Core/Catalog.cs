using System.Buffers.Binary;

namespace BcBak;

public sealed record AllocUnit(long Auid, byte Type, long OwnerId, byte[] FirstPage, byte[] RootPage, byte[] FirstIamPage);
public sealed record RowSet(long RowSetId, int IdMajor, int IdMinor, long Rows, byte CompressionLevel);
public sealed record SysObject(int ObjectId, string Name, string Type);
public sealed record SysColumn(int ColId, string Name, byte XType, short MaxLength, byte Precision, byte Scale);
public sealed record SysIndexCol(int IndexId, int KeyOrdinal, int ColId);

/// <summary>
/// Reads the SQL Server system catalog base tables directly from page images.
/// Bootstrap: boot page (1:9) holds a 6-byte pointer to the first sysallocunits page at
/// page offset 612 (observed on BC 27.5 and 28.1, verified by walking the chain and
/// matching all rows against sys.system_internals_allocation_units — see PROVENANCE.md).
/// Physical column layouts of sysallocunits/sysrowsets/sysschobjs/syscolpars/sysiscols
/// were validated row-for-row against sys.* views on a restored copy of the same backup.
/// </summary>
public sealed class Catalog
{
    public const int BootPageFirstSysIndexesOffset = 612;
    const long SysRowSetsRowSetId = 5L << 16;        // fixed partition id of sysrowsets itself
    const int SysSchObjsId = 34, SysColParsId = 41, SysIsColsId = 55;

    readonly PageFile _pf;
    public List<AllocUnit> AllocUnits { get; } = new();
    public List<RowSet> RowSets { get; } = new();
    public Dictionary<int, SysObject> Objects { get; } = new();
    public Dictionary<int, List<SysColumn>> Columns { get; } = new();
    public Dictionary<int, List<SysIndexCol>> IndexColumns { get; } = new();

    public Catalog(PageFile pf)
    {
        _pf = pf;
        var boot = pf.GetPage(1, 9);
        if (PageHeader.Type(boot) != 13) throw new InvalidDataException("page (1:9) is not the boot page");
        var (fp, ff) = ReadPagePtr(boot, BootPageFirstSysIndexesOffset);
        foreach (var (page, slot) in WalkChain(ff, fp))
            AllocUnits.Add(ParseAllocUnit(page, slot));
        foreach (var (page, slot) in WalkRowset(SysRowSetsRowSetId))
            RowSets.Add(ParseRowSet(page, slot));
        foreach (var (page, slot) in WalkTable(SysSchObjsId))
        {
            var o = ParseSysObject(page, slot);
            Objects[o.ObjectId] = o;
        }
    }

    /// <summary>Column + index metadata is loaded lazily per object (syscolpars/sysiscols are large).</summary>
    public void LoadColumnMetadata()
    {
        foreach (var (page, slot) in WalkTable(SysColParsId))
        {
            var (_, fx, _, _, varCols) = FixedVarRecord.Parse(page, slot);
            int objId = BinaryPrimitives.ReadInt32LittleEndian(fx);
            short number = BinaryPrimitives.ReadInt16LittleEndian(fx.AsSpan(4));
            if (number != 0) continue; // procedure parameters etc.
            int colId = BinaryPrimitives.ReadInt32LittleEndian(fx.AsSpan(6));
            byte xtype = fx[10];
            short maxLen = BinaryPrimitives.ReadInt16LittleEndian(fx.AsSpan(15));
            byte prec = fx[17], scale = fx[18];
            string name = varCols.Count > 0 ? DecodeUtf16(varCols[0].data) : $"col{colId}";
            if (!Columns.TryGetValue(objId, out var list)) Columns[objId] = list = new();
            list.Add(new SysColumn(colId, name, xtype, maxLen, prec, scale));
        }
        foreach (var list in Columns.Values) list.Sort((a, b) => a.ColId.CompareTo(b.ColId));
        foreach (var (page, slot) in WalkTable(SysIsColsId))
        {
            var (_, fx, _, _, _) = FixedVarRecord.Parse(page, slot);
            int objId = BinaryPrimitives.ReadInt32LittleEndian(fx);
            int idxId = BinaryPrimitives.ReadInt32LittleEndian(fx.AsSpan(4));
            int subId = BinaryPrimitives.ReadInt32LittleEndian(fx.AsSpan(8));
            int colId = BinaryPrimitives.ReadInt32LittleEndian(fx.AsSpan(16)); // intprop
            if (!IndexColumns.TryGetValue(objId, out var list)) IndexColumns[objId] = list = new();
            list.Add(new SysIndexCol(idxId, subId, colId));
        }
    }

    public static (int pageId, int fileId) ReadPagePtr(ReadOnlySpan<byte> b, int off)
        => (BinaryPrimitives.ReadInt32LittleEndian(b[off..]), BinaryPrimitives.ReadUInt16LittleEndian(b[(off + 4)..]));

    IEnumerable<(byte[] page, int slot)> WalkChain(int fileId, int pageId)
    {
        var seen = new HashSet<(int, int)>();
        while (pageId != 0 && seen.Add((fileId, pageId)))
        {
            var p = _pf.GetPage(fileId, pageId);
            foreach (var so in PageHeader.SlotOffsets(p))
            {
                int rt = FixedVarRecord.RecordType(p, so);
                if (rt is 5 or 6 or 7) continue;               // ghost records: deleted, not yet cleaned up
                if (rt != 0) throw new NotSupportedException($"record type {rt} not supported in catalog walk");
                yield return (p, so);
            }
            (pageId, fileId) = PageHeader.NextPage(p);
        }
    }

    public AllocUnit AuForRowset(long rowsetId, byte auType = 1)
        => AllocUnits.FirstOrDefault(a => a.OwnerId == rowsetId && a.Type == auType)
           ?? throw new InvalidDataException($"no allocation unit for rowset {rowsetId}");

    IEnumerable<(byte[] page, int slot)> WalkRowset(long rowsetId)
    {
        var au = AuForRowset(rowsetId);
        var (pid, fid) = ReadPagePtr(au.FirstPage, 0);
        return WalkChain(fid, pid);
    }

    public RowSet RowsetFor(int idMajor, params int[] idMinorPreference)
    {
        foreach (var m in idMinorPreference)
        {
            var rs = RowSets.FirstOrDefault(r => r.IdMajor == idMajor && r.IdMinor == m);
            if (rs != null) return rs;
        }
        throw new InvalidDataException($"no rowset for object {idMajor}");
    }

    IEnumerable<(byte[] page, int slot)> WalkTable(int objId)
        => WalkRowset(RowsetFor(objId, 1, 0).RowSetId);

    static AllocUnit ParseAllocUnit(byte[] page, int slot)
    {
        var (_, fx, _, _, _) = FixedVarRecord.Parse(page, slot);
        long auid = BinaryPrimitives.ReadInt64LittleEndian(fx);
        byte type = fx[8];
        long owner = BinaryPrimitives.ReadInt64LittleEndian(fx.AsSpan(9));
        return new AllocUnit(auid, type, owner,
            fx.AsSpan(23, 6).ToArray(), fx.AsSpan(29, 6).ToArray(), fx.AsSpan(35, 6).ToArray());
    }

    static RowSet ParseRowSet(byte[] page, int slot)
    {
        var (_, fx, _, _, _) = FixedVarRecord.Parse(page, slot);
        long rsid = BinaryPrimitives.ReadInt64LittleEndian(fx);
        int idMajor = BinaryPrimitives.ReadInt32LittleEndian(fx.AsSpan(9));
        int idMinor = BinaryPrimitives.ReadInt32LittleEndian(fx.AsSpan(13));
        long rows = BinaryPrimitives.ReadInt64LittleEndian(fx.AsSpan(27));
        byte cmpr = fx[35];
        return new RowSet(rsid, idMajor, idMinor, rows, cmpr);
    }

    static SysObject ParseSysObject(byte[] page, int slot)
    {
        var (_, fx, _, _, varCols) = FixedVarRecord.Parse(page, slot);
        int id = BinaryPrimitives.ReadInt32LittleEndian(fx);
        string type = System.Text.Encoding.ASCII.GetString(fx.AsSpan(13, 2)).Trim();
        string name = varCols.Count > 0 ? DecodeUtf16(varCols[0].data) : "";
        return new SysObject(id, name, type);
    }

    static string DecodeUtf16(byte[] b) => System.Text.Encoding.Unicode.GetString(b);
}
