using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace BcBak;

/// <summary>
/// Interprets storage-format cell bytes as typed values.
///
/// Uncompressed (FixedVar) encodings are the classic little-endian forms; compressed
/// (row/page, CD record) encodings differ per type. Every rule below was derived from
/// purpose-built probe tables on a scratch SQL Server (known values → DBCC PAGE
/// annotated bytes) and validated by comparing full decoded tables with SELECT output
/// on both the probe database and the BC demo databases (PROVENANCE.md "Type encodings").
///
/// Types this reader cannot decode throw, naming the column and the reason. It never
/// substitutes a default for a value it could not decode.
/// </summary>
public static class SqlTypes
{
    public static bool IsVariableLength(byte xtype) => xtype is 231 or 167 or 165 or 99 or 35 or 34 or 241 or 240;

    public static string Name(byte xtype) => xtype switch
    {
        36 => "uniqueidentifier", 40 => "date", 41 => "time", 42 => "datetime2", 43 => "datetimeoffset",
        48 => "tinyint", 52 => "smallint", 56 => "int", 58 => "smalldatetime", 59 => "real",
        60 => "money", 61 => "datetime", 62 => "float", 98 => "sql_variant", 104 => "bit",
        106 => "decimal", 108 => "numeric", 122 => "smallmoney", 127 => "bigint",
        165 => "varbinary", 167 => "varchar", 173 => "binary", 175 => "char", 189 => "timestamp",
        231 => "nvarchar", 239 => "nchar", 34 => "image", 35 => "text", 99 => "ntext", 241 => "xml",
        _ => $"xtype{xtype}"
    };

    public static object? Decode(Cell cell, SysColumn col, bool compressed, LobReader? lob)
    {
        if (cell.Kind == CellKind.Null) return null;
        var b = cell.Bytes!;
        if (cell.Complex)
        {
            if (lob is null)
                throw new NotSupportedException($"column {col.Name}: off-row value but no LOB reader available");
            b = lob.Resolve(b, col.Name);
            // Off-row bytes are the plain value; MAX/LOB types are never SCSU-compressed.
            return col.XType switch
            {
                231 or 99 => System.Text.Encoding.Unicode.GetString(b),           // nvarchar(max) / ntext
                167 or 35 => System.Text.Encoding.Latin1.GetString(b),            // varchar(max) / text
                165 or 34 => "0x" + Convert.ToHexString(b),                       // varbinary(max) / image
                _ => throw new NotSupportedException($"column {col.Name}: off-row value for type {Name(col.XType)} — not derived, refusing to guess"),
            };
        }
        switch (col.XType)
        {
            case 231 or 239: // nvarchar / nchar
            {
                string s;
                if (!compressed || col.MaxLength < 0) s = System.Text.Encoding.Unicode.GetString(b);
                else if (b.Length % 2 == 0) s = System.Text.Encoding.Unicode.GetString(b); // even = plain UTF-16LE
                else s = Scsu.Decode(b);                                                   // odd = SCSU
                if (col.XType == 239 && col.MaxLength > 0) s = s.PadRight(col.MaxLength / 2);
                return s;
            }
            case 167 or 175: // varchar / char — bytes are the single-byte collation form; Latin1 maps them 1:1
            {
                string s = System.Text.Encoding.Latin1.GetString(b);
                if (col.XType == 175 && col.MaxLength > 0) s = s.PadRight(col.MaxLength);
                return s;
            }
            case 34: return "0x" + Convert.ToHexString(b);   // image inline data
            case 35: return System.Text.Encoding.Latin1.GetString(b);
            case 99: return System.Text.Encoding.Unicode.GetString(b);
            case 165: return "0x" + Convert.ToHexString(b);
            case 173: // binary(n): fixed width; compression trims trailing zero bytes
                if (compressed && col.MaxLength > 0 && b.Length < col.MaxLength)
                {
                    var full = new byte[col.MaxLength];
                    b.CopyTo(full, 0);
                    b = full;
                }
                return "0x" + Convert.ToHexString(b);
            case 48 or 52 or 56 or 127: // integers
                if (!compressed) return col.XType switch
                {
                    48 => (long)b[0],
                    52 => (long)BinaryPrimitives.ReadInt16LittleEndian(b),
                    56 => (long)BinaryPrimitives.ReadInt32LittleEndian(b),
                    _ => BinaryPrimitives.ReadInt64LittleEndian(b),
                };
                return DecodeCompressedInt(b, unsigned: col.XType == 48, col);
            case 104: return b.Length != 0 && b[0] != 0;
            case 36: // uniqueidentifier
                if (b.Length != 16) throw new InvalidDataException($"GUID cell of {b.Length} bytes in {col.Name}");
                return new Guid(b).ToString().ToUpperInvariant();
            case 189: // rowversion
            { long t = 0; foreach (var by in b) t = (t << 8) | by; if (!compressed) t = BinaryPrimitives.ReadInt64BigEndian(b); return $"0x{t:X16}"; }
            case 106 or 108: // decimal / numeric
                return compressed ? DecodeVardecimal(b, col) : DecodeDecimal(b, col);
            case 61: // datetime
                return compressed ? DecodeCompressedDatetime(b, col) : DecodeDatetime(b, col);
            case 40: // date
                return FormatDate(ReadLeUInt(b, 3, col, "date"));
            case 41: // time — same layout compressed and not: scaled units, LE, width by scale
                return FormatTime(ReadLeUInt(b, TimeWidth(col.Scale), col, "time"), col.Scale);
            case 42: // datetime2 = time units then 3-byte date
            {
                int tw = TimeWidth(col.Scale);
                if (b.Length == 0) return FormatDate(0) + " " + FormatTime(0, col.Scale);
                if (b.Length != tw + 3) throw new InvalidDataException($"datetime2({col.Scale}) cell of {b.Length} bytes in {col.Name} (expected {tw + 3})");
                long units = 0;
                for (int i = tw - 1; i >= 0; i--) units = (units << 8) | b[i];
                long days = (long)b[tw] | ((long)b[tw + 1] << 8) | ((long)b[tw + 2] << 16);
                return FormatDate(days) + " " + FormatTime(units, col.Scale);
            }
            case 59: // real
                return BitConverter.ToSingle(PadCompressedFloat(b, 4, compressed, col));
            case 62: // float
                return BitConverter.ToDouble(PadCompressedFloat(b, 8, compressed, col));
            default:
                throw new NotSupportedException($"column {col.Name}: type {Name(col.XType)} (xtype {col.XType}) is not supported by this reader");
        }
    }

