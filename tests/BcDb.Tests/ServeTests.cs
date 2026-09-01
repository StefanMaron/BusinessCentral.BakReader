using System.Text.Json;
using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// Hermetic tests for the serve mode: one backup opened once, many requests answered
/// over a line-based JSON protocol (see Program.Serve). Driven in-process against
/// fixtures/typeprobe.bak; values asserted against the same oracle-known data the
/// fixture tests use.
/// </summary>
public class ServeTests
{
    static string Root
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "BcDb.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir!;
        }
    }

    static List<JsonDocument> RunOn(string fixture, Dictionary<string, string>? startupOpts, params string[] requests)
    {
        using var src = BcSource.Open(Path.Combine(Root, "fixtures", fixture));
        var input = new StringReader(string.Join("\n", requests));
        var output = new StringWriter();
        int rc = BusinessCentral.DbReader.Program.Serve(src, startupOpts ?? new Dictionary<string, string>(), input, output);
        Assert.Equal(0, rc);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l)).ToList();
    }

    static List<JsonDocument> Run(Dictionary<string, string>? startupOpts, params string[] requests)
        => RunOn("typeprobe.bak", startupOpts, requests);

    static List<JsonDocument> Run(params string[] requests) => Run(null, requests);

    static Dictionary<string, string> ExtTestSymbols => new()
    {
        ["symbols"] = Path.Combine(Root, "fixtures", "symbols-exttest-base.json") + ","
                    + Path.Combine(Root, "fixtures", "symbols-exttest-ext.json"),
    };

    [Fact]
    public void ReadReturnsOracleKnownValues()
    {
        var res = Run("""{"id": 7, "cmd": "read", "table": "probe", "select": "id,c_bigint,c_nvarchar"}""");
        var r = Assert.Single(res).RootElement;
        Assert.Equal(7, r.GetProperty("id").GetInt32());
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { "id", "c_bigint", "c_nvarchar" },
            r.GetProperty("headers").EnumerateArray().Select(h => h.GetString()).ToArray());
        var rows = r.GetProperty("rows").EnumerateArray()
            .ToDictionary(row => row[0].GetInt64(), row => row);
        // oracle-known values from typeprobe-probe.tsv
        Assert.Equal(9223372036854775807L, rows[2][1].GetInt64());
        Assert.Equal("Hello World", rows[2][2].GetString());
        Assert.Equal(-9223372036854775808L, rows[3][1].GetInt64());
        Assert.Equal("Ærøskøbing über café", rows[3][2].GetString());
        Assert.Equal(JsonValueKind.Null, rows[6][1].ValueKind);
    }

    [Fact]
    public void ErrorKeepsTheSessionAlive()
    {
        var res = Run(
            """{"id": 1, "cmd": "read", "table": "no_such_table"}""",
            """{"id": 2, "cmd": "read", "table": "probe_ghost", "select": "id,val", "top": 1}""");
        Assert.Equal(2, res.Count);
        Assert.False(res[0].RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("no_such_table", res[0].RootElement.GetProperty("error").GetString());
        Assert.True(res[1].RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(res[1].RootElement.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public void TablesAndCompaniesAndQuit()
    {
        var res = Run(
            """{"cmd": "tables"}""",
            """{"cmd": "companies"}""",
            """{"cmd": "quit"}""",
            """{"cmd": "read", "table": "probe"}""");   // after quit: must not be answered
        Assert.Equal(3, res.Count);
        var tables = res[0].RootElement.GetProperty("tables").EnumerateArray()
            .GroupBy(t => t.GetProperty("name").GetString()!)
            .ToDictionary(g => g.Key, g => g.First());
        Assert.Equal(8, tables["probe"].GetProperty("rows").GetInt64());
        Assert.Equal("page", tables["probe_ghost"].GetProperty("compression").GetString());
        Assert.Equal(JsonValueKind.Null, tables["probe"].GetProperty("company").ValueKind);
        Assert.True(res[1].RootElement.GetProperty("ok").GetBoolean());
        Assert.True(res[2].RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void DollarPrefixedPlatformTableKeepsItsRawName()
    {
        // "$probe$platform".Split('$') matches no <company>$<table>$<appid> shape; the
        // listing must fall back to the raw object name, not an empty string, so
        // platform tables ($ndo$... in real databases) stay discoverable (issue #14).
        var res = Run(
            """{"cmd": "tables"}""",
            """{"cmd": "read", "table": "$probe$platform", "select": "id,v"}""");
        var tables = res[0].RootElement.GetProperty("tables").EnumerateArray()
            .GroupBy(t => t.GetProperty("name").GetString()!)
            .ToDictionary(g => g.Key, g => g.First());
        Assert.True(tables.ContainsKey("$probe$platform"));
        Assert.Equal(2, tables["$probe$platform"].GetProperty("rows").GetInt64());
        Assert.False(tables.ContainsKey(""));
        var rows = res[1].RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal("platform-one", rows[0][1].GetString());
    }

    [Fact]
    public void AppSelectorDisambiguatesSameNamedTables()
    {
        // Two apps define "ambig" in the same company (legal via AL namespaces; the BC
        // demo database ships Dimension Set Entry twice). --company cannot help; the
        // error must name --app, and an app-id prefix must select one (issue #13).
        var res = Run(
            """{"id": 1, "cmd": "read", "table": "ambig"}""",
            """{"id": 2, "cmd": "read", "table": "ambig", "app": "2222", "select": "id,v"}""");
        Assert.False(res[0].RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("--app", res[0].RootElement.GetProperty("error").GetString());
        Assert.True(res[1].RootElement.GetProperty("ok").GetBoolean());
        var row = res[1].RootElement.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal("from-app-two", row[1].GetString());
    }

    [Fact]
    public void DescribeListsTableExtensionFields()
    {
        // Extension fields live in the $ext companion table as "<Field>$<extending app>"
        // columns; describe must resolve them through the extending app's tableextension
        // symbols to AL field ids and types (issue #12).
        var res = Run(ExtTestSymbols,
            """{"cmd": "describe", "table": "exttest"}""");
        var r = Assert.Single(res).RootElement;
        Assert.True(r.GetProperty("ok").GetBoolean());
        var fields = r.GetProperty("fields").EnumerateArray()
            .Where(f => f.GetProperty("sqlColumn").ValueKind == JsonValueKind.String)
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        Assert.Equal(2, fields["own"].GetProperty("id").GetInt32());
        Assert.Equal(50120, fields["extra"].GetProperty("id").GetInt32());
        Assert.Equal("Text", fields["extra"].GetProperty("type").GetString());
        Assert.Equal("extra$bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", fields["extra"].GetProperty("sqlColumn").GetString());
        Assert.Equal(50121, fields["num"].GetProperty("id").GetInt32());
    }

    [Fact]
    public void MergedReadJoinsExtensionCompanion()
    {
        // One AL record = base row + $ext companion row joined on the clustered key.
        // Base row 3 has no companion row: its extension fields are NULL. Values match
        // the oracle's LEFT JOIN (typeprobe-probe-exttest-merged.tsv is the same data).
        var res = Run(ExtTestSymbols,
            """{"cmd": "read", "table": "exttest", "merge-extensions": true}""");
        var r = Assert.Single(res).RootElement;
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { "id", "own", "extra", "num" },
            r.GetProperty("headers").EnumerateArray().Select(h => h.GetString()).ToArray());
        var rows = r.GetProperty("rows").EnumerateArray()
            .ToDictionary(row => row[0].GetInt64(), row => row);
        Assert.Equal("base-one", rows[1][1].GetString());
        Assert.Equal("ext-one", rows[1][2].GetString());
        Assert.Equal(11, rows[1][3].GetInt64());
        Assert.Equal(JsonValueKind.Null, rows[2][2].ValueKind);
        Assert.Equal(22, rows[2][3].GetInt64());
        Assert.Equal("base-three", rows[3][1].GetString());
        Assert.Equal(JsonValueKind.Null, rows[3][2].ValueKind);
        Assert.Equal(JsonValueKind.Null, rows[3][3].ValueKind);
    }

    // exttest2 carries Base Application's Posted Gen. Journal Line shape (table 181): the
    // base table's primary key is (id) alone, but its CLUSTERED index is (tmpl, batch, id),
    // and BC keys the $ext companion on the primary key. Joining a merged read on the base
    // table's clustered key therefore asks the companion for a column it does not have and
    // refuses; the join key is the companion's own key (GitHub issue #17). Field order is
    // mirrored from table 181 too: the primary-key field is not the first field.
    static void AssertExtTest2Merged(JsonElement r)
    {
        Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString());
        Assert.Equal(new[] { "tmpl", "id", "own", "batch",
                             "extra$bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                             "num$bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" },
            r.GetProperty("headers").EnumerateArray().Select(h => h.GetString()).ToArray());
        var rows = r.GetProperty("rows").EnumerateArray().ToDictionary(row => row[1].GetInt64(), row => row);
        Assert.Equal(3, rows.Count);
        // oracle values — fixtures/typeprobe-probe-exttest2-merged.tsv is the same LEFT JOIN
        Assert.Equal("GENERAL", rows[1][0].GetString());
        Assert.Equal("base-one", rows[1][2].GetString());
        Assert.Equal("DEFAULT", rows[1][3].GetString());
        Assert.Equal("ext-one", rows[1][4].GetString());
        Assert.Equal(11, rows[1][5].GetInt64());
        Assert.Equal("DAILY", rows[2][3].GetString());
        Assert.Equal(JsonValueKind.Null, rows[2][4].ValueKind);   // id 2 has no companion row
        Assert.Equal(JsonValueKind.Null, rows[2][5].ValueKind);
        Assert.Equal("SALES", rows[3][0].GetString());
        Assert.Equal(JsonValueKind.Null, rows[3][4].ValueKind);   // id 3 has one, with a NULL text
        Assert.Equal(33, rows[3][5].GetInt64());
    }

    [Fact]
    public void MergedReadJoinsOnTheCompanionKeyNotTheBaseClusteredKey()
        => AssertExtTest2Merged(Assert.Single(
            Run("""{"cmd": "read", "table": "exttest2", "merge-extensions": true}""")).RootElement);

    [Fact]
    public void BacpacMergedReadJoinsOnTheSameCompanionKey()
        => AssertExtTest2Merged(Assert.Single(RunOn("typeprobe.bacpac", null,
            """{"cmd": "read", "table": "exttest2", "merge-extensions": true}""")).RootElement);

    [Fact]
    public void MergedReadRefusesWhenTheCompanionKeyIsNotInTheBaseTable()
    {
        // exttest3's companion is keyed on "stranger", which its base table does not have.
        // That is the one shape a merged read genuinely cannot join, and it must refuse by
        // name rather than join on whatever happens to match.
        var r = Assert.Single(
            Run("""{"cmd": "read", "table": "exttest3", "merge-extensions": true}""")).RootElement;
        Assert.False(r.GetProperty("ok").GetBoolean());
        string err = r.GetProperty("error").GetString()!;
        Assert.Contains("stranger", err);
        Assert.Contains("TP$exttest3$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa$ext", err);
        Assert.Contains("refusing to guess", err);
    }

    [Fact]
    public void MalformedRequestReportsErrorAndContinues()
    {
        var res = Run(
            "this is not json",
            """{"id": "x", "cmd": "read", "table": "probe", "select": "id", "top": 2}""");
        Assert.Equal(2, res.Count);
        Assert.False(res[0].RootElement.GetProperty("ok").GetBoolean());
        Assert.True(res[1].RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("x", res[1].RootElement.GetProperty("id").GetString());
        Assert.Equal(2, res[1].RootElement.GetProperty("rows").GetArrayLength());
    }

    // ---- the same protocol over a .bacpac ------------------------------------------

    [Fact]
    public void BacpacServeAnswersTheSameValues()
    {
        var res = RunOn("typeprobe.bacpac", null,
            """{"id": 7, "cmd": "read", "table": "probe", "select": "id,c_bigint,c_nvarchar"}""");
        var r = Assert.Single(res).RootElement;
        Assert.True(r.GetProperty("ok").GetBoolean());
        var rows = r.GetProperty("rows").EnumerateArray().ToDictionary(row => row[0].GetInt64(), row => row);
        Assert.Equal(9223372036854775807L, rows[2][1].GetInt64());
        Assert.Equal("Hello World", rows[2][2].GetString());
        Assert.Equal("Ærøskøbing über café", rows[3][2].GetString());
        Assert.Equal(JsonValueKind.Null, rows[6][1].ValueKind);
    }

    [Fact]
    public void BacpacServeMergesExtensionCompanions()
    {
        var res = RunOn("typeprobe.bacpac", ExtTestSymbols,
            """{"id": 1, "cmd": "read", "table": "exttest", "merge-extensions": true, "select": "id,own,extra,num"}""");
        var r = Assert.Single(res).RootElement;
        Assert.True(r.GetProperty("ok").GetBoolean());
        var rows = r.GetProperty("rows").EnumerateArray().ToDictionary(row => row[0].GetInt64(), row => row);
        Assert.Equal("ext-one", rows[1][2].GetString());
        Assert.Equal(11, rows[1][3].GetInt64());
        Assert.Equal(JsonValueKind.Null, rows[2][2].ValueKind);
        // base row 3 has no companion row: its extension fields read as NULL, not as an error
        Assert.Equal(JsonValueKind.Null, rows[3][3].ValueKind);
    }

    [Fact]
    public void BacpacServeListsTablesAndReportsNoStorageCompression()
    {
        var res = RunOn("typeprobe.bacpac", null, """{"id": 1, "cmd": "tables"}""");
        var tables = Assert.Single(res).RootElement.GetProperty("tables").EnumerateArray().ToList();
        var dense = tables.Single(t => t.GetProperty("name").GetString() == "probe_dense");
        Assert.Equal(4000, dense.GetProperty("rows").GetInt64());
        Assert.Equal("-", dense.GetProperty("compression").GetString());
    }

    [Fact]
    public void BacpacServeErrorKeepsTheSessionAlive()
    {
        var res = RunOn("typeprobe.bacpac", null,
            """{"id": 1, "cmd": "read", "table": "no_such_table"}""",
            """{"id": 2, "cmd": "read", "table": "probe_ghost", "select": "id,val", "top": 1}""");
        Assert.Equal(2, res.Count);
        Assert.False(res[0].RootElement.GetProperty("ok").GetBoolean());
        Assert.True(res[1].RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(res[1].RootElement.GetProperty("rows").EnumerateArray());
    }
}
