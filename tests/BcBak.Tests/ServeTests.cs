using System.Text.Json;
using BcBak;
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
            while (dir != null && !File.Exists(Path.Combine(dir, "BcBak.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir!;
        }
    }

    static List<JsonDocument> Run(params string[] requests)
    {
        using var pf = new PageFile(Path.Combine(Root, "fixtures", "typeprobe.bak"));
        var cat = new Catalog(pf);
        var input = new StringReader(string.Join("\n", requests));
        var output = new StringWriter();
        int rc = BcBak.Program.Serve(pf, cat, new Dictionary<string, string>(), input, output);
        Assert.Equal(0, rc);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l)).ToList();
    }

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
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t);
        Assert.Equal(8, tables["probe"].GetProperty("rows").GetInt64());
        Assert.Equal("page", tables["probe_ghost"].GetProperty("compression").GetString());
        Assert.Equal(JsonValueKind.Null, tables["probe"].GetProperty("company").ValueKind);
        Assert.True(res[1].RootElement.GetProperty("ok").GetBoolean());
        Assert.True(res[2].RootElement.GetProperty("ok").GetBoolean());
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
}
