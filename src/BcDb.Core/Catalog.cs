using System.Buffers.Binary;

namespace BusinessCentral.DbReader;

internal sealed record AllocUnit(long Auid, byte Type, long OwnerId, byte[] FirstPage, byte[] RootPage, byte[] FirstIamPage);
internal sealed record RowSet(long RowSetId, int IdMajor, int IdMinor, long Rows, byte CompressionLevel);
internal sealed record SysObject(int ObjectId, string Name, string Type);
public sealed record SysColumn(int ColId, string Name, byte XType, short MaxLength, byte Precision, byte Scale)
{
    /// <summary>The SQL type name, e.g. "nvarchar" or "datetime2".</summary>
    public string TypeName => SqlTypes.Name(XType);
}
internal sealed record SysIndexCol(int IndexId, int KeyOrdinal, int ColId);

/// <summary>
/// One column of a rowset's physical leaf layout, from the sysrscols system table —
/// the layout SQL Server itself uses. Never derive layout from syscolpars order: on a
/// database with ALTER history (every upgraded BC database), physical order, offsets
/// and null-bit numbering all differ from declaration order, and dropped columns keep
/// their slots. Record layout (54-byte fixed part): rsid u64@0, rscolid u32@8
/// (0x04000000 flag = dropped; 0x08000000 flag = internal, a physical column that is
/// no user column — observed for the in-row version column change tracking adds, whose
/// masked low bits collide with a real column id), hbcolid u32@12, ti u32@24 (low byte = system type id;
/// for decimal prec@+8/scale@+16 bits, for time/datetime2 scale@+8, for string/binary
/// types max length@+8, 0 = MAX), ordkey i16@32, status u32@36 (bit 0x02 = dropped),
/// leaf offset i16@40 (negative = variable-length ordinal), null bit u16@44,
/// bit position u16@48. Derived from probe tables with ALTER history and validated
/// field-by-field against sys.system_internals_partition_columns
/// (PROVENANCE.md "Physical rowset layout").
/// </summary>
internal sealed record PhysColumn(int ColId, bool Dropped, bool Internal, byte XType, short MaxLength, byte Precision, byte Scale,
    short KeyOrdinal, short LeafOffset, ushort NullBit, ushort BitPos)
{
    public bool IsVar => LeafOffset < 0;
    public int VarOrdinal => -LeafOffset;
}

/// <summary>
/// Reads the SQL Server system catalog base tables directly from page images.
/// Bootstrap: boot page (1:9) holds a 6-byte pointer to the first sysallocunits page at
/// page offset 612 (observed on BC 27.5 and 28.1, verified by walking the chain and
/// matching all rows against sys.system_internals_allocation_units — see PROVENANCE.md).
/// Physical column layouts of sysallocunits/sysrowsets/sysschobjs/syscolpars/sysiscols
/// were validated row-for-row against sys.* views on a restored copy of the same backup.
/// </summary>
internal sealed class Catalog
{
    public const int BootPageFirstSysIndexesOffset = 612;
    const long SysRowSetsRowSetId = 5L << 16;        // fixed partition id of sysrowsets itself
    const int SysSchObjsId = 34, SysColParsId = 41, SysIsColsId = 55, SysRsColsId = 3;

    readonly PageFile _pf;
    public List<AllocUnit> AllocUnits { get; } = new();
    public List<RowSet> RowSets { get; } = new();
    public Dictionary<int, SysObject> Objects { get; } = new();
    public Dictionary<int, List<SysColumn>> Columns { get; } = new();
    public Dictionary<int, List<SysIndexCol>> IndexColumns { get; } = new();

    readonly Dictionary<long, RowSet> _rowsetIndex = new();   // (idMajor << 32 | idMinor) -> rowset

