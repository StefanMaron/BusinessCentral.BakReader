using System.Buffers.Binary;

namespace BcBak;

public sealed record AllocUnit(long Auid, byte Type, long OwnerId, byte[] FirstPage, byte[] RootPage, byte[] FirstIamPage);
public sealed record RowSet(long RowSetId, int IdMajor, int IdMinor, long Rows, byte CompressionLevel);
public sealed record SysObject(int ObjectId, string Name, string Type);
public sealed record SysColumn(int ColId, string Name, byte XType, short MaxLength, byte Precision, byte Scale);
public sealed record SysIndexCol(int IndexId, int KeyOrdinal, int ColId);

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
public sealed record PhysColumn(int ColId, bool Dropped, bool Internal, byte XType, short MaxLength, byte Precision, byte Scale,
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
public sealed class Catalog
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
            var o = ParseSysObject(page, slot);
            Objects[o.ObjectId] = o;
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
        foreach (var (page, slot) in WalkTable(SysColParsId))
        {
            int objId = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(slot + 4));
            if (objectId is { } w ? objId != w : _columnsLoadedFor.Contains(objId)) continue;
            var (_, fx, _, _, varCols) = FixedVarRecord.Parse(page, slot);
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
            int objId = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(slot + 4));
            if (objectId is { } w ? objId != w : _columnsLoadedFor.Contains(objId)) continue;
            var (_, fx, _, _, _) = FixedVarRecord.Parse(page, slot);
            int idxId = BinaryPrimitives.ReadInt32LittleEndian(fx.AsSpan(4));
            int subId = BinaryPrimitives.ReadInt32LittleEndian(fx.AsSpan(8));
            int colId = BinaryPrimitives.ReadInt32LittleEndian(fx.AsSpan(16)); // intprop
            if (!IndexColumns.TryGetValue(objId, out var list)) IndexColumns[objId] = list = new();
            list.Add(new SysIndexCol(idxId, subId, colId));
        }
        if (objectId is { } done) _columnsLoadedFor.Add(done); else _allColumnsLoaded = true;
    }

    Dictionary<long, List<PhysColumn>>? _rowsetColumns;

    /// <summary>Physical leaf layout of a rowset, in null-bit (physical) order. See <see cref="PhysColumn"/>.</summary>
    public List<PhysColumn> RowsetColumns(long rowsetId)
    {
        if (_rowsetColumns is null)
        {
            _rowsetColumns = new();
            foreach (var (page, slot) in WalkTable(SysRsColsId))
            {
                var (_, fx, _, _, _) = FixedVarRecord.Parse(page, slot);
                long rsid = BinaryPrimitives.ReadInt64LittleEndian(fx);
                uint rscolid = BinaryPrimitives.ReadUInt32LittleEndian(fx.AsSpan(8));
                uint ti = BinaryPrimitives.ReadUInt32LittleEndian(fx.AsSpan(24));
                short ordkey = BinaryPrimitives.ReadInt16LittleEndian(fx.AsSpan(32));
                uint status = BinaryPrimitives.ReadUInt32LittleEndian(fx.AsSpan(36));
                short offset = BinaryPrimitives.ReadInt16LittleEndian(fx.AsSpan(40));
                ushort nullbit = BinaryPrimitives.ReadUInt16LittleEndian(fx.AsSpan(44));
                ushort bitpos = BinaryPrimitives.ReadUInt16LittleEndian(fx.AsSpan(48));
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
                if (!_rowsetColumns.TryGetValue(rsid, out var list)) _rowsetColumns[rsid] = list = new();
                list.Add(new PhysColumn((int)(rscolid & 0x00FFFFFF), dropped, internalCol, xtype, maxLen, prec, scale, ordkey, offset, nullbit, bitpos));
            }
            foreach (var list in _rowsetColumns.Values) list.Sort((a, b) => a.NullBit.CompareTo(b.NullBit));
        }
        return _rowsetColumns.TryGetValue(rowsetId, out var cols)
            ? cols
            : throw new InvalidDataException($"no sysrscols layout for rowset {rowsetId}");
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
        var (pid, fid) = ReadPagePtr(au.FirstPage, 0);
        return WalkChain(fid, pid);
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
