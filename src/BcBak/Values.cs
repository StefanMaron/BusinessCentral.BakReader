using System.Buffers.Binary;

namespace BcBak;

/// <summary>
/// Interprets storage-format cell bytes as typed values.
/// Compressed (CD) cells hold row-compression encodings: integers are big-endian with
/// leading bytes trimmed; zero/empty values are zero-length; nvarchar uses SQL Server
/// "Unicode compression" (single-byte for Latin data, trailing 0x10 marker when the
/// single-byte form has even length; even-length cells are plain UTF-16LE).
/// All rules observed on the BC backups and validated against SELECT output (PROVENANCE.md).
/// Types whose compressed encoding has not been reversed yet (non-zero decimals,
/// datetime/datetime2/date/time, LOBs) are rendered as explicit raw hex, never as a guess.
/// </summary>
public static class SqlTypes
{
    public static bool IsVariableLength(byte xtype) => xtype is 231 or 167 or 165 or 99 or 35 or 34 or 241 or 240;

    public static string Name(byte xtype) => xtype switch
    {
        36 => "uniqueidentifier", 40 => "date", 41 => "time", 42 => "datetime2", 48 => "tinyint",
        52 => "smallint", 56 => "int", 61 => "datetime", 104 => "bit", 106 => "decimal",
        127 => "bigint", 165 => "varbinary", 167 => "varchar", 175 => "char", 189 => "timestamp",
        231 => "nvarchar", 239 => "nchar", 34 => "image", 35 => "text", 99 => "ntext", 241 => "xml",
        _ => $"xtype{xtype}"
    };

    public static object? Decode(Cell cell, SysColumn col, bool compressed)
    {
        if (cell.Kind == CellKind.Null) return null;
        var b = cell.Bytes!;
        switch (col.XType)
        {
            case 231 or 239: // nvarchar / nchar
                if (!compressed) return System.Text.Encoding.Unicode.GetString(b);
                if (b.Length == 0) return "";
                if (b.Length % 2 == 0) return System.Text.Encoding.Unicode.GetString(b);
                if (b[^1] == 0x10) b = b[..^1];
                foreach (var by in b) if (by >= 0x80)
                    throw new NotSupportedException($"non-Latin single-byte Unicode-compressed data in {col.Name} (SCSU windows not implemented)");
                return System.Text.Encoding.Latin1.GetString(b);
            case 167 or 175: return System.Text.Encoding.Latin1.GetString(b);
            case 48 or 52 or 56 or 127: // integers
                if (!compressed) return col.XType switch
                {
                    48 => b[0],
                    52 => (long)BinaryPrimitives.ReadInt16LittleEndian(b),
                    56 => (long)BinaryPrimitives.ReadInt32LittleEndian(b),
                    _ => BinaryPrimitives.ReadInt64LittleEndian(b),
                };
                if (b.Length == 0) return 0L;
                if (b.Length > 8) throw new InvalidDataException($"integer cell of {b.Length} bytes in {col.Name}");
                ulong u = 0; foreach (var by in b) u = (u << 8) | by; // big-endian, trimmed to minimal length
                if (col.XType == 48) return (long)u;                  // tinyint is unsigned: no bias
                // Signed integers are stored order-preserving with the value biased by 2^(8*len-1)
                // (observed: int value 1 stored as 0x81; validated against SELECT output, PROVENANCE.md).
                return (long)(u - (1UL << (8 * b.Length - 1)));
            case 104: return b.Length != 0 && b[0] != 0;
            case 36: // uniqueidentifier
                if (b.Length != 16) throw new InvalidDataException($"GUID cell of {b.Length} bytes in {col.Name}");
                return new Guid(b).ToString().ToUpperInvariant();
            case 189: // rowversion: big-endian trimmed under compression
                { long t = 0; foreach (var by in b) t = (t << 8) | by; return $"0x{t:X16}"; }
            case 106: // decimal
                if (compressed && b.Length == 0) return 0m;
                return Raw(b, "decimal-encoding-not-implemented");
            case 61 or 42 or 40 or 41: return Raw(b, Name(col.XType) + "-encoding-not-implemented");
            case 165 or 34 or 35 or 99 or 241: return Raw(b, Name(col.XType));
            default: return Raw(b, "unknown-xtype-" + col.XType);
        }
    }

    static int WidthOf(byte xtype) => xtype switch { 48 => 1, 52 => 2, 56 => 4, _ => 8 };
    static string Raw(byte[] b, string why) => $"raw[{why}]:0x{Convert.ToHexString(b)}";
}
