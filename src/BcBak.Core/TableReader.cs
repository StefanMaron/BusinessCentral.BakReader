using System.Buffers.Binary;

namespace BcBak;

/// <summary>
/// Enumerates the data pages of a table via its IAM chain and decodes rows.
///
/// Why IAM and not first_page/root_page: on the BC demo backups both sysallocunits.pgfirst
/// and the page-header object id can be stale — the demo database was shrunk after load,
/// which relocates pages without rewriting either (observed: the No. Series "first page"
/// per metadata belongs to sysobjvalues; the real data page carries a foreign m_objId).
/// The IAM bitmap is what SQL Server itself trusts, and matched
/// sys.dm_db_database_page_allocations exactly. See PROVENANCE.md.
/// </summary>
public sealed class TableReader
{
    readonly PageFile _pf;
    readonly Catalog _cat;

    public TableReader(PageFile pf, Catalog cat) { _pf = pf; _cat = cat; }

    public IEnumerable<int> DataPages(AllocUnit au)
    {
        var (iamPid, iamFid) = Catalog.ReadPagePtr(au.FirstIamPage, 0);
        // One IAM page per GAM interval, chained via the page header's next-page pointer.
        var seen = new HashSet<(int, int)>();
        while (iamPid != 0 && seen.Add((iamFid, iamPid)))
        {
            var iam = _pf.GetPage(iamFid, iamPid);
            if (PageHeader.Type(iam) != 10) throw new InvalidDataException($"page {iamFid}:{iamPid} in the IAM chain is not an IAM page");
            var slotOffs = PageHeader.SlotOffsets(iam).ToArray();
            if (slotOffs.Length != 2) throw new InvalidDataException("unexpected IAM slot count");
            int s0 = slotOffs[0], s1 = slotOffs[1];
            // Slot 0: IAM header. start_pg (the GAM-interval base this IAM's bitmap is
            // relative to) is a 6-byte page pointer at +40; eight 6-byte single-page
            // allocation slots (mixed-extent pages) follow at +46. Derived from a database
            // with mixed-page allocation enabled, field positions confirmed against
            // DBCC PAGE's IAM annotations (PROVENANCE.md "IAM pages").
            var (basePid, baseFid) = Catalog.ReadPagePtr(iam, s0 + 40);
            if (baseFid != 0 && baseFid != 1)
                throw new NotSupportedException($"IAM interval base in file {baseFid} — only single-data-file databases are supported");
            if (basePid % PageFile.GamIntervalPages != 0)
                throw new InvalidDataException($"IAM interval base {basePid} is not a GAM-interval boundary");
            for (int sp = 0; sp < 8; sp++)
            {
                var (spPid, spFid) = Catalog.ReadPagePtr(iam, s0 + 46 + 6 * sp);
                if (spPid == 0 && spFid == 0) continue;
                if (spFid != 1)
                    throw new NotSupportedException($"IAM single-page slot points into file {spFid} — only single-data-file databases are supported");
                if (!_pf.IsPageAllocated(spPid)) continue;
                var page = _pf.GetPage(1, spPid);
                if (PageHeader.Type(page) == 1) yield return spPid;
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
                    // The bitmap is 7992 bytes (63,936 bits) but a GAM interval is 63,904
                    // extents; bits in the 32-bit overhang are not extents (observed: bits
                    // 63920/63925/63928/63933 are set on every IAM of the demo databases).
                    if (extent >= PageFile.GamIntervalExtents) continue;
                    for (int pg = basePid + extent * 8; pg < basePid + extent * 8 + 8; pg++)
                    {
                        // The IAM bit covers the whole extent; individual pages can be deallocated
                        // (and then hold a stale image). The PFS allocation bit is the per-page truth
                        // (see PageFile.IsPageAllocated). A PFS-allocated page of a mapped extent is
                        // always present in the structural map, so absence is an error.
                        if (!_pf.IsPageAllocated(pg)) continue;
                        var page = _pf.GetPage(1, pg);
                        byte pt = PageHeader.Type(page);
                        if (pt == 1) yield return pg;
                        // 2/10: index and IAM pages of the same object share its extents.
                        // 8/9/11/16/17: GAM/SGAM/PFS/DCM/BCM pages recur at fixed intervals
                        // through the whole file and can sit inside an extent the IAM claims
                        // (measured on a 23 GB production database, where PFS pages every
                        // 8088 pages fall inside ordinary table extents once mixed-page
                        // allocation is off). They are never table data — skip them.
                        else if (pt is not (2 or 10 or 8 or 9 or 11 or 16 or 17))
                            throw new InvalidDataException($"page 1:{pg} in a data extent has unexpected type {pt}");
                    }
                }
            }
            (iamPid, iamFid) = PageHeader.NextPage(iam);
        }
    }

    /// <summary>
    /// The physical leaf layout of the table's clustered rowset (sysrscols), joined to
    /// syscolpars for names. Physical (null-bit) order governs both the compressed (CD)
    /// column array and the FixedVar layout; on tables with ALTER history it differs
    /// from declaration order and contains dropped columns that still hold slots.
    /// </summary>
    public List<(PhysColumn Phys, SysColumn? Col)> PhysicalColumns(int objectId, long rowsetId)
    {
        var names = _cat.Columns[objectId].ToDictionary(c => c.ColId);
        var result = new List<(PhysColumn, SysColumn?)>();
        foreach (var pc in _cat.RowsetColumns(rowsetId))
        {
            if (pc.Dropped) { result.Add((pc, null)); continue; }
            if (pc.ColId == 0) { result.Add((pc, null)); continue; } // uniquifier: internal, valueless for us
            if (!names.TryGetValue(pc.ColId, out var sc))
                throw new InvalidDataException($"rowset column {pc.ColId} (type {SqlTypes.Name(pc.XType)}) has no syscolpars entry — schema mismatch, refusing to guess");
            // The physical record is authoritative for storage width/precision; keep the
            // syscolpars name but the sysrscols type facts.
            result.Add((pc, sc with { XType = pc.XType, MaxLength = pc.XType is 106 or 108 ? sc.MaxLength : pc.MaxLength, Precision = pc.Precision != 0 ? pc.Precision : sc.Precision, Scale = pc.Scale != 0 ? pc.Scale : sc.Scale }));
        }
        return result;
    }

    public IEnumerable<Dictionary<string, Cell>> ReadRows(int objectId)
    {
        var rowset = _cat.RowsetFor(objectId, 1, 0);
        var au = _cat.AuForRowset(rowset.RowSetId);
        var phys = PhysicalColumns(objectId, rowset.RowSetId);
        foreach (var pid in DataPages(au))
        {
            var page = _pf.GetPage(1, pid);
            Cell[]? anchors = null; List<byte[]>? dict = null;
            if ((PageHeader.TypeFlagBits(page) & 0x80) != 0)
                (anchors, dict) = CdRecord.ParseCi(page);
            foreach (var so in PageHeader.SlotOffsets(page))
            {
                // A slot array entry of 0 is an empty slot (deleted / never completed):
                // SQL Server's own scan skips it and DBCC PAGE renders nothing for it.
                // Measured on a production heap, where such a slot would otherwise read
                // the page header bytes as a record. Anything else below the 96-byte
                // header is corruption.
                if (so == 0) continue;
                if (so < 96) throw new InvalidDataException($"slot offset {so} inside the page header — page corrupt?");
                Dictionary<string, Cell> row;
                if (CdRecord.IsCd(page, so))
                {
                    if (CdRecord.IsGhost(page, so)) continue; // deleted, not yet cleaned up
                    var cells = CdRecord.Parse(page, so, anchors, dict);
                    // A CD record written before columns were added carries fewer entries;
                    // they map onto the first entries of the physical order, and the
                    // missing trailing columns read as NULL (same versioning as the
                    // FixedVar column count; validated on an ALTERed page-compressed probe).
                    if (cells.Length > phys.Count)
                        throw new InvalidDataException($"compressed record has {cells.Length} columns, sysrscols says only {phys.Count}");
                    row = new Dictionary<string, Cell>();
                    for (int i = 0; i < phys.Count; i++)
                        if (phys[i].Col is { } col) row[col.Name] = i < cells.Length ? cells[i] : Cell.Null;
                }
                else
                {
                    int rt = FixedVarRecord.RecordType(page, so);
                    if (rt is 5 or 6 or 7) continue;
                    if (rt != 0) throw new NotSupportedException($"record type {rt} not supported");
                    row = DecodeFixedVar(page, so, phys);
                }
                yield return row;
            }
        }
    }

    /// <summary>
    /// Uncompressed rows, decoded strictly by the sysrscols leaf layout: fixed columns at
    /// their recorded offsets (bit columns by recorded bit position), variable columns by
    /// their recorded ordinals. A column whose null bit lies beyond the record's column
    /// count was added after the row was written and reads as NULL; a variable column
    /// beyond the record's variable count is a trimmed trailing empty value.
    /// </summary>
    Dictionary<string, Cell> DecodeFixedVar(byte[] page, int slot, List<(PhysColumn Phys, SysColumn? Col)> phys)
    {
        var (_, fx, ncols, nullBmp, varCols) = FixedVarRecord.Parse(page, slot);
        var row = new Dictionary<string, Cell>();
        foreach (var (pc, col) in phys)
        {
            if (col is null) continue; // dropped or uniquifier: physical slot without a value for us
            Cell cell;
            if (pc.NullBit > ncols) cell = Cell.Null;                       // column added after this row was written
            else if (FixedVarRecord.IsNull(nullBmp, pc.NullBit - 1)) cell = Cell.Null;
            else if (pc.IsVar)
                cell = pc.VarOrdinal <= varCols.Count
                    ? (varCols[pc.VarOrdinal - 1].complex ? Cell.OfComplex(varCols[pc.VarOrdinal - 1].data) : Cell.Of(varCols[pc.VarOrdinal - 1].data))
                    : Cell.Of(Array.Empty<byte>());
            else
            {
                int off = pc.LeafOffset - 4; // leaf offsets are from the record start; fx starts past the 4-byte header
                if (pc.XType == 104)
                {
                    if (off >= fx.Length) throw new InvalidDataException($"bit column {col.Name} at offset {pc.LeafOffset} beyond fixed data");
                    cell = Cell.Of(new[] { (byte)((fx[off] >> pc.BitPos) & 1) });
                }
                else
                {
                    int width = pc.MaxLength;
                    if (off + width > fx.Length)
                        throw new InvalidDataException($"fixed data ends inside column {col.Name} ({fx.Length - off} of {width} bytes) — schema/record mismatch, refusing to guess");
                    cell = Cell.Of(fx.AsSpan(off, width).ToArray());
                }
            }
            row[col.Name] = cell;
        }
        return row;
    }
}