    /// <summary>Compressed integers: big-endian, trimmed; signed values biased by 2^(8·len−1). Zero is stored empty.</summary>
    static long DecodeCompressedInt(byte[] b, bool unsigned, SysColumn col)
    {
        if (b.Length == 0) return 0L;
        if (b.Length > 8) throw new InvalidDataException($"integer cell of {b.Length} bytes in {col.Name}");
        ulong u = 0; foreach (var by in b) u = (u << 8) | by;
        if (unsigned) return (long)u;
        return (long)(u - (1UL << (8 * b.Length - 1)));
    }

    /// <summary>Uncompressed decimal: [u8 sign (1 = positive)][magnitude, little-endian, in 4-byte units].</summary>
    static string DecodeDecimal(byte[] b, SysColumn col)
    {
        if (b.Length is not (5 or 9 or 13 or 17)) throw new InvalidDataException($"decimal cell of {b.Length} bytes in {col.Name}");
        bool neg = b[0] == 0;
        var mag = new BigInteger(b.AsSpan(1), isUnsigned: true, isBigEndian: false);
        return FormatScaled(neg ? -mag : mag, col.Scale);
    }

    /// <summary>
    /// Row-compression decimal (the vardecimal form): [u8: bit 0x80 = positive,
    /// low 7 bits = biased exponent (value − 64 + 1)] then the mantissa as 10-bit
    /// base-1000 digit groups packed MSB-first, trailing zero bytes trimmed.
    /// value = 0.digits × 10^exponent. Zero is stored empty.
    /// </summary>
    static string DecodeVardecimal(byte[] b, SysColumn col)
    {
        if (b.Length == 0) return FormatScaled(BigInteger.Zero, col.Scale);
        bool neg = (b[0] & 0x80) == 0;
        int exp = (b[0] & 0x7f) - 64 + 1;
        int bits = 8 * (b.Length - 1);
        int groups = (bits + 9) / 10;
        BigInteger digits = 0;
        for (int g = 0; g < groups; g++)
        {
            int val = 0;
            for (int bit = 0; bit < 10; bit++)
            {
                int idx = g * 10 + bit;
                int bv = idx < bits ? (b[1 + idx / 8] >> (7 - idx % 8)) & 1 : 0;
                val = (val << 1) | bv;
            }
            if (val > 999) throw new InvalidDataException($"vardecimal digit group {val} > 999 in {col.Name}");
            digits = digits * 1000 + val;
        }
        // value = digits × 10^(exp − 3·groups); scale to the column's declared scale
        int pow = exp - 3 * groups + col.Scale;
        BigInteger scaled;
        if (pow >= 0) scaled = digits * BigInteger.Pow(10, pow);
        else
        {
            var (q, r) = BigInteger.DivRem(digits, BigInteger.Pow(10, -pow));
            if (!r.IsZero) throw new InvalidDataException($"vardecimal value in {col.Name} does not fit scale {col.Scale}");
            scaled = q;
        }
        return FormatScaled(neg ? -scaled : scaled, col.Scale);
    }

