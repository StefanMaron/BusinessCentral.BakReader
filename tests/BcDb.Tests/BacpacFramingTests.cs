using System.IO.Compression;
using System.Text;
using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// The native-BCP row framing of a .bacpac data stream, asserted on hand-written bytes,
/// plus the guards that must fire when a stream does not match the derived rules.
/// Every byte sequence here was observed in fixtures/typeprobe.bacpac for a row whose
/// value the oracle states (PROVENANCE.md "bacpac: native BCP row framing").
/// </summary>
public class BacpacFramingTests
{
    static BacpacColumn Col(string name, string type, bool nullable = true,
        int length = 0, byte precision = 0, byte scale = 0, bool isMax = false)
        => BacpacColumn.FromModel(name, type, nullable, isMax, length, precision, scale);

    static List<object?[]> Read(byte[] data, params BacpacColumn[] cols)
    {
        var sys = cols.Select((c, i) => c.ToSysColumn(i + 1)).ToList();
        var rows = new List<object?[]>();
        foreach (var cells in new BcpRowReader("t", cols).Read(new MemoryStream(data), cols))
            rows.Add(cells.Select((c, i) => SqlTypes.Decode(c, sys[i], compressed: false, lob: null, textIsUtf16: true)).ToArray());
        return rows;
    }

    [Fact]
    public void PrefixWidthsFollowTheDerivedRule()
    {
        // Fixed-length types: no prefix when NOT NULL, a one-byte length when nullable.
        Assert.Equal(0, BcpRowReader.PrefixLength(Col("a", "int", nullable: false)));
        Assert.Equal(1, BcpRowReader.PrefixLength(Col("a", "int")));
        Assert.Equal(0, BcpRowReader.PrefixLength(Col("a", "datetime2", nullable: false, scale: 7)));
        // char(n) is the only character type that follows that rule.
        Assert.Equal(0, BcpRowReader.PrefixLength(Col("a", "char", nullable: false, length: 10)));
        Assert.Equal(2, BcpRowReader.PrefixLength(Col("a", "char", length: 10)));
        // bit, uniqueidentifier and decimal always carry a one-byte prefix.
        Assert.Equal(1, BcpRowReader.PrefixLength(Col("a", "bit", nullable: false)));
        Assert.Equal(1, BcpRowReader.PrefixLength(Col("a", "uniqueidentifier", nullable: false)));
        Assert.Equal(1, BcpRowReader.PrefixLength(Col("a", "decimal", nullable: false, precision: 18, scale: 2)));
        // The variable-length family always carries two bytes, nullable or not — including
        // nchar(n), which is fixed-length in SQL Server but prefixed here.
        Assert.Equal(2, BcpRowReader.PrefixLength(Col("a", "nchar", nullable: false, length: 10)));
        Assert.Equal(2, BcpRowReader.PrefixLength(Col("a", "nvarchar", nullable: false, length: 50)));
        Assert.Equal(2, BcpRowReader.PrefixLength(Col("a", "varbinary", nullable: false, length: 50)));
        Assert.Equal(2, BcpRowReader.PrefixLength(Col("a", "rowversion", nullable: false)));
        // Legacy LOBs four bytes, (max) types eight.
        Assert.Equal(4, BcpRowReader.PrefixLength(Col("a", "image")));
        Assert.Equal(4, BcpRowReader.PrefixLength(Col("a", "text")));
        Assert.Equal(8, BcpRowReader.PrefixLength(Col("a", "varbinary", isMax: true)));
        Assert.Equal(8, BcpRowReader.PrefixLength(Col("a", "nvarchar", nullable: false, isMax: true)));
    }

    [Fact]
    public void PlatformTableRow()
    {
        // fixtures/typeprobe.bacpac, [$probe$platform] = (1, N'platform-one'), (2, NULL).
        var data = new byte[] { 0x01, 0, 0, 0, 0x18, 0 }
            .Concat(Encoding.Unicode.GetBytes("platform-one"))
            .Concat(new byte[] { 0x02, 0, 0, 0, 0xFF, 0xFF }).ToArray();
        var rows = Read(data, Col("id", "int", nullable: false), Col("v", "nvarchar", length: 20));
        Assert.Equal(2, rows.Count);
        Assert.Equal(1L, rows[0][0]);
        Assert.Equal("platform-one", rows[0][1]);
        Assert.Equal(2L, rows[1][0]);
        Assert.Null(rows[1][1]);
    }

