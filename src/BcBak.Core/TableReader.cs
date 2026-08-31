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
                        else if (pt is not (2 or 10))
                            throw new InvalidDataException($"page 1:{pg} in a data extent has unexpected type {pt}");
                    }
                }
            }
            (iamPid, iamFid) = PageHeader.NextPage(iam);
        }
    }

    /// <summary>
    /// Physical column order inside compressed records: clustered-index key columns first
    /// (sysiscols order), then the remaining columns in column-id order. Derived and validated
    /// against SELECT output for No. Series and Customer (see PROVENANCE.md).
    /// </summary>
    public List<SysColumn> PhysicalColumnOrder(int objectId)
    {
        var cols = _cat.Columns[objectId];
        var keyIds = (_cat.IndexColumns.TryGetValue(objectId, out var ics) ? ics : new())
            .Where(ic => ic.IndexId == 1).OrderBy(ic => ic.KeyOrdinal).Select(ic => ic.ColId).ToList();
        var byId = cols.ToDictionary(c => c.ColId);
        var order = new List<SysColumn>();
        foreach (var k in keyIds) order.Add(byId[k]);
        foreach (var c in cols) if (!keyIds.Contains(c.ColId)) order.Add(c);
        return order;
    }

    public IEnumerable<Dictionary<string, Cell>> ReadRows(int objectId)
    {
        var rowset = _cat.RowsetFor(objectId, 1, 0);
        var au = _cat.AuForRowset(rowset.RowSetId);
        var physOrder = PhysicalColumnOrder(objectId);
        var declOrder = _cat.Columns[objectId];
        foreach (var pid in DataPages(au))
        {
            var page = _pf.GetPage(1, pid);
            Cell[]? anchors = null; List<byte[]>? dict = null;
            if ((PageHeader.TypeFlagBits(page) & 0x80) != 0)
                (anchors, dict) = CdRecord.ParseCi(page);
            foreach (var so in PageHeader.SlotOffsets(page))
            {
                var row = new Dictionary<string, Cell>();
                if (CdRecord.IsCd(page, so))
                {
                    if (CdRecord.IsGhost(page, so)) continue; // deleted, not yet cleaned up
                    var cells = CdRecord.Parse(page, so, anchors, dict);
                    if (cells.Length != physOrder.Count)
                        throw new InvalidDataException($"record has {cells.Length} columns, catalog says {physOrder.Count}");
                    for (int i = 0; i < cells.Length; i++) row[physOrder[i].Name] = cells[i];
                }
                else
                {
                    int rt = FixedVarRecord.RecordType(page, so);
                    if (rt is 5 or 6 or 7) continue;
                    if (rt != 0) throw new NotSupportedException($"record type {rt} not supported");
                    row = DecodeFixedVar(page, so, declOrder);
                }
                yield return row;
            }
        }
    }

    /// <summary>
    /// Uncompressed rows: fixed-length columns in declaration order inside the fixed data,
    /// variable-length columns in declaration order in the variable section. This matches the
    /// system base tables (validated); for user heaps it is an assumption that the physical
    /// order equals column-id order (true unless columns were dropped/altered — the BC demo
    /// databases are created in one pass).
    /// </summary>
    Dictionary<string, Cell> DecodeFixedVar(byte[] page, int slot, List<SysColumn> cols)
    {
        var (_, fx, ncols, nullBmp, varCols) = FixedVarRecord.Parse(page, slot);
        var row = new Dictionary<string, Cell>();
        int fixedOff = 0, varIdx = 0, idx = 0;
        foreach (var c in cols)
        {
            bool isVar = SqlTypes.IsVariableLength(c.XType);
            Cell cell;
            if (idx >= ncols) cell = Cell.Null;
            else if (FixedVarRecord.IsNull(nullBmp, idx)) cell = Cell.Null;
            // A trailing empty variable-length column can be omitted from the record's
            // variable section entirely; NULL is signalled via the null bitmap only.
            else if (isVar) cell = varIdx < varCols.Count
                ? (varCols[varIdx].complex ? Cell.OfComplex(varCols[varIdx].data) : Cell.Of(varCols[varIdx].data))
                : Cell.Of(Array.Empty<byte>());
            else if (fx.Length - fixedOff < c.MaxLength)
                throw new InvalidDataException($"fixed data ends inside column {c.Name} ({fx.Length - fixedOff} of {c.MaxLength} bytes) — schema/record mismatch, refusing to guess");
            else cell = Cell.Of(fx.AsSpan(fixedOff, c.MaxLength).ToArray());
            if (isVar && idx < ncols) varIdx++; // var-offset entries exist for null interior var columns too
            if (!isVar) fixedOff += c.MaxLength;
            row[c.Name] = cell;
            idx++;
        }
        return row;
    }
}
