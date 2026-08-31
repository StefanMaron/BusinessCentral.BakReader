using System.Buffers.Binary;

namespace BcBak;

/// <summary>
/// Maps SQL Server 8 KB pages inside a native full backup (.bak) to their file offsets,
/// by parsing the backup's structure — not by trusting page self-identification.
///
/// How a full backup lays out page data (derived from the BC demo backups and validated
/// block-for-block against a fresh RESTORE of the same files — see PROVENANCE.md
/// "Data-copy layout"):
///
///  * The first MSDA/MQDA region holds every GAM-allocated extent of the data file, in
///    extent order: 8 consecutive blocks per extent. Which extents exist comes from the
///    GAM page (1:2), which is itself at a knowable position (block 2) because extent 0
///    is always allocated and leads the region. A GAM interval covers 63,904 extents;
///    files larger than one interval continue with the next interval's GAM page, whose
///    position is again knowable (the interval's first extent contains it).
///  * The second MSDA/MQDA region (if present) re-dumps the extents that changed while
///    the backup was running: extents 0 and 1 (file header, PFS, GAM, SGAM, DCM, BCM,
///    boot page), then every extent whose DCM (differential changed map, page 1:6) bit
///    is set in the region-2 image but not in the region-1 image, in extent order.
///    RESTORE applies regions in order, so region 2 supersedes region 1.
///  * Each region is padded to a 1 MB boundary with filler pseudo-pages (header bytes
///    01 65, page id 0) that RESTORE discards. The reader verifies every block beyond
///    the derived extent list is such a filler block.
///  * The MSTL/MQTL transaction-log region is NOT replayed. Validated consequence on
///    the demo backups: only allocation bookkeeping pages (PFS/GAM/DCM/boot) and a
///    handful of system-table rows written *during* the backup differ from a real
///    RESTORE; no BC table data page is affected. See PROVENANCE.md "Log region".
///
/// Page self-identification (m_pageId in the header) is deliberately not used to build
/// the map: deallocated pages keep their old headers, so a stale image elsewhere in the
/// file can carry the same page id as a live page. Resolving duplicates by scan order
/// ("last image wins") picks the stale image for such pages — measured: 20 pages wrong
/// on the BC 27.5 demo backup, 9 on 28.1. The structural map matches RESTORE on all of
/// them. Self-identification is kept as a cross-check only (<see cref="CrossCheck"/>).
/// </summary>
public sealed class PageFile : IDisposable
{
    public const int PageSize = 8192;
    /// <summary>Extents covered by one GAM page: 511,232 pages / 8. Pages-and-extents architecture guide.</summary>
    public const int GamIntervalExtents = 63904;
    public const int PagesPerExtent = 8;

    // page header field offsets (observed + confirmed via DBCC PAGE; PROVENANCE.md "Page header offsets")
    const byte PtData = 1, PtIndex = 2, PtGam = 8, PtDcm = 16, PtFileHeader = 15, PtFiller = 0x65;

    readonly FileStream _fs;
    readonly Dictionary<int, long> _map = new();       // pageId (file 1) -> file offset
    public MtfFile Mtf { get; }
    public int SupersededPageCount { get; private set; }  // pages whose region-1 image was replaced by region 2
    public int GamIntervalCount { get; private set; }

    public PageFile(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        Mtf = new MtfFile(_fs);
        BuildMap();
    }

    byte[] ReadBlock(MtfFile.DataRegion r, long block)
    {
        if (block >= r.BlockCount) throw new InvalidDataException($"block {block} beyond region ({r.BlockCount} blocks)");
        var b = new byte[PageSize];
        _fs.Seek(r.DataOffset + block * PageSize, SeekOrigin.Begin);
        _fs.ReadExactly(b);
        return b;
    }

    static bool IsFiller(ReadOnlySpan<byte> b) => b[0] == 1 && b[1] == PtFiller;

    static void ExpectPage(ReadOnlySpan<byte> b, int pageId, byte type, string what)
    {
        int pid = BinaryPrimitives.ReadInt32LittleEndian(b[32..]);
        int fid = BinaryPrimitives.ReadUInt16LittleEndian(b[36..]);
        if (b[0] != 1 || b[1] != type || pid != pageId || fid != 1)
            throw new InvalidDataException(
                $"expected {what} (page 1:{pageId}, type {type}) but found headerVersion={b[0]} type={b[1]} page {fid}:{pid} — backup layout differs from every observed file, refusing to guess");
    }