    [Fact]
    public void NullableFixedLengthNullIsAllOnes()
    {
        // 0xFF in a one-byte prefix is NULL; 0x00 would be a zero-length value, which no
        // fixed-length column can have.
        var rows = Read(new byte[] { 0xFF, 0x04, 0x2A, 0, 0, 0 }, Col("a", "int"), Col("b", "int"));
        Assert.Null(rows[0][0]);
        Assert.Equal(42L, rows[0][1]);
    }

    [Fact]
    public void EmptyIsNotNull()
    {
        // A zero-length nvarchar is the empty string; only 0xFFFF is NULL.
        var rows = Read(new byte[] { 0, 0, 0xFF, 0xFF }, Col("a", "nvarchar", length: 10), Col("b", "nvarchar", length: 10));
        Assert.Equal("", rows[0][0]);
        Assert.Null(rows[0][1]);
    }

    [Fact]
    public void DecimalCarriesItsOwnPrecisionAndScale()
    {
        // [len 19][precision][scale][sign: 1 = positive][16-byte magnitude, little-endian].
        var body = new byte[] { 19, 18, 2, 1 }.Concat(new byte[16]).ToArray();
        body[4] = 0x7D;                                   // 125 -> 1.25 at scale 2
        var rows = Read(body, Col("d", "decimal", nullable: false, precision: 18, scale: 2));
        Assert.Equal("1.25", rows[0][0]);

        var neg = new byte[] { 19, 18, 2, 0 }.Concat(new byte[16]).ToArray();
        neg[4] = 0x7D;
        Assert.Equal("-1.25", Read(neg, Col("d", "decimal", nullable: false, precision: 18, scale: 2))[0][0]);
    }

    [Fact]
    public void TimeAndDatetime2AreAlwaysFullWidthHundredNanosecondUnits()
    {
        // Regardless of the declared scale, a bacpac writes time as five bytes of 100 ns
        // units and datetime2 as those five bytes plus a three-byte day number.
        var t0 = BitConverter.GetBytes(863990000000L).Take(5).ToArray();     // 23:59:59
        Assert.Equal("23:59:59", Read(t0, Col("t", "time", nullable: false))[0][0]);

        var d = new byte[] { 0x5B, 0x95, 0x0A };                             // 1900-01-01
        var dt2 = t0.Concat(d).ToArray();
        Assert.Equal("1900-01-01 23:59:59", Read(dt2, Col("d", "datetime2", nullable: false))[0][0]);
        Assert.Equal("1900-01-01 23:59:59.0000000",
            Read(dt2, Col("d", "datetime2", nullable: false, scale: 7))[0][0]);
    }

    [Fact]
    public void NonUnicodeTextIsExportedAsUtf16()
    {
        // DacFx writes varchar/char/text as UTF-16, not in the column's collation code page.
        var data = new byte[] { 0x0A, 0 }.Concat(Encoding.Unicode.GetBytes("Hello")).ToArray();
        Assert.Equal("Hello", Read(data, Col("v", "varchar", length: 100))[0][0]);

        var ch = Encoding.Unicode.GetBytes("xyz       ");   // char(10), padded by the server
        Assert.Equal("xyz       ", Read(ch, Col("c", "char", nullable: false, length: 10))[0][0]);
    }

    [Fact]
    public void RowversionIsEightBytesBigEndian()
    {
        var data = new byte[] { 8, 0, 0, 0, 0, 0, 0, 0, 0x07, 0xE9 };
        Assert.Equal("0x00000000000007E9", Read(data, Col("v", "rowversion", nullable: false))[0][0]);
    }

    // ---- guards -------------------------------------------------------------------

