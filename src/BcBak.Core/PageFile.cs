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
    public const int GamIntervalPages = GamIntervalExtents * PagesPerExtent;
    public const int PagesPerExtent = 8;

    // page header field offsets (observed + confirmed via DBCC PAGE; PROVENANCE.md "Page header offsets")
    const byte PtGam = 8, PtSgam = 9, PtDcm = 16, PtFileHeader = 15, PtFiller = 0x65;
    public int FilePages { get; private set; }

    readonly Microsoft.Win32.SafeHandles.SafeFileHandle _fh;
    readonly long _fileLength;
    readonly Dictionary<int, long> _map = new();       // pageId (file 1) -> file offset
    public MtfFile Mtf { get; }
    public int SupersededPageCount { get; private set; }  // pages whose region-1 image was replaced by region 2
    public int GamIntervalCount { get; private set; }

    /// <summary>
    /// Opens the backup for positional (pread) access: every read fetches exactly the
    /// bytes it needs. A buffered stream amplified the scattered 8 KB page reads by its
    /// buffer size — a cold open read ~195 MB of a 893 MB file for a few MB of bitmap
    /// and catalog pages. With <paramref name="prefetch"/> a background thread reads the
    /// whole file sequentially once to populate the OS page cache — worthwhile when many
    /// tables will be read (sequential cold read of the demo backup: ~0.4 s; the same
    /// bytes fetched page-by-page cost more), waste for a session reading two tables.
    /// </summary>
    public PageFile(string path, bool prefetch = false)
    {
        _fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        _fileLength = RandomAccess.GetLength(_fh);
        if (prefetch)
        {
            var t = new Thread(() =>
            {
                try
                {
                    using var fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
                    var buf = new byte[4 << 20];
                    long len = RandomAccess.GetLength(fh);
                    for (long off = 0; off < len; off += buf.Length)
                        if (RandomAccess.Read(fh, buf, off) == 0) break;
                }
                catch { /* prefetch is best-effort: the real reads carry the errors */ }
            }) { IsBackground = true, Name = "bcbak-prefetch" };
            t.Start();
        }
        Mtf = new MtfFile(_fh, _fileLength);
        BuildMap();
    }

    void ReadAt(long offset, Span<byte> buf)
    {
        int n = 0;
        while (n < buf.Length)
        {
            int r = RandomAccess.Read(_fh, buf[n..], offset + n);
            if (r == 0) throw new EndOfStreamException($"unexpected end of file at offset 0x{offset + n:x}");
            n += r;
        }
    }

    byte[] ReadBlock(MtfFile.DataRegion r, long block)
    {
        if (block >= r.BlockCount) throw new InvalidDataException($"block {block} beyond region ({r.BlockCount} blocks)");
        var b = new byte[PageSize];
        ReadAt(r.DataOffset + block * PageSize, b);
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

        // Bootstrap: extent 0 always leads region 1, so block 0 is the file header page.
        var hdrPage = ReadBlock(r0, 0);
        ExpectPage(hdrPage, 0, PtFileHeader, "file header page");
        // The file header data is a FixedVar record at page offset 96 whose field count
        // varies with the SQL Server version that wrote the file (observed: 56 and 60
        // fields). "Size" (in pages) is variable-length column 4 — validated on five
        // backups with known sizes across versions, field name from DBCC PAGE.
        var (_, _, _, _, hdrCols) = FixedVarRecord.Parse(hdrPage, 96);
        if (hdrCols.Count < 5 || hdrCols[4].data.Length < 4)
            throw new InvalidDataException($"file header record has {hdrCols.Count} variable columns — Size field not where every observed layout puts it, refusing to guess");
        FilePages = BinaryPrimitives.ReadInt32LittleEndian(hdrCols[4].data);
        if (FilePages <= 0 || FilePages % PagesPerExtent != 0)
            throw new InvalidDataException($"file header Size field reads {FilePages} pages — not a positive multiple of 8, refusing to guess");

        // --- Region 0: the full copy. Per GAM interval, in extent order, the stream holds
        // every extent that is GAM-allocated, contains a PFS page, is the interval's first
        // extent (GAM/SGAM/DCM/BCM live there), or is SGAM-marked mixed-with-free-pages.
        long cursor = 0;
        int intervals = (FilePages + GamIntervalPages - 1) / GamIntervalPages;
        for (int k = 0; k < intervals; k++)
        {
            long leadBlock = cursor;
            // Interval 0: GAM at page 2 / SGAM at 3 (pages 0,1 are file header and first PFS).
            // Interval k>0: GAM at the interval's first page, SGAM next (observed on a
            // two-interval file; DBCC-confirmed types 8/9 at 511232/511233).
            int gamPid = k == 0 ? 2 : k * GamIntervalPages;
            var gamPage = ReadBlock(r0, leadBlock + (k == 0 ? 2 : 0));
            ExpectPage(gamPage, gamPid, PtGam, $"GAM page of interval {k}");
            var sgamPage = ReadBlock(r0, leadBlock + (k == 0 ? 3 : 1));
            ExpectPage(sgamPage, gamPid + 1, PtSgam, $"SGAM page of interval {k}");
            var dcmPage = ReadBlock(r0, leadBlock + 6);
            ExpectPage(dcmPage, k * GamIntervalPages + 6, PtDcm, $"DCM page of interval {k}");
            var gam = AllocBitmap(gamPage);
            var sgam = AllocBitmap(sgamPage);
            for (int e = 0; e < GamIntervalExtents; e++)
            {
                long firstPage = (long)k * GamIntervalPages + (long)e * PagesPerExtent;
                if (firstPage >= FilePages) break;
                bool inStream =
                    e == 0                                              // interval lead extent
                    || !(e < gam.Length * 8 && BitSet(gam, e))          // GAM bit clear = allocated
                    || (e < sgam.Length * 8 && BitSet(sgam, e))         // mixed extent with free pages
                    || ContainsPfsPage(firstPage);                      // PFS pages are always copied
                if (!inStream) continue;
                if (cursor + PagesPerExtent > r0.BlockCount)
                    throw new InvalidDataException("derived extent list exceeds the data region — backup layout differs from the derived model");
                for (int p = 0; p < PagesPerExtent; p++)
                    _map[(int)(firstPage + p)] = r0.DataOffset + (cursor + p) * PageSize;
                cursor += PagesPerExtent;
            }
        }
        GamIntervalCount = intervals;
        VerifyFillerTail(r0, cursor);

        // --- Region 2 (optional): the extents that changed while the backup ran, written
        // as 8-block extent frames in ascending extent order. WHICH extents is not
        // recorded in any single on-disk structure: the DCM diff under-lists them (an
        // extent flagged before the backup that changes again during it is re-read with
        // no bit changing — measured on a production backup), and per-block page-header
        // identification over-trusts content (live pages of some internal types carry
        // another page's id in their header — measured on the BC 28.1 demo backup, where
        // RESTORE placed such a frame by position, not by the headers). The frame extent
        // is therefore chosen by consensus of frame-aligned page headers, constrained by
        // (a) strictly ascending frame order and (b) membership in the interval's FINAL
        // DCM image (read from the interval's lead-extent frame in this region) or being
        // a lead extent itself. Exactly one candidate may satisfy both — anything else
        // fails loudly. The whole 8-block frame is mapped (RESTORE writes whole frames,
        // including slots without readable headers). Validated byte-for-byte against
        // fresh RESTOREs of five backups (PROVENANCE.md "Re-read region").
        if (Mtf.MqdaRegions.Count == 2)
        {
            var r1 = Mtf.MqdaRegions[1];
            long c1 = 0;
            long prevExtent = -1;
            int currentInterval = -1;
            byte[]? dcmFinal = null;
            while (c1 + PagesPerExtent <= r1.BlockCount)
            {
                // whole 64 KB extent frame in one positional read
                var frameBuf = new byte[PagesPerExtent * PageSize];
                ReadAt(r1.DataOffset + c1 * PageSize, frameBuf);
                var frame = new byte[PagesPerExtent][];
                for (int p = 0; p < PagesPerExtent; p++) frame[p] = frameBuf.AsSpan(p * PageSize, PageSize).ToArray();
                var candidates = new HashSet<long>();
                for (int p = 0; p < PagesPerExtent; p++)
                {
                    var pg = frame[p];
                    if (pg[0] != 1 || !KnownPageTypes.Contains(pg[1])) continue;
                    int pid = BinaryPrimitives.ReadInt32LittleEndian(pg.AsSpan(32));
                    int fid = BinaryPrimitives.ReadUInt16LittleEndian(pg.AsSpan(36));
                    if (fid != 1 || pid < 0 || pid >= FilePages) continue;
                    if (pid % PagesPerExtent != p) continue; // not frame-aligned: no vote
                    candidates.Add(pid / PagesPerExtent);
                }
                if (candidates.Count == 0)
                {
                    if (frame.All(pg => IsFiller(pg))) break; // trailer padding
                    throw new InvalidDataException($"re-read frame at block {c1} has no frame-aligned page identity and is not padding — cannot place, refusing to guess");
                }
                var valid = new List<long>();
                foreach (long e in candidates)
                {
                    if (e <= prevExtent) continue;
                    int iv = (int)(e / GamIntervalExtents);
                    bool isLead = (iv == 0 && e <= 1) || e == (long)iv * GamIntervalExtents;
                    bool inDcm = iv == currentInterval && dcmFinal != null
                        && (e - (long)iv * GamIntervalExtents) < (long)dcmFinal.Length * 8
                        && BitSet(dcmFinal, (int)(e - (long)iv * GamIntervalExtents));
                    if (isLead || inDcm) valid.Add(e);
                }
                if (valid.Count != 1)
                    throw new InvalidDataException($"re-read frame at block {c1}: {valid.Count} placement candidates ({string.Join(",", candidates)}) survive the ascending and changed-map constraints — refusing to guess");
                long extent = valid[0];
                int interval = (int)(extent / GamIntervalExtents);
                if (interval != currentInterval)
                {
                    // First frame of an interval is its lead extent; its slot 6 carries the
                    // interval's final DCM image, needed to admit that interval's other frames.
                    if (extent != (long)interval * GamIntervalExtents)
                        throw new InvalidDataException($"re-read section of interval {interval} does not start with its lead extent — refusing to guess");
                    var dcmPage = frame[6];
                    ExpectPage(dcmPage, interval * GamIntervalPages + 6, PtDcm, $"final DCM page of interval {interval}");
                    dcmFinal = AllocBitmap(dcmPage);
                    currentInterval = interval;
                }
                for (int p = 0; p < PagesPerExtent; p++)
                {
                    int pid = (int)(extent * PagesPerExtent + p);
                    if (_map.ContainsKey(pid)) SupersededPageCount++;
                    _map[pid] = r1.DataOffset + (c1 + p) * PageSize;
                }
                prevExtent = extent;
                c1 += PagesPerExtent;
            }
            VerifyFillerTail(r1, c1);
        }
    }

    static readonly HashSet<byte> KnownPageTypes = new() { 1, 2, 3, 4, 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19, 20 };

    static bool ContainsPfsPage(long extentFirstPage)
    {
        for (int p = 0; p < PagesPerExtent; p++)
            if ((extentFirstPage + p) % PfsInterval == 0) return true;
        return false;
    }

    void VerifyFillerTail(MtfFile.DataRegion r, long from)
    {
        // The tail is contiguous: scan it in large chunks, one filler check per block.
        var chunk = new byte[256 * PageSize];
        for (long b = from; b < r.BlockCount; )
        {
            int blocks = (int)Math.Min(chunk.Length / PageSize, r.BlockCount - b);
            ReadAt(r.DataOffset + b * PageSize, chunk.AsSpan(0, blocks * PageSize));
            for (int i = 0; i < blocks; i++, b++)
                if (!IsFiller(chunk.AsSpan(i * PageSize)))
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
            ReadAt(off, page);
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
    /// <summary>
    /// Compare every mapped page byte-for-byte against a restored copy of the same
    /// backup (the validation oracle). Returns counts and the body-differing page ids.
    /// Pages the restore process itself rewrites (allocation bitmaps, boot, PFS,
    /// log-redo targets, version-upgrade writes) show up as diffs; everything else
    /// must match exactly.
    /// </summary>
    public (long exact, long headerOnly, List<int> bodyDiff) CompareAgainst(string mdfPath)
    {
        using var mdf = new FileStream(mdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long exact = 0, hdr = 0;
        var body = new List<int>();
        var a = new byte[PageSize];
        var b = new byte[PageSize];
        foreach (var (pid, off) in _map.OrderBy(kv => kv.Key))
        {
            ReadAt(off, a);
            mdf.Seek((long)pid * PageSize, SeekOrigin.Begin); mdf.ReadExactly(b);
            if (a.AsSpan().SequenceEqual(b)) exact++;
            else if (a.AsSpan(96).SequenceEqual(b.AsSpan(96))) hdr++;
            else body.Add(pid);
        }
        return (exact, hdr, body);
    }

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

    public void Dispose() => _fh.Dispose();
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
