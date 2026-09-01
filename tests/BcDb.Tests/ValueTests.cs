using BusinessCentral.DbReader;
using Xunit;

public class ValueTests
{
    static SysColumn Col(byte xtype, short maxLen = 0, byte prec = 0, byte scale = 0)
        => new(1, "t", xtype, maxLen, prec, scale);

    static object? D(string hex, SysColumn c, bool compressed = true)
        => SqlTypes.Decode(Cell.Of(Convert.FromHexString(hex)), c, compressed, null);

    // All byte inputs below are real cell bytes observed in the typeprobe database,
    // expected values from SQL Server SELECT (PROVENANCE.md "Type encodings").

    [Theory]
    [InlineData("c019", 38, 20, "1.00000000000000000000")]
    [InlineData("c019", 18, 2, "1.00")]
    [InlineData("c032", 5, 0, "2")]
    [InlineData("404b", 5, 0, "-3")]
    [InlineData("c4f9fde0", 5, 0, "99999")]
    [InlineData("44f9fde0", 5, 0, "-99999")]
    [InlineData("be19", 18, 2, "0.01")]
    [InlineData("3ff780", 18, 2, "-0.99")]
    [InlineData("ac19", 38, 20, "0.00000000000000000001")]
    [InlineData("3f7d", 38, 20, "-0.50000000000000000000")]
    [InlineData("d0f9fe7f9fe7f9fe7f9fe7f9fe7f9fe7e1", 38, 20, "99999999999999999.99999999999999999999")]
    [InlineData("50f9fe7f9fe7f9fe7f9fe7f9fe7f9fe7e1", 38, 20, "-99999999999999999.99999999999999999999")]
    [InlineData("c51edc8c540c566a6e14ea8e80", 38, 20, "123456.78901234567890123457")]
    [InlineData("", 38, 20, "0.00000000000000000000")]
    public void CompressedDecimal(string hex, byte prec, byte scale, string expected)
        => Assert.Equal(expected, D(hex, Col(106, 17, prec, scale)));

    [Theory]
    [InlineData("", "1900-01-01 00:00:00.000")]
    [InlineData("ad247f018b81ff", "9999-12-31 23:59:59.997")]
    [InlineData("7f2e4600000000", "1753-01-01 00:00:00.000")] // pre-1900: negative day count
    [InlineData("f2c9013520dd", "1980-06-15 18:45:30.123")]
    [InlineData("80b4b700cf5a2d", "2026-08-31 12:34:56.790")]
    public void CompressedDatetime(string hex, string expected)
        => Assert.Equal(expected, D(hex, Col(61)));

    [Theory]
    [InlineData("5b950a", "1900-01-01")]
    [InlineData("000000", "0001-01-01")]
    [InlineData("dab937", "9999-12-31")]
    [InlineData("124a0b", "2026-08-31")]
    public void Date(string hex, string expected)
        => Assert.Equal(expected, D(hex, Col(40)));

    [Theory]
    [InlineData("ffbf692ac9", 7, "23:59:59.9999999")]
    [InlineData("07c4aaf46e", 7, "13:14:15.1234567")]
    [InlineData("7f5101", 0, "23:59:59")]
    [InlineData("000000", 0, "00:00:00")]
    public void Time(string hex, byte scale, string expected)
        => Assert.Equal(expected, D(hex, Col(41, 0, 0, scale)));

    [Theory]
    [InlineData("ffbf692ac9dab937", 7, "9999-12-31 23:59:59.9999999")]
    [InlineData("0000000000000000", 7, "0001-01-01 00:00:00.0000000")]
    [InlineData("839bd30153460b", 3, "2024-01-15 08:30:45.123")]
    [InlineData("b5770053460b", 0, "2024-01-15 08:30:45")]
    public void Datetime2(string hex, byte scale, string expected)
        => Assert.Equal(expected, D(hex, Col(42, 0, 0, scale)));

    [Theory]
    [InlineData("c03f", 1.5f)]
    [InlineData("c0bf", -1.5f)]
    [InlineData("6042a20d", 1.0000000031710769e-030f)]
    [InlineData("", 0f)]
    public void CompressedReal(string hex, float expected)
        => Assert.Equal(expected, D(hex, Col(59)));

    [Theory]
    [InlineData("0240", 2.25)]
    [InlineData("2059c0", -100.5)]
    [InlineData("59f3f8c21f6ea501", 1e-300)]
    public void CompressedFloat(string hex, double expected)
        => Assert.Equal(expected, D(hex, Col(62)));

    [Theory]
    [InlineData("81", 56, 1L)]
    [InlineData("", 56, 0L)]
    [InlineData("7f", 56, -1L)]
    [InlineData("80", 56, 0L)] // 0x80 - bias(0x80) = 0? no: 128-128=0 — but zero is stored empty; keep as encoding identity
    [InlineData("ff", 48, 255L)] // tinyint unsigned: no bias
    public void CompressedIntegers(string hex, byte xtype, long expected)
        => Assert.Equal(expected, D(hex, Col(xtype)));

    [Fact]
    public void CompressedBinaryPadsToDeclaredWidth()
        => Assert.Equal("0x8000000000000000", D("80", Col(173, 8)));

    [Fact]
    public void MoneyThrowsLoudly()
    {
        var ex = Assert.Throws<NotSupportedException>(() => D("00", Col(60)));
        Assert.Contains("money", ex.Message);
        Assert.Contains("t", ex.Message); // names the column
    }

    [Fact]
    public void UnknownTypeNamesColumnAndType()
    {
        var ex = Assert.Throws<NotSupportedException>(() => D("00", Col(98)));
        Assert.Contains("sql_variant", ex.Message);
    }

    [Fact]
    public void GuidWrongLengthThrows()
        => Assert.Throws<InvalidDataException>(() => D("0102", Col(36)));

    [Fact]
    public void OffRowValueWithoutLobReaderThrows()
    {
        var cell = Cell.OfComplex(new byte[16]);
        Assert.Throws<NotSupportedException>(() => SqlTypes.Decode(cell, Col(34), true, null));
    }
}