    public Catalog(PageFile pf)
    {
        _pf = pf;
        var boot = pf.GetPage(1, 9);
        if (PageHeader.Type(boot) != 13) throw new InvalidDataException("page (1:9) is not the boot page");
        var (fp, ff) = ReadPagePtr(boot, BootPageFirstSysIndexesOffset);
        foreach (var (page, slot) in WalkChain(ff, fp))
            AllocUnits.Add(ParseAllocUnit(page, slot));
        foreach (var (page, slot) in WalkRowset(SysRowSetsRowSetId))
        {
            var rs = ParseRowSet(page, slot);
            RowSets.Add(rs);
            _rowsetIndex.TryAdd(((long)rs.IdMajor << 32) | (uint)rs.IdMinor, rs);
        }
        foreach (var (page, slot) in WalkTable(SysSchObjsId))
        {
            TotalObjectCount++;
            var fx = FixedVarRecord.ParseFixed(page, slot, out int nameStart, out int nameLen);
            // Type is the two-byte field at fixed offset 13. Only "U " (user table) is
            // ever consumed; the demo backups carry ~19x more objects than tables, and
            // each one skipped here is a UTF-16 name decode and a dictionary entry not
            // paid for. The second byte matters: "UQ" is a unique constraint, not a
            // table, and admitting it costs an exception per constraint in BuildTables.
            if (fx[13] != (byte)'U' || fx[14] != (byte)' ') continue;
            int id = BinaryPrimitives.ReadInt32LittleEndian(fx);
            Objects[id] = new SysObject(id, DecodeUtf16(page, nameStart, nameLen), "U");
        }
    }

    /// <summary>Rows in sysschobjs, including the objects <see cref="Objects"/> filters out.</summary>
    public int TotalObjectCount { get; private set; }

    /// <summary>Lookups answered by descending a clustered index rather than scanning it.</summary>
    public int ClusteredSeeks { get; private set; }
    /// <summary>Lookups that fell back to a scan because the index had an underived shape.</summary>
    public int ClusteredSeekDeclines { get; private set; }

    readonly Dictionary<long, HashSet<int>> _auPageSets = new();

    HashSet<int> AuPageSet(AllocUnit au)
    {
        if (!_auPageSets.TryGetValue(au.Auid, out var set))
            _auPageSets[au.Auid] = set = AllocUnitPages(au).ToHashSet();
        return set;
    }

    /// <summary>
    /// The rows of a catalog base table whose leading clustered-key column equals
    /// <paramref name="key"/>, found by descending the index. Null when the index shape is
    /// one <see cref="ClusteredSeek"/> has not been derived for — the caller then scans,
    /// which produces the same rows and only costs time.
    /// </summary>
    IEnumerable<(byte[] page, int slot)>? TrySeekRows(int catalogObjectId, long key, int leadWidth, string what)
    {
        var au = AuForRowset(RowsetFor(catalogObjectId, 1, 0).RowSetId);
        var leaf = ClusteredSeek.FindLeaf(_pf, au, AuPageSet(au), key, leadWidth, what);
        if (leaf is null) { ClusteredSeekDeclines++; return null; }
        ClusteredSeeks++;
        return WalkLeafRun(leaf, key, leadWidth, what);
    }

