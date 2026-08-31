using System.Buffers.Binary;

namespace BcBak;

/// <summary>
/// Maps SQL Server 8 KB pages inside a native full-backup (.bak, Microsoft Tape Format).
///
/// Instead of parsing the MTF stream chain, pages are located by a linear scan: every
/// 8192-aligned block whose bytes parse as a plausible page header (headerVersion 1,
/// known m_type, sane m_pageId) is treated as an image of that page. Observed on the
/// BC 27.5 / 28.1 demo backups: page images start at file offset 16384 and are
/// 8192-aligned throughout.
///
/// A page id can occur more than once (later images come from the transaction-log
/// portion of the backup). Verified against a real RESTORE on both files: the LAST
/// occurrence in the file is always the image the restored database contains
/// (120/120 and 40/40 duplicate pages checked). Hence last-one-wins.
/// See PROVENANCE.md ("Duplicate page images").
/// </summary>
public sealed class PageFile : IDisposable
{
    public const int PageSize = 8192;
    readonly FileStream _fs;
    readonly Dictionary<(int fileId, int pageId), long> _map = new();
    public int DuplicateImageCount { get; private set; }

    public PageFile(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        Scan();
    }

    static readonly HashSet<byte> KnownPageTypes = new() { 1, 2, 3, 4, 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19, 20 };

    void Scan()
    {
        var buf = new byte[16 * 1024 * 1024];
        long fileOff = 0;
        int read;
        while ((read = ReadFully(buf)) > 0)
        {
            int usable = read / PageSize * PageSize;
            for (int i = 0; i + PageSize <= usable; i += PageSize)
            {
                var h = buf.AsSpan(i, 40);
                if (h[0] != 1 || !KnownPageTypes.Contains(h[1])) continue;
                int pageId = BinaryPrimitives.ReadInt32LittleEndian(h[32..]);
                int fileId = BinaryPrimitives.ReadUInt16LittleEndian(h[36..]);
                if (fileId is < 1 or > 4 || pageId is < 0 or > 10_000_000) continue;
                var key = (fileId, pageId);
                if (_map.ContainsKey(key)) DuplicateImageCount++;
                _map[key] = fileOff + i; // last occurrence wins (see class remarks)
            }
            fileOff += read;
            if (read < buf.Length) break;
        }
    }

    int ReadFully(byte[] buf)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = _fs.Read(buf, total, buf.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public int PageCount => _map.Count;

    public bool TryGetPage(int fileId, int pageId, out byte[] page)
    {
        if (_map.TryGetValue((fileId, pageId), out long off))
        {
            page = new byte[PageSize];
            _fs.Seek(off, SeekOrigin.Begin);
            if (_fs.Read(page, 0, PageSize) != PageSize) throw new IOException("short page read");
            return true;
        }
        page = Array.Empty<byte>();
        return false;
    }

    public byte[] GetPage(int fileId, int pageId)
        => TryGetPage(fileId, pageId, out var p)
            ? p
            : throw new InvalidDataException($"page ({fileId}:{pageId}) not present in backup image");

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
