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
        if (iamPid == 0) yield break;
        var iam = _pf.GetPage(iamFid, iamPid);
        if (PageHeader.Type(iam) != 10) throw new InvalidDataException("first_iam_page does not point at an IAM page");
        if (PageHeader.NextPage(iam) is not (0, _)) throw new NotSupportedException("multi-interval IAM chains not supported (database > ~4 GB)");
        var slotOffs = PageHeader.SlotOffsets(iam).ToArray();
        if (slotOffs.Length != 2) throw new InvalidDataException("unexpected IAM slot count");
        // Slot 0: IAM header incl. 8 single-page slots. In both BC demo backups every observed
        // allocation is a dedicated extent and all single-page slots are empty; verified via
        // sys.dm_db_database_page_allocations (zero mixed-extent data pages). Fail loudly otherwise:
        // any nonzero 6-byte group in the tail of slot 0 would be a single-page allocation we ignore.
        int s0 = slotOffs[0], s1 = slotOffs[1];
        for (int off = s0 + 46; off + 6 <= s1; off += 6)
            if (iam.AsSpan(off, 6).IndexOfAnyExcept((byte)0) >= 0)
                throw new NotSupportedException("IAM single-page slots in use — mixed extents not supported");
        int bitmapLen = BinaryPrimitives.ReadUInt16LittleEndian(iam.AsSpan(s1 + 2));
        for (int b = 0; b < bitmapLen; b++)
        {
            byte v = iam[s1 + 4 + b];
            if (v == 0) continue;
            for (int bit = 0; bit < 8; bit++)
            {
                if ((v & (1 << bit)) == 0) continue;
                int extent = b * 8 + bit;
                for (int pg = extent * 8; pg < extent * 8 + 8; pg++)
                {
                    if (!_pf.TryGetPage(1, pg, out var page)) continue; // allocated-but-never-written pages are absent from the backup
                    if (PageHeader.Type(page) == 1) yield return pg;
                }
            }
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
                    if (PageHeader.GhostRecords(page) != 0)
                        throw new NotSupportedException("ghost records on a compressed page — ghost detection for CD records not implemented");
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
            else if (isVar) cell = varIdx < varCols.Count ? Cell.Of(varCols[varIdx].data) : Cell.Null;
            else cell = Cell.Of(fx.AsSpan(fixedOff, Math.Min(c.MaxLength, fx.Length - fixedOff)).ToArray());
            if (isVar && idx < ncols) varIdx++; // var-offset entries exist for null interior var columns too
            if (!isVar) fixedOff += c.MaxLength;
            row[c.Name] = cell;
            idx++;
        }
        return row;
    }
}
