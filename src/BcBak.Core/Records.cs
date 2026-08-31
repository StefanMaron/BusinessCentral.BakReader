using System.Buffers.Binary;

namespace BcBak;

public enum CellKind { Null, Value }

/// <summary>One decoded storage cell: raw storage-format bytes (before type interpretation), or NULL.</summary>
public readonly record struct Cell(CellKind Kind, byte[]? Bytes, bool Complex = false)
{
    public static readonly Cell Null = new(CellKind.Null, null);
    public static Cell Of(byte[] b) => new(CellKind.Value, b);
    /// <summary>An off-row pointer (text pointer or inline root) rather than the value itself.</summary>
    public static Cell OfComplex(byte[] b) => new(CellKind.Value, b, Complex: true);
}

/// <summary>
/// Uncompressed FixedVar record parser (status bits, fixed data, column count, null bitmap,
/// variable-length offset array). Layout is public knowledge described in prose in SQL Server
/// internals literature; every offset used here was verified against a live SQL Server via
/// DBCC PAGE and by exact row-for-row comparison of the system catalog with sys.* views
/// (see PROVENANCE.md).
/// </summary>
public static class FixedVarRecord
{
    /// <summary>Record type from status bits A (bits 1-3): 0=primary, 5/6/7=ghost variants.</summary>
    public static int RecordType(byte[] p, int off) => (p[off] >> 1) & 7;

    public static (byte statusA, byte[] fixedData, int colCount, byte[] nullBitmap, List<(byte[] data, bool complex)> varCols)
        Parse(byte[] p, int off)
    {
        byte a = p[off];
        bool hasNullBmp = (a & 0x10) != 0;
        bool hasVar = (a & 0x20) != 0;
        int fixedEnd = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(off + 2));
        var fixedData = p.AsSpan(off + 4, fixedEnd - 4).ToArray();
        int pos = off + fixedEnd;
        int ncols = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(pos)); pos += 2;
        var nullBmp = Array.Empty<byte>();
        if (hasNullBmp)
        {
            int nb = (ncols + 7) / 8;
            nullBmp = p.AsSpan(pos, nb).ToArray(); pos += nb;
        }
        var varCols = new List<(byte[], bool)>();
        if (hasVar)
        {
            int nvar = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(pos)); pos += 2;
            int dataStart = pos + 2 * nvar - off;
            int prev = dataStart;
            for (int i = 0; i < nvar; i++)
            {
                int end = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(pos + 2 * i));
                bool complex = (end & 0x8000) != 0;
                end &= 0x7fff;
                varCols.Add((p.AsSpan(off + prev, end - prev).ToArray(), complex));
                prev = end;
            }
        }
        return (a, fixedData, ncols, nullBmp, varCols);
    }

    public static bool IsNull(byte[] nullBmp, int colIndex0Based)
        => nullBmp.Length > 0 && (nullBmp[colIndex0Based / 8] & (1 << (colIndex0Based % 8))) != 0;
}

/// <summary>
/// CD ("column descriptor") record parser for row/page-compressed data.
/// The conceptual model (CI structure after the page header, per-column anchor record,
/// page dictionary, prefix + dictionary compression) is documented by Microsoft
/// (Page Compression Implementation, Microsoft Learn). All byte-level details were
/// derived from the BC backup files themselves cross-annotated with DBCC PAGE, and
/// validated by exact row comparison with SQL Server SELECT output. See PROVENANCE.md.
///
/// CD codes: 0=NULL, 1=empty/zero, 2..9 = (code-1) data bytes, 0xA = value in long data
/// region, 0xC = one-byte page-dictionary symbol.
/// </summary>
public static class CdRecord
{
    public static bool IsCd(byte[] p, int off) => (p[off] & 1) != 0;

    /// <summary>
    /// Ghost (deleted, not yet cleaned up) CD record: header bits 2+3 set (byte 0x0D
    /// observed). Derived by deleting rows from a page-compressed probe table and
    /// correlating header bytes with DBCC PAGE record types: 0x01/0x21 = PRIMARY_RECORD,
    /// 0x0D = GHOST_DATA_RECORD, 267/67 records with no exception (PROVENANCE.md).
    /// A header with exactly one of the two bits set has never been observed — throw.
    /// </summary>
    public static bool IsGhost(byte[] p, int off)
    {
        int bits = p[off] & 0x0C;
        if (bits is not (0 or 0x0C))
            throw new NotSupportedException($"CD record header 0x{p[off]:x2}: ghost-bit pattern 0x{bits:x2} never observed — refusing to guess");
        return bits == 0x0C;
    }

