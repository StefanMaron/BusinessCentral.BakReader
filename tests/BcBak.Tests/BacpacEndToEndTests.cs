using BcBak;
using Xunit;

/// <summary>
/// Hermetic end-to-end tests against fixtures/typeprobe.bacpac — a sqlpackage export of
/// the same `typeprobe` database fixtures/typeprobe.bak was taken from. Expected values
/// are the very same oracle fixtures the .bak tests assert against, so a bacpac read and
/// a backup read of one database must produce byte-identical output. These run
/// everywhere, including CI, with no SQL Server and no sqlpackage.
/// </summary>
public class BacpacEndToEndTests : IDisposable
{
    internal static string Root
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

    internal static string BacpacPath => Path.Combine(Root, "fixtures", "typeprobe.bacpac");

    readonly IBcSource _src;

    public BacpacEndToEndTests() => _src = BcSource.Open(BacpacPath);

    public void Dispose() => _src.Dispose();

    internal static string Fmt(object? v) => v switch
    {
        null => "NULL",
        bool b => b ? "1" : "0",
        float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    List<string> ReadTable(string name, string[] cols)
    {
        var t = _src.Tables.Single(x => x.Name == name);
        var meta = _src.Columns(t);
        var sel = cols.Select(n => meta.Single(c => c.Name == n)).ToList();
        var rows = _src.ReadRows(t, sel)
            .Select(row => string.Join("|", sel.Select(c => Fmt(row[c.Name]))))
            .ToList();
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
    public void AllNullableTypesMatchOracle(string table, string fixture)
        // The three tables hold identical data under no/row/page compression in the .bak;
        // a bacpac has no storage form at all, so all three must read the same.
        => Assert.Equal(Fixture(fixture), ReadTable(table, ProbeCols));

    static readonly string[] NotNullCols =
    {
        "id","n_tinyint","n_smallint","n_int","n_bigint","n_bit","n_dec38_20","n_dec18_2","n_dec5_0",
        "n_datetime","n_datetime2_7","n_datetime2_0","n_date","n_time7","n_time0","n_guid",
        "n_nvarchar","n_varchar","n_nchar","n_char","n_binary","n_varbinary","n_vbmax","n_nvmax","n_ver"
    };

    [Fact]
    public void NonNullableColumnsMatchOracle()
        // Nullability decides the BCP prefix width: a non-nullable fixed-length column is
        // written raw with no prefix at all, so getting this wrong misaligns every later
        // column of the row. probe_notnull exists for exactly this rule.
        => Assert.Equal(Fixture("typeprobe-probe-notnull.tsv"), ReadTable("probe_notnull", NotNullCols));

    [Fact]
    public void NonNullableFloatsDecodeExactly()
    {
        // real/float are left out of the TSV fixture (SQL Server's float-to-string form is
        // not .NET's round-trip form); assert them here against the chosen literals.
        var t = _src.Tables.Single(x => x.Name == "probe_notnull");
        var meta = _src.Columns(t);
        var sel = new[] { "id", "n_real", "n_float" }.Select(n => meta.Single(c => c.Name == n)).ToList();
        var byId = _src.ReadRows(t, sel).ToDictionary(r => (long)r["id"]!, r => r);
        Assert.Equal(0f, byId[1]["n_real"]);
        Assert.Equal(0d, byId[1]["n_float"]);
        Assert.Equal(1.5f, byId[2]["n_real"]);
        Assert.Equal(2.25d, byId[2]["n_float"]);
        Assert.Equal(-1.5f, byId[3]["n_real"]);
        Assert.Equal(-2.25d, byId[3]["n_float"]);
    }

    [Fact]
    public void ManyDataFilesConcatenateInOrder()
        // probe_dense is written as seven TableData-NNN-00000.BCP files; every row of every
        // file must appear exactly once.
        => Assert.Equal(Fixture("typeprobe-probe-dense.tsv"),
            ReadTable("probe_dense", new[] { "id", "grp", "amount", "posted", "note" }));

    [Fact]
    public void LegacyAndMaxLobsAreInline()
        // text/ntext/image carry a 4-byte length prefix and (max) types an 8-byte one; both
        // hold the whole value inline, so none of the .bak LOB machinery applies. probe_lob
        // row 4 is a 160,000-byte value — well past any single-file chunk boundary.
        => Assert.Equal(Fixture("typeprobe-probe-lob.tsv"),
            ReadTable("probe_lob", new[] { "id", "c_image", "c_text", "c_ntext", "c_vbmax", "c_nvmax" }));

    [Fact]
    public void LobsFromThePageCompressedTable()
        => Assert.Equal(Fixture("typeprobe-probe-lob-page.tsv"),
            ReadTable("probe_lob_page", new[] { "id", "c_image", "c_vbmax", "c_nvmax" }));

    [Fact]
    public void MultiLinkLobs()
        => Assert.Equal(Fixture("typeprobe-probe-lob2.tsv"),
            ReadTable("probe_lob2", new[] { "id", "c_image", "c_vbmax" }));

    [Fact]
    public void NulledAndUpdatedLobs()
        => Assert.Equal(Fixture("typeprobe-probe-lob-upd.tsv"),
            ReadTable("probe_lob_upd", new[] { "id", "c_image", "c_text" }));

    [Fact]
    public void GhostRowsAreNotInTheExport()
        // 500 rows inserted, 166 deleted: a bacpac is a logical export, so the deleted rows
        // are simply absent — the same 334 rows the .bak reader has to filter ghosts to get.
        => Assert.Equal(Fixture("typeprobe-probe-ghost.tsv"),
            ReadTable("probe_ghost", new[] { "id", "val", "amt" }));

    [Fact]
    public void WideTable()
        => Assert.Equal(Fixture("typeprobe-probe-wide.tsv"),
            ReadTable("probe_wide", new[] { "id", "c1", "c100", "c199", "c200", "wtext", "wdec" }));

    [Theory]
    [InlineData("probe_altered", "typeprobe-probe-altered.tsv", "id,b,d,b1,b2,e,f,b3,g")]
    [InlineData("probe_altered_page", "typeprobe-probe-altered-page.tsv", "id,b,d,b1,b2,e,f,b3")]
    public void AlteredTables(string table, string fixture, string cols)
        // Dropped columns leave no trace in model.xml, so the physical-layout problem the
        // .bak reader solves via sysrscols does not exist here — but the result must match.
        => Assert.Equal(Fixture(fixture), ReadTable(table, cols.Split(',')));

    [Fact]
    public void Heap()
        => Assert.Equal(Fixture("typeprobe-probe-heap.tsv"), ReadTable("probe_heap", new[] { "id", "txt", "amt" }));

    [Fact]
    public void ChangeTrackedTable()
        // Change tracking's internal in-row version column is a storage detail; it is not a
        // column of the logical table and must not appear in the bacpac read either.
        => Assert.Equal(Fixture("typeprobe-probe-tracked.tsv"),
            ReadTable("probe_tracked", new[] { "id", "g", "txt", "amt" }));

    [Fact]
    public void RowOverflow()
        => Assert.Equal(Fixture("typeprobe-probe-overflow.tsv"),
            ReadTable("probe_overflow", new[] { "id", "v1", "v2", "n1" }));

    [Fact]
    public void TableListMatchesTheProbeDatabase()
    {
        var names = _src.Tables.Select(t => t.Name).ToHashSet();
        Assert.Contains("probe", names);
        Assert.Contains("probe_notnull", names);
        Assert.Contains("$probe$platform", names);
        Assert.Contains("TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa$ext", names);
        // sqlpackage does not export change tracking's internal side tables
        Assert.DoesNotContain(names, n => n.StartsWith("MSchange_tracking", StringComparison.Ordinal));
    }

    [Fact]
    public void RowCountsAreReported()
    {
        Assert.Equal(4000, _src.Tables.Single(t => t.Name == "probe_dense").RowCount());
        Assert.Equal(334, _src.Tables.Single(t => t.Name == "probe_ghost").RowCount());
        Assert.Equal(3, _src.Tables.Single(t => t.Name == "probe_notnull").RowCount());
    }

    [Fact]
    public void ClusteredKeyComesFromThePrimaryKey()
        => Assert.Equal(new[] { "id" },
            _src.RowKeyColumns(_src.Tables.Single(t => t.Name == "probe_dense")));

    [Fact]
    public void ColumnMetadataCarriesSqlTypesAndWidths()
    {
        var cols = _src.Columns(_src.Tables.Single(t => t.Name == "probe"));
        var nv = cols.Single(c => c.Name == "c_nvarchar");
        Assert.Equal(231, nv.XType);
        Assert.Equal(200, nv.MaxLength);          // nvarchar(100): SysColumn.MaxLength is bytes
        var dec = cols.Single(c => c.Name == "c_dec38_20");
        Assert.Equal(106, dec.XType);
        Assert.Equal(38, dec.Precision);
        Assert.Equal(20, dec.Scale);
        var t7 = cols.Single(c => c.Name == "c_time7");
        Assert.Equal(41, t7.XType);
        Assert.Equal(7, t7.Scale);
        Assert.Equal(100, cols.Single(c => c.Name == "c_varbinary").MaxLength);
        Assert.Equal(-1, _src.Columns(_src.Tables.Single(t => t.Name == "probe_lob"))
            .Single(c => c.Name == "c_nvmax").MaxLength);   // (max) is -1, as in syscolpars
        Assert.Equal(189, _src.Columns(_src.Tables.Single(t => t.Name == "probe_notnull"))
            .Single(c => c.Name == "n_ver").XType);
    }

    [Fact]
    public void SelectingOneColumnStillDecodesIt()
    {
        // Unselected columns are skipped without being decoded; the selected one must not
        // be affected by that (a stream-position bug would show up here first).
        var t = _src.Tables.Single(x => x.Name == "probe");
        var col = _src.Columns(t).Single(c => c.Name == "c_nvarchar");
        var vals = _src.ReadRows(t, new[] { col }).Select(r => Fmt(r["c_nvarchar"])).ToList();
        Assert.Contains("Hello World", vals);
        Assert.Contains("Ærøskøbing über café", vals);
        Assert.Equal(8, vals.Count);
    }
}
