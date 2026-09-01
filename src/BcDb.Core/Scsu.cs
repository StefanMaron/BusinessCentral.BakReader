using System.Text;

namespace BusinessCentral.DbReader;

/// <summary>
/// Decoder for SCSU (Standard Compression Scheme for Unicode), Unicode Technical
/// Standard #6 — the public Unicode specification. SQL Server's "Unicode compression"
/// for nvarchar/nchar columns under row/page compression is SCSU with one convention on
/// top: a value whose SCSU byte form would have even length gets a trailing 0x10 tag
/// (SC0, which emits nothing) so that stored SCSU is always odd-length; even-length
/// stored values are plain UTF-16LE. Both facts validated against SQL Server SELECT
/// output for Latin, Cyrillic, Greek, CJK and surrogate-pair data (PROVENANCE.md
/// "Unicode compression is SCSU").
/// </summary>
internal static class Scsu
{
    // Static window offsets for SQn quoting (UTS #6 table 2).
    static readonly int[] StaticOffsets = { 0x0000, 0x0080, 0x0100, 0x0300, 0x2000, 0x2080, 0x2100, 0x3000 };
    // Initial dynamic window offsets (UTS #6 table 3).
    static readonly int[] InitialDynamicOffsets = { 0x0080, 0x00C0, 0x0400, 0x0600, 0x0900, 0x3040, 0x30A0, 0xFF00 };

    /// <summary>Window offset for an SDn/UDn index byte (UTS #6 section 3.8.4, table 4).</summary>
    static int WindowOffset(byte x, string context) => x switch
    {
        >= 0x01 and <= 0x67 => x * 0x80,
        >= 0x68 and <= 0xA7 => x * 0x80 + 0xAC00,
        0xF9 => 0x00C0, 0xFA => 0x0250, 0xFB => 0x0370, 0xFC => 0x0530,
        0xFD => 0x3040, 0xFE => 0x30A0, 0xFF => 0xFF60,
        _ => throw new InvalidDataException($"SCSU: reserved window index 0x{x:x2} {context}"),
    };

    public static string Decode(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length);
        var dyn = (int[])InitialDynamicOffsets.Clone();
        int win = 0;
        bool unicodeMode = false;
        int i = 0;
        while (i < b.Length)
        {
            byte c = b[i];
            if (!unicodeMode)
            {
                if (c >= 0x80) { sb.Append(CharsFor(dyn[win] + c - 0x80)); i++; continue; }
                switch (c)
                {
                    case 0x00 or 0x09 or 0x0A or 0x0D or (>= 0x20 and <= 0x7F):
                        sb.Append((char)c); i++; break;
                    case >= 0x01 and <= 0x08: // SQn: quote one char from static/dynamic window n
                        Need(b, i, 1);
                        byte q = b[i + 1];
                        sb.Append(CharsFor(q < 0x80 ? StaticOffsets[c - 1] + q : dyn[c - 1] + q - 0x80));
                        i += 2; break;
                    case 0x0E: // SQU: quote one UTF-16BE unit
                        Need(b, i, 2);
                        sb.Append((char)((b[i + 1] << 8) | b[i + 2]));
                        i += 3; break;
                    case 0x0F: unicodeMode = true; i++; break; // SCU
                    case >= 0x10 and <= 0x17: win = c - 0x10; i++; break; // SCn
                    case >= 0x18 and <= 0x1F: // SDn: define dynamic window n
                        Need(b, i, 1);
                        win = c - 0x18;
                        dyn[win] = WindowOffset(b[i + 1], "in SDn");
                        i += 2; break;
                    default:
                        throw new InvalidDataException($"SCSU: reserved single-byte-mode tag 0x{c:x2}");
                }
            }
            else
            {
                switch (c)
                {
                    case >= 0xE0 and <= 0xE7: unicodeMode = false; win = c - 0xE0; i++; break; // UCn
                    case >= 0xE8 and <= 0xEF: // UDn
                        Need(b, i, 1);
                        unicodeMode = false; win = c - 0xE8;
                        dyn[win] = WindowOffset(b[i + 1], "in UDn");
                        i += 2; break;
                    case 0xF0: // UQU: quote one unit that would otherwise be a tag
                        Need(b, i, 2);
                        sb.Append((char)((b[i + 1] << 8) | b[i + 2]));
                        i += 3; break;
                    case 0xF1: // UDX: define extended window
                        Need(b, i, 2);
                        int v = (b[i + 1] << 8) | b[i + 2];
                        unicodeMode = false; win = v >> 13;
                        dyn[win] = 0x10000 + (v & 0x1FFF) * 0x80;
                        i += 3; break;
                    case 0xF2:
                        throw new InvalidDataException("SCSU: reserved Unicode-mode tag 0xF2");
                    default: // plain UTF-16BE unit
                        Need(b, i, 1);
                        sb.Append((char)((c << 8) | b[i + 1]));
                        i += 2; break;
                }
            }
        }
        return sb.ToString();
    }

    static void Need(ReadOnlySpan<byte> b, int i, int extra)
    {
        if (i + extra >= b.Length)
            throw new InvalidDataException("SCSU: truncated tag sequence");
    }

    /// <summary>A window offset ≥ 0x10000 produces a surrogate pair.</summary>
    static string CharsFor(int cp) => char.ConvertFromUtf32(cp);
}