    /// <summary>
    /// Leaf rows from <paramref name="first"/> forward whose leading key equals
    /// <paramref name="key"/>, following the leaf chain because one object's rows span
    /// pages. The leading key column of every catalog base table sits at record offset 4
    /// (sysrscols records leaf offset 4 for the first key column of all six).
    /// </summary>
    IEnumerable<(byte[] page, int slot)> WalkLeafRun(byte[] first, long key, int leadWidth, string what)
    {
        var page = first;
        var seen = new HashSet<int>();
        while (true)
        {
            long previous = 0;
            bool havePrevious = false;
            foreach (var so in PageHeader.SlotOffsets(page))
            {
                if (so == 0) continue;                          // empty slot
                if (so < 96) throw new InvalidDataException($"{what}: leaf slot offset {so} inside the page header — page corrupt?");
                int rt = FixedVarRecord.RecordType(page, so);
                if (rt is 5 or 6 or 7) continue;                // ghost
                if (rt != 0) throw new NotSupportedException($"{what}: record type {rt} not supported in a catalog leaf walk");
                long k = leadWidth == 8
                    ? BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(so + 4))
                    : BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(so + 4));
                if (havePrevious && k < previous)
                    throw new InvalidDataException($"{what}: leaf keys are not ascending ({k} after {previous}) — refusing to guess");
                previous = k;
                havePrevious = true;
                if (k < key) continue;                          // still before the run
                if (k > key) yield break;                       // past it: the run is over
                yield return (page, so);
            }
            var (nextPid, nextFid) = PageHeader.NextPage(page);
            if (nextPid == 0) yield break;
            if (nextFid != 1)
                throw new NotSupportedException($"{what}: leaf chain continues into file {nextFid} — only single-data-file databases are supported");
            if (!seen.Add(nextPid))
                throw new InvalidDataException($"{what}: leaf chain revisits page 1:{nextPid} — refusing to loop");
            page = _pf.GetPage(1, nextPid);
        }
    }

    bool _allColumnsLoaded;
    readonly HashSet<int> _columnsLoadedFor = new();

    /// <summary>
    /// Load column + index metadata from syscolpars/sysiscols — for one object, or for all
    /// (objectId null). Both tables are heaps of every column of every object, so the page
    /// walk always covers all pages; the per-object form skips the record parse and
    /// materialization for other objects (the object id is the first fixed column of both
    /// record layouts, read without a full parse). Loads are cumulative and idempotent.
    /// </summary>
    public void LoadColumnMetadata(int? objectId = null)
    {
        if (_allColumnsLoaded || (objectId is { } wanted0 && _columnsLoadedFor.Contains(wanted0))) return;
        // syscolpars is clustered on the object id, so one object's columns can be
        // reached by descending the index instead of scanning every leaf page.
        var colParsRows = objectId is { } seekCols
            ? TrySeekRows(SysColParsId, seekCols, 4, "syscolpars")
            : null;
        foreach (var (page, slot) in colParsRows ?? WalkTable(SysColParsId))
        {
            int objId = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(slot + 4));
            if (objectId is { } w ? objId != w : _columnsLoadedFor.Contains(objId)) continue;
            var fx = FixedVarRecord.ParseFixed(page, slot, out int nameStart, out int nameLen);
            short number = BinaryPrimitives.ReadInt16LittleEndian(fx[4..]);
            if (number != 0) continue; // procedure parameters etc.
            int colId = BinaryPrimitives.ReadInt32LittleEndian(fx[6..]);
            byte xtype = fx[10];
            short maxLen = BinaryPrimitives.ReadInt16LittleEndian(fx[15..]);
            byte prec = fx[17], scale = fx[18];
            string name = nameLen > 0 ? DecodeUtf16(page, nameStart, nameLen) : $"col{colId}";
            if (!Columns.TryGetValue(objId, out var list)) Columns[objId] = list = new();
            list.Add(new SysColumn(colId, name, xtype, maxLen, prec, scale));
        }
        foreach (var list in Columns.Values) list.Sort((a, b) => a.ColId.CompareTo(b.ColId));
        var isColsRows = objectId is { } seekIsCols
            ? TrySeekRows(SysIsColsId, seekIsCols, 4, "sysiscols")
            : null;
        foreach (var (page, slot) in isColsRows ?? WalkTable(SysIsColsId))
        {
            int objId = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(slot + 4));
            if (objectId is { } w ? objId != w : _columnsLoadedFor.Contains(objId)) continue;
            var fx = FixedVarRecord.ParseFixed(page, slot, out _, out _);
            int idxId = BinaryPrimitives.ReadInt32LittleEndian(fx[4..]);
            int subId = BinaryPrimitives.ReadInt32LittleEndian(fx[8..]);
            int colId = BinaryPrimitives.ReadInt32LittleEndian(fx[16..]); // intprop
            if (!IndexColumns.TryGetValue(objId, out var list)) IndexColumns[objId] = list = new();
            list.Add(new SysIndexCol(idxId, subId, colId));
        }
        if (objectId is { } done) _columnsLoadedFor.Add(done); else _allColumnsLoaded = true;
    }

    Dictionary<long, List<PhysColumn>>? _rowsetColumns;                       // every rowset, when scanned
    readonly Dictionary<long, List<PhysColumn>> _seekedRowsetColumns = new();  // one rowset at a time, when seeked

    /// <summary>
    /// Physical leaf layout of a rowset, in null-bit (physical) order. See <see cref="PhysColumn"/>.
    ///
    /// sysrscols is clustered on the rowset id, so one rowset's layout is a descent rather
    /// than a scan of all 948 leaf pages / 112,849 rows the BC 28.1 demo backup holds. When
    /// the whole table has already been scanned that answer is used instead, and a rowset
    /// whose index cannot be descended falls back to the scan.
    /// </summary>
    public List<PhysColumn> RowsetColumns(long rowsetId)
    {
        if (_rowsetColumns is not null) return RowsetColumnsByScan(rowsetId);
        if (_seekedRowsetColumns.TryGetValue(rowsetId, out var cached)) return cached;

        var rows = TrySeekRows(SysRsColsId, rowsetId, 8, "sysrscols");
        if (rows is null) return RowsetColumnsByScan(rowsetId);

        var list = new List<PhysColumn>();
        foreach (var (page, slot) in rows) list.Add(ParseRowsetColumn(page, slot).Column);
        if (list.Count == 0)
            throw new InvalidDataException($"no sysrscols layout for rowset {rowsetId}");
        list.Sort((a, b) => a.NullBit.CompareTo(b.NullBit));
        return _seekedRowsetColumns[rowsetId] = list;
    }

    /// <summary>The same layout, reached by scanning every sysrscols row. The seek is checked against this.</summary>
    public List<PhysColumn> RowsetColumnsByScan(long rowsetId)
    {
        if (_rowsetColumns is null)
        {
            _rowsetColumns = new();
            foreach (var (page, slot) in WalkTable(SysRsColsId))
            {
                var (rsid, column) = ParseRowsetColumn(page, slot);
                if (!_rowsetColumns.TryGetValue(rsid, out var list)) _rowsetColumns[rsid] = list = new();
                list.Add(column);
            }
            foreach (var list in _rowsetColumns.Values) list.Sort((a, b) => a.NullBit.CompareTo(b.NullBit));
        }
        return _rowsetColumns.TryGetValue(rowsetId, out var cols)
            ? cols
            : throw new InvalidDataException($"no sysrscols layout for rowset {rowsetId}");
    }

    static (long RowSetId, PhysColumn Column) ParseRowsetColumn(byte[] page, int slot)
    {
        var fx = FixedVarRecord.ParseFixed(page, slot, out _, out _);
        long rsid = BinaryPrimitives.ReadInt64LittleEndian(fx);
        uint rscolid = BinaryPrimitives.ReadUInt32LittleEndian(fx[8..]);
        uint ti = BinaryPrimitives.ReadUInt32LittleEndian(fx[24..]);
        short ordkey = BinaryPrimitives.ReadInt16LittleEndian(fx[32..]);
        uint status = BinaryPrimitives.ReadUInt32LittleEndian(fx[36..]);
        short offset = BinaryPrimitives.ReadInt16LittleEndian(fx[40..]);
        ushort nullbit = BinaryPrimitives.ReadUInt16LittleEndian(fx[44..]);
        ushort bitpos = BinaryPrimitives.ReadUInt16LittleEndian(fx[48..]);
        byte xtype = (byte)(ti & 0xff);
        byte b1 = (byte)((ti >> 8) & 0xff), b2 = (byte)((ti >> 16) & 0xff);
        int strLen = (int)((ti >> 8) & 0xffff);
        short maxLen = xtype switch
        {
            106 or 108 => (short)DecimalStorageBytes(b1),
            231 or 239 or 167 or 175 or 165 or 173 or 34 or 35 or 99 or 241 => strLen == 0 ? (short)-1 : (short)strLen,
            _ => (short)FixedWidth(xtype, b1),
        };
        (byte prec, byte scale) = xtype switch
        {
            106 or 108 => (b1, b2),
            41 or 42 or 43 => ((byte)0, b1),
            _ => ((byte)0, (byte)0),
        };
        bool dropped = (status & 0x02) != 0 || (rscolid & 0x04000000) != 0;
        bool internalCol = (rscolid & 0x08000000) != 0;
        return (rsid, new PhysColumn((int)(rscolid & 0x00FFFFFF), dropped, internalCol, xtype, maxLen, prec, scale,
                                     ordkey, offset, nullbit, bitpos));
    }

    static int DecimalStorageBytes(int precision)
        => precision <= 9 ? 5 : precision <= 19 ? 9 : precision <= 28 ? 13 : 17;

    static int FixedWidth(byte xtype, byte tiLen) => xtype switch
    {
        48 or 104 => 1, 52 => 2, 56 or 59 => 4, 127 or 62 or 61 or 189 => 8, 36 => 16,
        58 => 4, 122 => 4, 60 => 8, 40 => 3,
        41 => tiLen <= 2 ? 3 : tiLen <= 4 ? 4 : 5,
        42 => (tiLen <= 2 ? 3 : tiLen <= 4 ? 4 : 5) + 3,
        _ => tiLen,
    };

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
                if (so == 0) continue;                         // empty slot (deleted / never completed)
                if (so < 96) throw new InvalidDataException($"catalog slot offset {so} inside the page header — page corrupt?");
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
        // The chain walk below reads one page at a time and only learns the next page id
        // from the page it just read, so it can never have more than one read in flight.
        // The IAM chain already knows the whole page set, so warm it first, in file order
        // and in parallel. See PageFile.Prefetch for the measurement that motivates it.
        _pf.PrefetchOnce(au.Auid, () => AllocUnitPages(au));
        var (pid, fid) = ReadPagePtr(au.FirstPage, 0);
        return WalkChain(fid, pid);
    }

    /// <summary>
    /// Every page an allocation unit's IAM chain claims, derived without reading any of
    /// the pages themselves — which is the whole point, since reading them one at a time
    /// is the cost this feeds. It is deliberately a superset of the unit's data pages:
    /// the per-page PFS allocation filter and the page-type filter that
    /// <see cref="TableReader.DataPages"/> applies both need the page read, and warming a
    /// few extra pages of an extent that is contiguous on disk is free.
    ///
    /// This drives the prefetch and nothing else, so a chain it cannot make sense of ends
    /// the enumeration instead of throwing: the result can only ever be "fewer pages
    /// warmed". The same malformed structure still fails loudly in
    /// <see cref="TableReader.DataPages"/>, which is where it is actually relied upon.
    /// </summary>
    public List<int> AllocUnitPages(AllocUnit au)
    {
        var pages = new List<int>();
        var (iamPid, iamFid) = ReadPagePtr(au.FirstIamPage, 0);
        var seen = new HashSet<(int, int)>();
        while (iamPid != 0 && seen.Add((iamFid, iamPid)))
        {
            if (iamFid != 1 || !_pf.TryGetPage(iamFid, iamPid, out var iam)) break;
            if (PageHeader.Type(iam) != 10) break;
            var slotOffs = PageHeader.SlotOffsets(iam).ToArray();
            if (slotOffs.Length != 2) break;
            int s0 = slotOffs[0], s1 = slotOffs[1];
            var (basePid, baseFid) = ReadPagePtr(iam, s0 + 40);
            if (baseFid is not (0 or 1) || basePid % PageFile.GamIntervalPages != 0) break;
            for (int sp = 0; sp < 8; sp++)
            {
                var (spPid, spFid) = ReadPagePtr(iam, s0 + 46 + 6 * sp);
                if (spFid == 1 && spPid != 0) pages.Add(spPid);
            }
            int bitmapLen = BinaryPrimitives.ReadUInt16LittleEndian(iam.AsSpan(s1 + 2));
            for (int b = 0; b < bitmapLen; b++)
            {
                byte v = iam[s1 + 4 + b];
                if (v == 0) continue;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((v & (1 << bit)) == 0) continue;
                    int extent = b * 8 + bit;
                    if (extent >= PageFile.GamIntervalExtents) continue;   // bitmap overhang, see TableReader
                    for (int pg = basePid + extent * PageFile.PagesPerExtent;
                         pg < basePid + extent * PageFile.PagesPerExtent + PageFile.PagesPerExtent; pg++)
                        pages.Add(pg);
                }
            }
            (iamPid, iamFid) = PageHeader.NextPage(iam);
        }
        return pages;
    }

    public RowSet RowsetFor(int idMajor, params int[] idMinorPreference)
    {
        foreach (var m in idMinorPreference)
            if (_rowsetIndex.TryGetValue(((long)idMajor << 32) | (uint)m, out var rs)) return rs;
        throw new InvalidDataException($"no rowset for object {idMajor}");
    }

    IEnumerable<(byte[] page, int slot)> WalkTable(int objId)
        => WalkRowset(RowsetFor(objId, 1, 0).RowSetId);

    static AllocUnit ParseAllocUnit(byte[] page, int slot)
    {
        var fx = FixedVarRecord.ParseFixed(page, slot, out _, out _);
        long auid = BinaryPrimitives.ReadInt64LittleEndian(fx);
        byte type = fx[8];
        long owner = BinaryPrimitives.ReadInt64LittleEndian(fx[9..]);
        return new AllocUnit(auid, type, owner,
            fx.Slice(23, 6).ToArray(), fx.Slice(29, 6).ToArray(), fx.Slice(35, 6).ToArray());
    }

    static RowSet ParseRowSet(byte[] page, int slot)
    {
        var fx = FixedVarRecord.ParseFixed(page, slot, out _, out _);
        long rsid = BinaryPrimitives.ReadInt64LittleEndian(fx);
        int idMajor = BinaryPrimitives.ReadInt32LittleEndian(fx[9..]);
        int idMinor = BinaryPrimitives.ReadInt32LittleEndian(fx[13..]);
        long rows = BinaryPrimitives.ReadInt64LittleEndian(fx[27..]);
        byte cmpr = fx[35];
        return new RowSet(rsid, idMajor, idMinor, rows, cmpr);
    }


    static string DecodeUtf16(byte[] page, int start, int len) => System.Text.Encoding.Unicode.GetString(page, start, len);
}