    /// <summary>
    /// Extract the extent bitmap of a GAM/SGAM/DCM/BCM page: two slots, slot 1 =
    /// [2 status bytes][u16 bitmap byte length][bitmap], LSB-first (bit n of byte k =
    /// extent 8k+n relative to the page's interval). Same record shape as IAM bitmaps;
    /// derived from the files and validated via the restore comparison (PROVENANCE.md).
    /// </summary>
    static byte[] AllocBitmap(byte[] page)
    {
        int slotCnt = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(22));
        if (slotCnt != 2) throw new InvalidDataException($"allocation page with {slotCnt} slots (expected 2)");
        int s1 = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(PageSize - 4));
        int len = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(s1 + 2));
        return page.AsSpan(s1 + 4, len).ToArray();
    }

    static bool BitSet(byte[] bmp, int i) => (bmp[i / 8] & (1 << (i % 8))) != 0;

    void BuildMap()
    {
        if (Mtf.MqdaRegions.Count > 2)
            throw new NotSupportedException($"{Mtf.MqdaRegions.Count} MQDA data regions — only the full-copy + changed-extent-re-read shape (1 or 2 regions) has been derived and validated");
        var r0 = Mtf.MqdaRegions[0];

        // --- Region 0: GAM-driven extent walk, one interval at a time ---
        long cursor = 0;
        var interval0Dcm = Array.Empty<byte>();
        for (int interval = 0; ; interval++)
        {
            long intervalFirstPage = (long)interval * GamIntervalExtents * PagesPerExtent;
            if (intervalFirstPage + 2 > int.MaxValue)
                throw new NotSupportedException("file exceeds the int32 page-id range");
            // The interval's first extent holds its GAM page and is always allocated, so it
            // leads this interval's contribution: GAM = block cursor+2.
            var gamPage = ReadBlock(r0, cursor + 2);
            ExpectPage(gamPage, (int)intervalFirstPage + 2, PtGam, $"GAM page of interval {interval}");
            var gam = AllocBitmap(gamPage);
            if (interval == 0)
            {
                var dcmPage = ReadBlock(r0, cursor + 6);
                ExpectPage(dcmPage, 6, PtDcm, "DCM page");
                interval0Dcm = AllocBitmap(dcmPage);
            }
            for (int e = 0; e < GamIntervalExtents; e++)
            {
                if (e < gam.Length * 8 && BitSet(gam, e)) continue; // GAM bit set = extent free = not in the stream
                if (cursor + PagesPerExtent > r0.BlockCount)
                    throw new InvalidDataException("GAM-allocated extents exceed the data region — backup layout differs from the derived model");
                long firstPage = intervalFirstPage + (long)e * PagesPerExtent;
                for (int p = 0; p < PagesPerExtent; p++)
                    _map[(int)(firstPage + p)] = r0.DataOffset + (cursor + p) * PageSize;
                cursor += PagesPerExtent;
            }
            GamIntervalCount = interval + 1;
            if (cursor >= r0.BlockCount) break;
            var peek = ReadBlock(r0, cursor);
            if (IsFiller(peek)) break;        // rest of the region is 1 MB-boundary padding
            // otherwise the file spans another GAM interval; loop verifies its GAM page
        }
        VerifyFillerTail(r0, cursor);

        // --- Region 1 (optional): extents 0,1 + extents whose DCM bit appeared during the backup ---
        if (Mtf.MqdaRegions.Count == 2)
        {
            if (GamIntervalCount > 1)
                throw new NotSupportedException("changed-extent re-read section on a multi-GAM-interval file — this shape has not been observed and its layout is not derived; refusing to guess");
            var r1 = Mtf.MqdaRegions[1];
            var dcm1Page = ReadBlock(r1, 6);
            ExpectPage(dcm1Page, 6, PtDcm, "DCM page in re-read region");
            var dcm1 = AllocBitmap(dcm1Page);
            var extents = new List<int> { 0, 1 };
            int maxE = Math.Max(interval0Dcm.Length, dcm1.Length) * 8;
            for (int e = 2; e < Math.Min(maxE, GamIntervalExtents); e++)
                if (e < dcm1.Length * 8 && BitSet(dcm1, e) && !(e < interval0Dcm.Length * 8 && BitSet(interval0Dcm, e)))
                    extents.Add(e);
            long c1 = 0;
            foreach (int e in extents)
            {
                if (c1 + PagesPerExtent > r1.BlockCount)
                    throw new InvalidDataException("changed-extent list exceeds the re-read region — backup layout differs from the derived model");
                for (int p = 0; p < PagesPerExtent; p++)
                {
                    int pid = e * PagesPerExtent + p;
                    if (_map.ContainsKey(pid)) SupersededPageCount++;
                    _map[pid] = r1.DataOffset + (c1 + p) * PageSize;
                }
                c1 += PagesPerExtent;
            }
            VerifyFillerTail(r1, c1);
        }
    }

    void VerifyFillerTail(MtfFile.DataRegion r, long from)
    {
        for (long b = from; b < r.BlockCount; b++)
        {
            var blk = ReadBlock(r, b);
            if (!IsFiller(blk))
                throw new InvalidDataException(
                    $"block {b} of {r.Dblk} region is neither mapped by the derived extent list nor padding filler — backup layout differs from the derived model, refusing to guess");
        }
    }

    public int PageCount => _map.Count;

    /// <summary>PFS interval: one PFS page per 8088 pages (pages-and-extents architecture guide).</summary>
    public const int PfsInterval = 8088;
    readonly Dictionary<int, byte[]> _pfsCache = new();

    /// <summary>
    /// Whether a page is individually allocated per the PFS byte map. IAM/GAM bits track
    /// whole extents; pages inside an extent can be individually deallocated and then keep
    /// a stale image in the backup, so readers must filter on this bit. PFS layout: page 1:1
    /// covers pages 0..8087, then one PFS page every 8088 pages; single record at slot 0,
    /// byte array at record+4, one byte per page, bit 0x40 = allocated. Derived from the
    /// files and validated for every page of both demo databases against
    /// sys.dm_db_database_page_allocations (PROVENANCE.md "PFS pages").
    /// </summary>
    public bool IsPageAllocated(int pageId)
    {
        int intervalBase = pageId / PfsInterval * PfsInterval;
        if (!_pfsCache.TryGetValue(intervalBase, out var data))
        {
            int pfsPid = intervalBase == 0 ? 1 : intervalBase;
            var pfs = GetPage(1, pfsPid);
            if (pfs[1] != 11) throw new InvalidDataException($"page 1:{pfsPid} is not a PFS page (type {pfs[1]})");
            int s0 = BinaryPrimitives.ReadUInt16LittleEndian(pfs.AsSpan(PageSize - 2));
            data = pfs.AsSpan(s0 + 4, PfsInterval).ToArray();
            _pfsCache[intervalBase] = data;
        }
        return (data[pageId - intervalBase] & 0x40) != 0;
    }

    public bool TryGetPage(int fileId, int pageId, out byte[] page)
    {
        if (fileId != 1)
            throw new NotSupportedException($"page reference to file {fileId}: only single-data-file databases are supported (BC databases use one data file)");
        if (_map.TryGetValue(pageId, out long off))
        {
            page = new byte[PageSize];
            _fs.Seek(off, SeekOrigin.Begin);
            _fs.ReadExactly(page);
            return true;
        }
        page = Array.Empty<byte>();
        return false;
    }

    public byte[] GetPage(int fileId, int pageId)
        => TryGetPage(fileId, pageId, out var p)
            ? p
            : throw new InvalidDataException($"page ({fileId}:{pageId}) is not an allocated page of this backup");

    /// <summary>
    /// Cross-check the structural map against page self-identification, the empirical
    /// method the map replaced. Scans every block of every data region. Returns per-class
    /// counts; disagreements where a *later* self-identified image would have won the
    /// old "last image wins" rule are the cases that rule got wrong.
    /// </summary>
    public (long agree, long staleHeaders, long unidentified, List<int> lastWinsDisagreements) CrossCheck()
    {
        long agree = 0, stale = 0, unident = 0;
        var lastWins = new Dictionary<int, long>();  // pid -> offset per "last valid self-id image wins"
        var known = new HashSet<byte> { 1, 2, 3, 4, 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19, 20 };
        foreach (var r in Mtf.MqdaRegions)
        {
            for (long b = 0; b < r.BlockCount; b++)
            {
                var blk = ReadBlock(r, b);
                if (blk[0] == 1 && known.Contains(blk[1]))
                {
                    int pid = BinaryPrimitives.ReadInt32LittleEndian(blk.AsSpan(32));
                    int fid = BinaryPrimitives.ReadUInt16LittleEndian(blk.AsSpan(36));
                    if (fid == 1 && pid >= 0)
                    {
                        long off = r.DataOffset + b * PageSize;
                        lastWins[pid] = off;
                        if (_map.TryGetValue(pid, out long m) && m == off) agree++;
                        else stale++;
                        continue;
                    }
                }
                unident++;
            }
        }
        var disagreements = lastWins
            .Where(kv => _map.TryGetValue(kv.Key, out long m) && m != kv.Value)
            .Select(kv => kv.Key).OrderBy(x => x).ToList();
        return (agree, stale, unident, disagreements);
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>Field accessors for the 96-byte page header (offsets observed + confirmed via DBCC PAGE, see PROVENANCE.md).</summary>
public static class PageHeader
{
    public static byte Type(ReadOnlySpan<byte> p) => p[1];
    public static byte TypeFlagBits(ReadOnlySpan<byte> p) => p[2];   // 0x80 = compression info (CI) present
    public static byte Level(ReadOnlySpan<byte> p) => p[3];
    public static ushort SlotCount(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt16LittleEndian(p[22..]);
    public static (int pageId, int fileId) NextPage(ReadOnlySpan<byte> p)
        => (BinaryPrimitives.ReadInt32LittleEndian(p[16..]), BinaryPrimitives.ReadUInt16LittleEndian(p[20..]));
    public static ushort GhostRecords(ReadOnlySpan<byte> p) => BinaryPrimitives.ReadUInt16LittleEndian(p[58..]);
    public static IEnumerable<int> SlotOffsets(byte[] p)
    {
        int n = SlotCount(p);
        for (int s = 0; s < n; s++)
            yield return BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(PageFile.PageSize - 2 * (s + 1)));
    }
}
