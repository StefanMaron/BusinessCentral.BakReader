using System.Text.Json;
using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// The request surface itself: how a caller names a column on --select / --sha256, and
/// which keys a serve request may carry. Both are places where an unrecognized name used
/// to be dropped without a word, so a typo produced a plausible wrong answer instead of
/// an error (GitHub issues #15 and #16). Driven against fixtures/typeprobe.bak and
/// fixtures/typeprobe.bacpac, so both containers are covered.
/// </summary>
public class RequestSurfaceTests
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

    static JsonElement One(string fixture, params string[] requests)
        => All(fixture, requests).Single();

    static List<JsonElement> All(string fixture, params string[] requests)
    {
        using var src = BcSource.Open(Path.Combine(Root, "fixtures", fixture));
        var output = new StringWriter();
        Assert.Equal(0, BusinessCentral.DbReader.Program.Serve(src, new Dictionary<string, string>(),
            new StringReader(string.Join("\n", requests)), output));
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
    }

    static string Error(JsonElement r)
    {
        Assert.False(r.GetProperty("ok").GetBoolean());
        return r.GetProperty("error").GetString()!;
    }

    // ---- #16: column names on --select and --sha256 --------------------------------

    [Theory]
    [InlineData("typeprobe.bak")]
    [InlineData("typeprobe.bacpac")]
    public void ColumnNameWithATrailingSpaceCanBeSelected(string fixture)
    {
        // probe_oddnames has the columns [pad ] and [ pad]. A token is matched as written
        // before it is trimmed, so "pad " reaches the trailing-space column.
        var r = One(fixture, """{"id":1,"cmd":"read","table":"probe_oddnames","select":"id,pad "}""");
        Assert.True(r.GetProperty("ok").GetBoolean());
        var rows = r.GetProperty("rows").EnumerateArray().ToDictionary(x => x[0].GetInt64(), x => x);
        Assert.Equal("trailing-one", rows[1][1].GetString());
        Assert.Equal(JsonValueKind.Null, rows[2][1].ValueKind);
    }

    [Theory]
    [InlineData("typeprobe.bak")]
    [InlineData("typeprobe.bacpac")]
    public void ColumnNameWithALeadingSpaceCanBeSelected(string fixture)
    {
        // "id, pad" splits into "id" and " pad": the second token, as written, is the name.
        var r = One(fixture, """{"id":1,"cmd":"read","table":"probe_oddnames","select":"id, pad"}""");
        Assert.True(r.GetProperty("ok").GetBoolean());
        var rows = r.GetProperty("rows").EnumerateArray().ToDictionary(x => x[0].GetInt64(), x => x);
        Assert.Equal("leading-one", rows[1][1].GetString());
        Assert.Equal("leading-two", rows[2][1].GetString());
    }

    [Theory]
    [InlineData("typeprobe.bak")]
    [InlineData("typeprobe.bacpac")]
    public void SpaceAfterACommaStillMeansNothing(string fixture)
    {
        // The reason the trim exists: "A, B" must keep working where no column is named
        // with a space. Trimming is the fallback, so both spellings reach `amt`.
        foreach (var sel in new[] { "id,amt", "id, amt" })
        {
            var r = One(fixture, $$"""{"id":1,"cmd":"read","table":"probe_oddnames","select":"{{sel}}"}""");
            Assert.True(r.GetProperty("ok").GetBoolean());
            Assert.Equal(new[] { "id", "amt" },
                r.GetProperty("headers").EnumerateArray().Select(h => h.GetString()).ToArray());
        }
    }

    [Fact]
    public void UnknownColumnStillReportsTheNameAsWritten()
    {
        var e = Error(One("typeprobe.bak", """{"id":1,"cmd":"read","table":"probe","select":"c_nosuch"}"""));
        Assert.Contains("c_nosuch", e);
        Assert.Contains("not found", e);
    }

    [Theory]
    [InlineData("typeprobe.bak")]
    [InlineData("typeprobe.bacpac")]
    public void Sha256NamingNoSelectedColumnIsRefused(string fixture)
    {
        // Silently doing nothing here returns the raw value where a hash was asked for —
        // a fixture built that way compares hex against a hash and fails misleadingly.
        var e = Error(One(fixture,
            """{"id":1,"cmd":"read","table":"probe_lob","select":"id,c_vbmax","sha256":"NoSuchColumn"}"""));
        Assert.Contains("NoSuchColumn", e);
        Assert.Contains("sha256", e);
    }

    [Fact]
    public void Sha256NamingAColumnThatIsNotSelectedIsRefused()
    {
        var e = Error(One("typeprobe.bak",
            """{"id":1,"cmd":"read","table":"probe_lob","select":"id","sha256":"c_vbmax"}"""));
        Assert.Contains("c_vbmax", e);
    }

    [Theory]
    [InlineData("typeprobe.bak")]
    [InlineData("typeprobe.bacpac")]
    public void Sha256StillHashesTheColumnItNames(string fixture)
    {
        var r = One(fixture, """{"id":1,"cmd":"read","table":"probe_lob","select":"id,c_vbmax","sha256":"c_vbmax"}""");
        Assert.True(r.GetProperty("ok").GetBoolean());
        var rows = r.GetProperty("rows").EnumerateArray().ToDictionary(x => x[0].GetInt64(), x => x);
        // oracle-known: row 1 is the empty value, so its SHA-256 is the empty-input hash
        Assert.Equal("sha256:E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
            rows[1][1].GetString());
    }

    // ---- #15: serve request keys ---------------------------------------------------

    [Fact]
    public void UnknownRequestKeyIsRefused()
    {
        var e = Error(One("typeprobe.bak",
            """{"id":1,"cmd":"read","table":"probe","select":"id","totallyBogusKey":"xyz"}"""));
        Assert.Contains("totallyBogusKey", e);
        Assert.Contains("select", e);          // the message lists what is accepted
    }

    [Fact]
    public void MisspelledOptionIsRefusedRatherThanDropped()
    {
        // "tpo" for "top" used to answer ok:true with every row.
        var e = Error(One("typeprobe.bak", """{"id":1,"cmd":"read","table":"probe","tpo":2}"""));
        Assert.Contains("tpo", e);
    }

    [Fact]
    public void MisspelledCompanyKeyIsRefusedRatherThanReadingEveryCompany()
    {
        var e = Error(One("typeprobe.bak", """{"id":1,"cmd":"read","table":"ambig","compayn":"ProbeCo"}"""));
        Assert.Contains("compayn", e);
    }

    [Fact]
    public void CamelCaseSpellingOfMergeExtensionsIsRefusedAndTheRealKeyIsNamed()
    {
        var e = Error(One("typeprobe.bak",
            """{"id":1,"cmd":"read","table":"exttest","mergeExtensions":true}"""));
        Assert.Contains("mergeExtensions", e);
        Assert.Contains("merge-extensions", e);
    }

    [Fact]
    public void KeyThatIsRealButMeaninglessForTheCommandIsRefused()
    {
        // Same failure as an unknown key: the caller asked for something and it was dropped.
        var e = Error(One("typeprobe.bak", """{"id":1,"cmd":"tables","table":"probe"}"""));
        Assert.Contains("table", e);
        Assert.Contains("tables", e);
    }

    [Fact]
    public void AcceptedKeysStillWorkAndTheSessionSurvivesARefusal()
    {
        var res = All("typeprobe.bak",
            """{"id":1,"cmd":"read","table":"probe","select":"id","nope":1}""",
            """{"id":2,"cmd":"read","table":"probe","select":"id","top":2}""",
            """{"id":3,"cmd":"tables"}""",
            """{"id":4,"cmd":"companies"}""");
        Assert.Equal(4, res.Count);
        Assert.False(res[0].GetProperty("ok").GetBoolean());
        Assert.True(res[1].GetProperty("ok").GetBoolean());
        Assert.Equal(2, res[1].GetProperty("rows").GetArrayLength());
        Assert.True(res[2].GetProperty("ok").GetBoolean());
        Assert.True(res[3].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void IdAndCmdAreAlwaysAccepted()
    {
        var r = One("typeprobe.bak", """{"id":"abc","cmd":"companies"}""");
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal("abc", r.GetProperty("id").GetString());
    }

    [Fact]
    public void QuitTakesNoOtherKeys()
    {
        var res = All("typeprobe.bak", """{"id":1,"cmd":"quit","table":"probe"}""");
        Assert.Contains("table", Error(res[0]));
    }

    // ---- #18: the same treatment for command-line flags ----------------------------
    // The CLI dropped any --flag it did not recognize, so a typo produced a plausible
    // wrong answer with exit 0: --compayn read every company, --tpo returned every row,
    // --mergeExtensions returned the base table's columns and none of the extension's.

    static string Refused(string cmd, params string[] args)
        => Assert.Throws<ArgumentException>(() => Program.ParseOpts(args, cmd)).Message;

    [Fact]
    public void UnknownCliFlagIsRefusedAndTheAcceptedOnesAreNamed()
    {
        string e = Refused("read", "--table", "probe", "--totallyBogusFlag");
        Assert.Contains("totallyBogusFlag", e);
        Assert.Contains("--table", e);
        Assert.Contains("--merge-extensions", e);
    }

    [Theory]
    [InlineData("compayn", "CRONUS")]      // --company
    [InlineData("tpo", "2")]               // --top
    [InlineData("mergeExtensions", null)]  // --merge-extensions, wrong casing
    public void MisspelledCliFlagIsRefusedRatherThanDropped(string flag, string? value)
    {
        var args = value is null
            ? new[] { "--table", "probe", "--" + flag }
            : new[] { "--table", "probe", "--" + flag, value };
        Assert.Contains(flag, Refused("read", args));
    }

    [Fact]
    public void AFlagIsOnlyAcceptedByTheCommandsThatUseIt()
    {
        // --fixture belongs to verify, --against to validate; neither is a read option.
        Assert.Contains("fixture", Refused("read", "--table", "probe", "--fixture", "f.tsv"));
        Assert.Contains("against", Refused("read", "--table", "probe", "--against", "x.mdf"));
        Assert.Contains("select", Refused("tables", "--select", "id"));
        // and verify does accept both its own flag and the read options it passes through
        var ok = Program.ParseOpts(new[] { "--fixture", "f.tsv", "--table", "probe", "--top", "2" }, "verify");
        Assert.Equal("f.tsv", ok["fixture"]);
        Assert.Equal("2", ok["top"]);
    }

    [Fact]
    public void AValueTakingFlagWithoutAValueIsRefused()
    {
        // Both shapes: at the end of the line, and immediately followed by another flag.
        // Silently becoming the string "true" made --company search for a company named
        // "true", which surfaces as a confusing "table not found" instead of a syntax error.
        Assert.Contains("--company", Refused("read", "--table", "probe", "--company"));
        Assert.Contains("--company", Refused("read", "--company", "--top", "2"));
    }

    [Fact]
    public void ValuelessFlagsTakeNoValueAndPositionalArgumentsAreRefused()
    {
        var opts = Program.ParseOpts(new[] { "--merge-extensions", "--table", "probe" }, "read");
        Assert.Equal("true", opts["merge-extensions"]);
        Assert.Equal("probe", opts["table"]);
        Assert.Contains("probe", Refused("read", "probe"));
    }

    [Theory]
    [InlineData("typeprobe.bak")]
    [InlineData("typeprobe.bacpac")]
    public void TopRefusesAValueThatIsNotARowCount(string fixture)
    {
        // Checked where both surfaces meet, so a serve request gets the same message.
        var r = One(fixture, """{"id":1,"cmd":"read","table":"probe","select":"id","top":"abc"}""");
        string e = Error(r);
        Assert.Contains("top", e);
        Assert.Contains("abc", e);
    }

    [Fact]
    public void FormatRefusesAValueItDoesNotUnderstand()
    {
        string e = Refused("read", "--table", "probe", "--format", "yaml");
        Assert.Contains("yaml", e);
        Assert.Contains("json", e);
    }

    [Fact]
    public void MainFailsOnAnUnknownFlagAndSucceedsWithoutOne()
    {
        string bak = Path.Combine(Root, "fixtures", "typeprobe.bak");
        var good = new[] { "read", bak, "--table", "probe", "--select", "id", "--top", "1" };
        Assert.Equal(0, Program.Main(good));
        Assert.Equal(1, Program.Main(good.Append("--totallyBogusFlag").ToArray()));
    }
}