    public static Cell[] Parse(byte[] p, int off, Cell[]? anchors, List<byte[]>? dictionary)
    {
        byte hdr = p[off];
        if ((hdr & 1) == 0) throw new InvalidDataException("not a CD record");
        bool hasLong = (hdr & 0x20) != 0;
        int ncols = p[off + 1];
        if (ncols >= 128) throw new NotSupportedException("CD records with >127 columns not implemented (2-byte column count)");
        // 4-bit CD array, low nibble first
        var codes = new byte[ncols];
        for (int i = 0; i < ncols; i++)
        {
            byte b = p[off + 2 + i / 2];
            codes[i] = (byte)(i % 2 == 0 ? b & 0xf : b >> 4);
        }
        int pos = off + 2 + (ncols + 1) / 2;
        int clusters = (ncols + 29) / 30;
        if (clusters > 1) pos += clusters - 1; // per-cluster short-data byte counts (observed; see PROVENANCE.md)

        var cells = new Cell[ncols];
        var longCols = new List<int>();
        for (int i = 0; i < ncols; i++)
        {
            switch (codes[i])
            {
                case 0: cells[i] = Cell.Null; break;
                case 1: cells[i] = Resolve(i, Array.Empty<byte>(), anchors); break;
                case >= 2 and <= 9:
                    int n = codes[i] - 1;
                    cells[i] = Resolve(i, p.AsSpan(pos, n).ToArray(), anchors); pos += n;
                    break;
                case 0xA: longCols.Add(i); cells[i] = Cell.Null; break;
                case 0xB: // BIT_COLUMN: a bit with value 1, stored entirely in the CD code
                    cells[i] = Cell.Of(new byte[] { 1 });
                    break;
                case 0xC:
                    byte sym = p[pos]; pos += 1;
                    if (dictionary is null || sym >= dictionary.Count)
                        throw new InvalidDataException("dictionary symbol without dictionary");
                    cells[i] = Resolve(i, dictionary[sym], anchors);
                    break;
                default: throw new NotSupportedException($"CD code 0x{codes[i]:x} not implemented");
            }
        }
        if (hasLong)
        {
            if (p[pos] != 1) throw new NotSupportedException($"long data region header 0x{p[pos]:x2} not implemented");
            int cnt = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(pos + 1));
            if (cnt != longCols.Count) throw new InvalidDataException("long region count mismatch");
            int endsOff = pos + 3;
            int dataBase = endsOff + 2 * cnt + (clusters > 1 ? clusters - 1 : 0); // per-cluster long-value counts (observed)
            int prev = 0;
            for (int i = 0; i < cnt; i++)
            {
                int endRaw = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(endsOff + 2 * i));
                // High bit marks a complex entry: an off-row pointer, not inline data
                // (same convention as the FixedVar variable-offset array; derived from
                // probe tables with LOB columns under page compression, PROVENANCE.md).
                bool complex = (endRaw & 0x8000) != 0;
                int end = endRaw & 0x7fff;
                var cell = Resolve(longCols[i], p.AsSpan(dataBase + prev, end - prev).ToArray(), anchors);
                cells[longCols[i]] = complex ? Cell.OfComplex(cell.Bytes!) : cell;
                prev = end;
            }
        }
        return cells;
    }

    /// <summary>Apply anchor-prefix reconstruction: stored = [prefixLength byte][suffix] when the column has a non-null anchor.</summary>
    static Cell Resolve(int col, byte[] stored, Cell[]? anchors)
    {
        var anchor = anchors is not null && col < anchors.Length ? anchors[col] : Cell.Null;
        if (anchor.Kind == CellKind.Null || anchor.Bytes is not { Length: > 0 })
            return Cell.Of(stored);
        if (stored.Length == 0) return Cell.Of(anchor.Bytes); // exact anchor match
        int plen = stored[0];
        if (plen > anchor.Bytes.Length) throw new InvalidDataException("prefix length exceeds anchor");
        var full = new byte[plen + stored.Length - 1];
        anchor.Bytes.AsSpan(0, plen).CopyTo(full);
        stored.AsSpan(1).CopyTo(full.AsSpan(plen));
        return Cell.Of(full);
    }

    /// <summary>
    /// Parse the compression information structure at page offset 96 (pages with
    /// typeFlagBits 0x80): [u8 header (bit1 anchor present, bit2 dictionary present)]
    /// [u16 pageModCount], then one u16 end-offset (relative to the CI start) per
    /// present part — end-of-anchor if bit1, end-of-dictionary if bit2 — then the
    /// anchor record, then the dictionary. The offset fields are conditional:
    /// anchor-only pages carry no end-of-dictionary slot (observed on a page-compressed
    /// table with ghosts whose page had an anchor but no dictionary; the both-present
    /// layout was validated against DBCC PAGE CompressionInfo dumps. PROVENANCE.md).
    /// </summary>
    public static (Cell[]? anchors, List<byte[]> dictionary) ParseCi(byte[] p)
    {
        int o = 96;
        byte hdr = p[o];
        if ((hdr & 1) != 0) throw new NotSupportedException("CI version bit set — unknown CI version");
        bool hasAnchor = (hdr & 2) != 0, hasDict = (hdr & 4) != 0;
        int cursor = o + 3;
        int endOfAnchor = 0;
        if (hasAnchor) { endOfAnchor = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(cursor)); cursor += 2; }
        if (hasDict) cursor += 2; // end-of-dictionary (not needed: entry offsets delimit it)
        Cell[]? anchors = null;
        if (hasAnchor)
            anchors = Parse(p, cursor, null, null);
        var dict = new List<byte[]>();
        if (hasDict)
        {
            int d = o + (hasAnchor ? endOfAnchor : cursor - o);
            int cnt = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(d));
            int prev = 2 + 2 * cnt; // entry end offsets are relative to dictionary start
            for (int i = 0; i < cnt; i++)
            {
                int end = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(d + 2 + 2 * i));
                dict.Add(p.AsSpan(d + prev, end - prev).ToArray());
                prev = end;
            }
        }
        return (anchors, dict);
    }
}