    [Fact]
    public void PrefixThatDisagreesWithTheTypeWidthThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Read(new byte[] { 0x02, 0, 0 }, Col("a", "int")));
        Assert.Contains("a", ex.Message);
        Assert.Contains("int", ex.Message);
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void DecimalWhosePrecisionDisagreesWithTheModelThrows()
    {
        var body = new byte[] { 19, 38, 2, 1 }.Concat(new byte[16]).ToArray();
        var ex = Assert.Throws<InvalidDataException>(() =>
            Read(body, Col("d", "decimal", nullable: false, precision: 18, scale: 2)));
        Assert.Contains("d", ex.Message);
        Assert.Contains("38", ex.Message);
    }

    [Fact]
    public void TruncatedStreamThrowsNamingTheTable()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Read(new byte[] { 0x01, 0x00 }, Col("id", "int", nullable: false)));
        Assert.Contains("t", ex.Message);
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void ChunkedMaxValueIsRefusedRatherThanGuessed()
    {
        // 0xFFFFFFFFFFFFFFFE is SQL Server's "length unknown, chunks follow" form. No
        // export observed so far uses it, so it is not derived — and must not be guessed.
        var data = new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        var ex = Assert.Throws<NotSupportedException>(() => Read(data, Col("b", "varbinary", isMax: true)));
        Assert.Contains("b", ex.Message);
        Assert.Contains("refusing to guess", ex.Message);
    }

    [Fact]
    public void TypeWhoseFramingIsNotDerivedThrowsNamingIt()
    {
        var ex = Assert.Throws<NotSupportedException>(() => Read(new byte[8], Col("m", "money", nullable: false)));
        Assert.Contains("m", ex.Message);
        Assert.Contains("money", ex.Message);
    }

    // ---- container guards ---------------------------------------------------------

    static string Rewrite(string entry, Func<string, string> edit)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bcdb-test-" + Guid.NewGuid().ToString("N") + ".bacpac");
        File.Copy(BacpacEndToEndTests.BacpacPath, tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Update))
        {
            var e = zip.GetEntry(entry)!;
            string text;
            using (var r = new StreamReader(e.Open())) text = r.ReadToEnd();
            e.Delete();
            var n = zip.CreateEntry(entry);
            using var w = new StreamWriter(n.Open());
            w.Write(edit(text));
        }
        return tmp;
    }

    [Fact]
    public void ModelChecksumMismatchThrows()
    {
        // model.xml is parsed on first use rather than at open (it is 113 MB in a real
        // export), so the guard fires on the first query — before any row is returned.
        var tmp = Rewrite("model.xml", s => s.Replace("probe_dense", "probe_dxnse"));
        try
        {
            using var src = BcSource.Open(tmp);
            var ex = Assert.Throws<InvalidDataException>(() => src.Tables);
            Assert.Contains("model.xml", ex.Message);
            Assert.Contains("checksum", ex.Message);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void ARefusedPackageIsNotLeftOpen()
    {
        // A constructor that throws leaves the caller no instance to dispose, so the guards
        // below the zip check have to close the archive themselves. On Linux a leaked handle
        // is invisible — an open file can still be unlinked — so this went unnoticed until
        // the release matrix ran the suite on Windows, where the refused .bacpac stayed
        // locked and could not be deleted or moved. FileShare.None is honored on both.
        var tmp = Rewrite("Origin.xml", s => s.Replace("\"Data\">2.0.0.0<", "\"Data\">3.0.0.0<"));
        try
        {
            Assert.Throws<InvalidDataException>(() => BcSource.Open(tmp));
            using var exclusive = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void UnknownDataStreamVersionThrows()
    {
        var tmp = Rewrite("Origin.xml", s => s.Replace("\"Data\">2.0.0.0<", "\"Data\">3.0.0.0<"));
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => BcSource.Open(tmp));
            Assert.Contains("3.0.0.0", ex.Message);
            Assert.Contains("refusing to guess", ex.Message);
        }
        finally { File.Delete(tmp); }
    }
}
