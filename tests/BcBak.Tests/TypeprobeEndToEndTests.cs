using BcBak;
using Xunit;

/// <summary>
/// Hermetic end-to-end tests against fixtures/typeprobe.bak — a real SQL Server backup
/// of a small scratch database (tools/typeprobe.sql), committed to the repository.
/// Expected values are oracle fixtures exported from SELECT on that same database.
/// These run everywhere, including CI, with no SQL Server and no BC artifacts.
/// </summary>
public class TypeprobeEndToEndTests : IDisposable
{
    static string Root
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "BcBak.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir!;
        }
    }

    readonly PageFile _pf;
    readonly Catalog _cat;

    public TypeprobeEndToEndTests()
    {
        _pf = new PageFile(Path.Combine(Root, "fixtures", "typeprobe.bak"));
        _cat = new Catalog(_pf);
    }

    public void Dispose() => _pf.Dispose();

    List<string> ReadTable(string name, string[] cols, bool sha256Last = false)
    {
        _cat.LoadColumnMetadata();
        var obj = _cat.Objects.Values.Single(o => o.Type == "U" && o.Name == name);
        var rs = _cat.RowsetFor(obj.ObjectId, 1, 0);
        bool compressed = rs.CompressionLevel > 0;
        var lob = new LobReader(_pf);
        var tr = new TableReader(_pf, _cat);
        var meta = _cat.Columns[obj.ObjectId];
        var sel = cols.Select(n => meta.Single(c => c.Name == n)).ToList();
        var rows = new List<string>();
        foreach (var row in tr.ReadRows(obj.ObjectId))
            rows.Add(string.Join("|", sel.Select((c, i) =>
            {
                var v = SqlTypes.Decode(row[c.Name], c, compressed, lob);
                if (v is null) return "NULL";
                if (sha256Last && i == sel.Count - 1 && v is string s && s.StartsWith("0x"))
                    return "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Convert.FromHexString(s[2..])));
                return v switch
                {
                    bool bo => bo ? "1" : "0",
                    float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    _ => v.ToString()!,
                };
            })));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    static List<string> Fixture(string file)
        => File.ReadAllLines(Path.Combine(Root, "fixtures", file))
            .Where(l => l.Length > 0)
            .Select(l => l.EndsWith("|#", StringComparison.Ordinal) ? l[..^2] : l)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

    static readonly string[] ProbeCols =
    {
        "id","c_tinyint","c_smallint","c_int","c_bigint","c_bit","c_dec38_20","c_dec18_2","c_dec5_0",
        "c_datetime","c_datetime2_7","c_datetime2_3","c_datetime2_0","c_date","c_time7","c_time0",
        "c_guid","c_nvarchar","c_varchar","c_nchar","c_char","c_binary","c_varbinary"
    };

    [Theory]
    [InlineData("probe", "typeprobe-probe.tsv")]
    [InlineData("probe_row", "typeprobe-probe-row.tsv")]
    [InlineData("probe_page", "typeprobe-probe-page.tsv")]
    public void AllTypesMatchOracle(string table, string fixture)
        => Assert.Equal(Fixture(fixture), ReadTable(table, ProbeCols));

    [Fact]
    public void PageCompressionWithAnchorsAndDictionary()
        => Assert.Equal(Fixture("typeprobe-probe-dense.tsv"),
            ReadTable("probe_dense", new[] { "id", "grp", "amount", "posted", "note" }));

    [Fact]
    public void LegacyAndMaxLobs()
        => Assert.Equal(Fixture("typeprobe-probe-lob.tsv"),
            ReadTable("probe_lob", new[] { "id", "c_image", "c_text", "c_ntext", "c_vbmax", "c_nvmax" }));

    [Fact]
    public void LobsUnderPageCompression()
        => Assert.Equal(Fixture("typeprobe-probe-lob-page.tsv"),
            ReadTable("probe_lob_page", new[] { "id", "c_image", "c_vbmax", "c_nvmax" }));

    [Fact]
    public void MultiLinkLobRoots()
        => Assert.Equal(Fixture("typeprobe-probe-lob2.tsv"),
            ReadTable("probe_lob2", new[] { "id", "c_image", "c_vbmax" }));

    [Fact]
    public void GhostRecordsInCompressedPagesAreSkipped()
        // 500 rows inserted, 166 deleted right before BACKUP: the page carries 166
        // ghost CD records that SELECT does not return — neither must the reader.
        => Assert.Equal(Fixture("typeprobe-probe-ghost.tsv"),
            ReadTable("probe_ghost", new[] { "id", "val", "amt" }));

    [Fact]
    public void WideTableUsesTwoByteColumnCount()
        // 203 columns: the CD column count takes the two-byte form (BC tables reach
        // 216 columns, so this occurs in real data).
        => Assert.Equal(Fixture("typeprobe-probe-wide.tsv"),
            ReadTable("probe_wide", new[] { "id", "c1", "c100", "c199", "c200", "wtext", "wdec" }));

    [Theory]
    [InlineData("probe_altered", "typeprobe-probe-altered.tsv", "id,b,d,b1,b2,e,f,b3,g")]
    [InlineData("probe_altered_page", "typeprobe-probe-altered-page.tsv", "id,b,d,b1,b2,e,f,b3")]
    public void AlteredTablesDecodeByPhysicalLayout(string table, string fixture, string cols)
        // Columns dropped and added after rows existed: physical order, offsets, null
        // bits and the record column count all diverge from declaration order. The
        // sysrscols leaf layout is the only correct source (an upgraded production
        // database first exposed this).
        => Assert.Equal(Fixture(fixture), ReadTable(table, cols.Split(',')));

    [Fact]
    public void HeapWithEmptySlotsAndChurn()
        // A heap with delete/update churn: 99 slot-array entries are 0 (empty slots)
        // and must be skipped the way SQL Server's own scan skips them — a production
        // heap first exposed this as phantom all-NULL rows.
        => Assert.Equal(Fixture("typeprobe-probe-heap.tsv"),
            ReadTable("probe_heap", new[] { "id", "txt", "amt" }));

    [Fact]
    public void ChangeTrackedTableSkipsInternalVersionColumn()
        // Change tracking adds an internal in-row bigint version column whose sysrscols
        // rscolid carries flag 0x08000000; its masked low bits collide with a real column
        // id, so treating it as a user column shadows that column's value ("GUID cell of
        // 8 bytes" on Published/Installed Application in the BC 28.1 demo database).
        => Assert.Equal(Fixture("typeprobe-probe-tracked.tsv"),
            ReadTable("probe_tracked", new[] { "id", "g", "txt", "amt" }));

    [Fact]
    public void UpdatedAndNulledLegacyLobs()
        // Rewriting a legacy text/image value bumps the word at +16 of the SMALL_ROOT
        // record (reading size as i32 fused them into a giant length), and updating a
        // value to NULL leaves a text pointer to a type-8 (NULL per DBCC PAGE) root
        // record that must decode as SQL NULL.
        => Assert.Equal(Fixture("typeprobe-probe-lob-upd.tsv"),
            ReadTable("probe_lob_upd", new[] { "id", "c_image", "c_text" }));

    [Fact]
    public void PrefetchOpenReadsIdentically()
    {
        // --prefetch runs a best-effort background sequential read to warm the OS cache;
        // the decoded output must be byte-for-byte what a plain open produces.
        using var pf = new PageFile(Path.Combine(Root, "fixtures", "typeprobe.bak"), prefetch: true);
        var cat = new Catalog(pf);
        cat.LoadColumnMetadata();
        var obj = cat.Objects.Values.Single(o => o.Type == "U" && o.Name == "probe_ghost");
        var rows = new TableReader(pf, cat).ReadRows(obj.ObjectId).Count();
        Assert.Equal(334, rows);
    }

    [Fact]
    public void RowOverflow()
        => Assert.Equal(Fixture("typeprobe-probe-overflow.tsv"),
            ReadTable("probe_overflow", new[] { "id", "v1", "v2", "n1" }));

    [Fact]
    public void StructuralMapSelfCheck()
    {
        var (agree, _, _, disagreements) = _pf.CrossCheck();
        Assert.True(agree > 300, $"only {agree} self-identifying blocks agree");
        Assert.Empty(disagreements);
    }

    [Fact]
    public void CatalogEnumeratesProbeTables()
    {
        var names = _cat.Objects.Values.Where(o => o.Type == "U").Select(o => o.Name).ToHashSet();
        Assert.Contains("probe", names);
        Assert.Contains("probe_dense", names);
        Assert.Contains("probe_lob", names);
    }
}