    /// <summary>Format a scale-shifted integer as a plain decimal string with exactly `scale` fraction digits.</summary>
    static string FormatScaled(BigInteger scaled, int scale)
    {
        bool neg = scaled.Sign < 0;
        var abs = BigInteger.Abs(scaled);
        if (scale == 0) return (neg ? "-" : "") + abs.ToString(CultureInfo.InvariantCulture);
        var p = BigInteger.Pow(10, scale);
        var (i, f) = BigInteger.DivRem(abs, p);
        return (neg ? "-" : "") + i + "." + f.ToString(CultureInfo.InvariantCulture).PadLeft(scale, '0');
    }

    /// <summary>Uncompressed datetime: [i32 ticks of day, 1/300 s][i32 days since 1900-01-01], little-endian.</summary>
    static string DecodeDatetime(byte[] b, SysColumn col)
    {
        if (b.Length != 8) throw new InvalidDataException($"datetime cell of {b.Length} bytes in {col.Name}");
        int ticks = BinaryPrimitives.ReadInt32LittleEndian(b);
        int days = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(4));
        return FormatDatetime(days, (uint)ticks, col);
    }

    /// <summary>
    /// Compressed datetime: the 64-bit value (days since 1900 in the high 32 bits, 1/300-second
    /// ticks in the low 32) stored like a compressed bigint — big-endian, biased, trimmed.
    /// </summary>
    static string DecodeCompressedDatetime(byte[] b, SysColumn col)
    {
        long v = DecodeCompressedInt(b, unsigned: false, col);
        int days = (int)(v >> 32);
        uint ticks = (uint)v;
        return FormatDatetime(days, ticks, col);
    }

    static string FormatDatetime(int days, uint ticks, SysColumn col)
    {
        int ms = (int)Math.Round(ticks % 300 * 10.0 / 3.0, MidpointRounding.AwayFromZero);
        var dt = new DateTime(1900, 1, 1).AddDays(days).AddSeconds(ticks / 300);
        return dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "." + ms.ToString("D3", CultureInfo.InvariantCulture);
    }

    static int TimeWidth(int scale) => scale <= 2 ? 3 : scale <= 4 ? 4 : 5;

    static long ReadLeUInt(byte[] b, int width, SysColumn col, string what)
    {
        if (b.Length == 0) return 0;
        if (b.Length != width) throw new InvalidDataException($"{what} cell of {b.Length} bytes in {col.Name} (expected {width})");
        long v = 0;
        for (int i = b.Length - 1; i >= 0; i--) v = (v << 8) | b[i];
        return v;
    }

    static string FormatDate(long daysSince0001)
        => DateTime.MinValue.AddDays(daysSince0001).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static string FormatTime(long units, int scale)
    {
        long perSecond = (long)Math.Pow(10, scale);
        long seconds = units / perSecond, frac = units % perSecond;
        var t = TimeSpan.FromSeconds(seconds);
        string s = $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        return scale == 0 ? s : s + "." + frac.ToString(CultureInfo.InvariantCulture).PadLeft(scale, '0');
    }

    /// <summary>Compressed real/float: little-endian with low-order zero bytes trimmed; the stored bytes are the high end.</summary>
    static byte[] PadCompressedFloat(byte[] b, int width, bool compressed, SysColumn col)
    {
        if (!compressed)
        {
            if (b.Length != width) throw new InvalidDataException($"{Name(col.XType)} cell of {b.Length} bytes in {col.Name}");
            return b;
        }
        if (b.Length > width) throw new InvalidDataException($"{Name(col.XType)} cell of {b.Length} bytes in {col.Name}");
        var full = new byte[width];
        b.CopyTo(full, width - b.Length);
        return full;
    }
}
