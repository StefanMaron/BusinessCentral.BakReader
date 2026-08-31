using BcBak;
using Xunit;

public class ScsuTests
{
    // Byte sequences taken from real row-compressed nvarchar cells of the typeprobe
    // database, values confirmed against SQL Server SELECT output (PROVENANCE.md
    // "Unicode compression is SCSU").
    [Theory]
    [InlineData("48656c6c6f20576f726c64", "Hello World")]
    [InlineData("c672f8736bf862696e6720fc62657220636166e910", "Ærøskøbing über café")]
    [InlineData("129ab8c0b8bbbbb8c6b020c2b5c1c2", "Кириллица тест")]
    [InlineData("1ffba5cbcbc7cdc9cabc20cac1c9200e4e2d0e65870e5b5720616e64200ed83c0edf8920656d6f6a69", "Ελληνικά και 中文字 and 🎉 emoji")]
    [InlineData("e6f8e520c6d8c520e920e820fc20f620e420df", "æøå ÆØÅ é è ü ö ä ß")]
    [InlineData("616210", "ab")] // trailing 0x10 pad tag emits nothing
    [InlineData("", "")]
    public void DecodesObservedSqlServerScsu(string hex, string expected)
        => Assert.Equal(expected, Scsu.Decode(Convert.FromHexString(hex)));

    [Fact]
    public void RejectsTruncatedTagSequence()
        => Assert.Throws<InvalidDataException>(() => Scsu.Decode(new byte[] { 0x0E, 0x4E })); // SQU needs 2 bytes

    [Fact]
    public void RejectsReservedWindowIndex()
        => Assert.Throws<InvalidDataException>(() => Scsu.Decode(new byte[] { 0x18, 0x00, 0x41 })); // SD0 with reserved index 0
}
