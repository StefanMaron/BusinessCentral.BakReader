using System.Buffers.Binary;

namespace BcBak;

/// <summary>
/// Resolves off-row values: LOB columns (image/text/ntext, varbinary(max)/nvarchar(max))
/// and row-overflow columns. All byte layouts derived from purpose-built probe tables
/// on a scratch SQL Server database, cross-annotated with DBCC PAGE, and validated by
/// comparing assembled values with SELECT output (PROVENANCE.md "Off-row storage").
///
/// In-row forms (the column's stored cell, marked "complex" by the record format):
///  * 16-byte text pointer (image/text/ntext): [u64 timestamp][6-byte page ptr][u16 slot].
///  * 12+12n-byte inline root (MAX types / row-overflow):
///    [u8 type (2 = row-overflow, 4 = LOB root)][u8][u8 level][u8][u32 updateSeq]
///    [u32 timestamp] then n links of [u32 cumulative size][6-byte page ptr][u16 slot].
///
/// Records on LOB pages (page types 3/4), addressed by (page, slot):
///    [u16 statusA][u16 record length][u64 blobId][u16 type], then per type:
///    type 0 SMALL:            [u32 length][u16] then the data.
///    type 2 INTERNAL:         [u16 maxLinks][u16 curLinks][u16 level] then curLinks ×
///                             [u64 cumulative size][6-byte page ptr][u16 slot].
///    type 3 DATA:             data up to record length.
///    type 5 LARGE_ROOT_YUKON: [u16 maxLinks][u16 curLinks][u16 level][u32] then
///                             curLinks × [u32 cumulative size][6-byte page ptr][u16 slot].
/// </summary>
public sealed class LobReader
{
    readonly PageFile _pf;
    public LobReader(PageFile pf) => _pf = pf;

    /// <summary>Resolve a complex in-row cell (text pointer or inline root) to the full value.</summary>
    public byte[] Resolve(byte[] pointer, string column)
    {
        if (pointer.Length == 16)
        {
            // text pointer: timestamp, then row id of the root/data record
            var (page, fileId, slot) = ReadRowId(pointer, 8);
            var ms = new MemoryStream();
            AppendRecord(ms, fileId, page, slot, 0, column);
            return ms.ToArray();
        }
        if (pointer.Length >= 24 && (pointer.Length - 12) % 12 == 0 && pointer[0] is 2 or 4)
        {
            int links = (pointer.Length - 12) / 12;
            var ms = new MemoryStream();
            long expected = 0;
            for (int i = 0; i < links; i++)
            {
                int o = 12 + 12 * i;
                uint cumSize = BinaryPrimitives.ReadUInt32LittleEndian(pointer.AsSpan(o));
                var (page, fileId, slot) = ReadRowId(pointer, o + 4);
                AppendRecord(ms, fileId, page, slot, 0, column);
                if (ms.Length != cumSize)
                    throw new InvalidDataException($"off-row value of {column}: assembled {ms.Length} bytes where the root link records {cumSize}");
                expected = cumSize;
            }
            if (ms.Length != expected)
                throw new InvalidDataException($"off-row value of {column}: assembled {ms.Length} bytes, root says {expected}");
            return ms.ToArray();
        }
        throw new NotSupportedException($"column {column}: unrecognized {pointer.Length}-byte off-row pointer (first byte 0x{(pointer.Length > 0 ? pointer[0] : 0):x2}) — layout not derived, refusing to guess");
    }

    static (int page, int fileId, int slot) ReadRowId(byte[] b, int o)
        => (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)),
            BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 6)));

    void AppendRecord(MemoryStream ms, int fileId, int pageId, int slot, int depth, string column)
    {
        if (depth > 8) throw new InvalidDataException($"LOB chain of {column} exceeds depth 8 — cycle?");
        var page = _pf.GetPage(fileId, pageId);
        byte pt = PageHeader.Type(page);
        if (pt is not (3 or 4))
            throw new InvalidDataException($"LOB pointer of {column} leads to page 1:{pageId} of type {pt} (expected a LOB page)");
        var offs = PageHeader.SlotOffsets(page).ToArray();
        if (slot >= offs.Length)
            throw new InvalidDataException($"LOB pointer of {column}: slot {slot} beyond {offs.Length} slots on page 1:{pageId}");
        int r = offs[slot];
        if (r < 96)
            throw new InvalidDataException($"LOB pointer of {column} leads to an empty slot ({slot} on page 1:{pageId}) — dangling pointer, refusing to guess");
        int recLen = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(r + 2));
        int type = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(r + 12));
        switch (type)
        {
            case 0: // SMALL: complete value in one record
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(page.AsSpan(r + 14));
                ms.Write(page, r + 20, len);
                break;
            }
            case 3: // DATA: one chunk
                ms.Write(page, r + 14, recLen - 14);
                break;
            case 5: // LARGE_ROOT_YUKON
            {
                int cur = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(r + 16));
                long start = ms.Length;
                for (int i = 0; i < cur; i++)
                {
                    int o = r + 24 + 12 * i;
                    uint cumSize = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(o));
                    var (p2, f2, s2) = ReadRowId(page, o + 4);
                    AppendRecord(ms, f2, p2, s2, depth + 1, column);
                    if (ms.Length - start != cumSize)
                        throw new InvalidDataException($"LOB tree of {column}: assembled {ms.Length - start} bytes where link {i} records {cumSize}");
                }
                break;
            }
            case 2: // INTERNAL
            {
                int cur = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(r + 16));
                long start = ms.Length;
                for (int i = 0; i < cur; i++)
                {
                    int o = r + 20 + 16 * i;
                    long cumSize = BinaryPrimitives.ReadInt64LittleEndian(page.AsSpan(o));
                    var (p2, f2, s2) = ReadRowId(page, o + 8);
                    AppendRecord(ms, f2, p2, s2, depth + 1, column);
                    if (ms.Length - start != cumSize)
                        throw new InvalidDataException($"LOB tree of {column}: assembled {ms.Length - start} bytes where internal link {i} records {cumSize}");
                }
                break;
            }
            default:
                throw new NotSupportedException($"LOB record type {type} on page 1:{pageId} slot {slot} (column {column}) — layout not derived, refusing to guess");
        }
    }
}
