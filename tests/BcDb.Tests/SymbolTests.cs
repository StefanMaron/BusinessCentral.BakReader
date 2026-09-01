using System.IO.Compression;
using System.Text;
using BusinessCentral.DbReader;
using Xunit;

public class SymbolTests
{
    const string SampleJson = """
    {
      "AppId": "11111111-2222-3333-4444-555555555555",
      "Name": "Test App",
      "Publisher": "T",
      "Tables": [
        {
          "Id": 50100, "Name": "Top Level",
          "Fields": [
            { "Id": 1, "Name": "No.", "TypeDefinition": { "Name": "Code[20]" } },
            { "Id": 2, "Name": "Sum", "TypeDefinition": { "Name": "Decimal" },
              "Properties": [ { "Name": "FieldClass", "Value": "FlowField" } ] }
          ]
        }
      ],
      "Namespaces": [
        { "Name": "Inner",
          "Tables": [
            { "Id": 50101, "Name": "Nested Table",
              "Fields": [
                { "Id": 1, "Name": "Type", "TypeDefinition": { "Name": "Enum", "Subtype": { "Name": "My Enum", "Id": 50102 } } }
              ] }
          ],
          "Namespaces": [
            { "Name": "Deep", "Tables": [ { "Id": 50103, "Name": "Deep/One", "Fields": [] } ] }
          ]
        }
      ]
    }
    """;

    static SymbolStore FromJson(string json)
    {
        var f = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(f, json);
        try { return SymbolStore.Load(new[] { f }); }
        finally { File.Delete(f); }
    }

    [Fact]
    public void WalksNamespacesRecursively()
    {
        var s = FromJson(SampleJson);
        Assert.Equal(3, s.Tables.Count);
        Assert.Single(s.Find("Top Level"));
        Assert.Single(s.Find("Nested Table"));
        Assert.Single(s.Find("Deep/One"));
    }

    [Fact]
    public void MapsAlNamesAndTypes()
    {
        var s = FromJson(SampleJson);
        var t = s.Find("Top Level")[0];
        Assert.Equal(50100, t.Id);
        Assert.Equal("Test App", t.AppName);
        Assert.Equal("Code[20]", t.Fields[0].TypeName);
        Assert.Equal("FlowField", t.Fields[1].FieldClass);
        var e = s.Find("Nested Table")[0];
        Assert.Equal("Enum \"My Enum\"", e.Fields[0].TypeName);
    }

    [Fact]
    public void FindForSqlTableUsesBcNormalization()
    {
        var s = FromJson(SampleJson);
        // '/' becomes '_' in SQL object names
        Assert.NotNull(s.FindForSqlTable("Deep_One", "11111111-2222-3333-4444-555555555555"));
        Assert.Null(s.FindForSqlTable("Deep_One", "99999999-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void SqlNameNormalization()
    {
        Assert.Equal("No_", SqlNames.Normalize("No."));
        Assert.Equal("G_L Account", SqlNames.Normalize("G/L Account"));
        Assert.Equal("A_B_C_D_E_F_G_H", SqlNames.Normalize("A.B\"C\\D/E'F%G[H"));
    }

    [Fact]
    public void LoadsNavxPackagedApp()
    {
        // Build a minimal NAVX .app: 40-byte header (magic + header length), then a zip
        // containing SymbolReference.json — the packaging observed on shipped BC apps.
        using var zipMs = new MemoryStream();
        using (var zip = new ZipArchive(zipMs, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("SymbolReference.json").Open());
            w.Write(SampleJson);
        }
        var navx = new byte[40 + zipMs.Length];
        Encoding.ASCII.GetBytes("NAVX").CopyTo(navx, 0);
        BitConverter.GetBytes(40).CopyTo(navx, 4);
        zipMs.ToArray().CopyTo(navx, 40);
        var f = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".app");
        File.WriteAllBytes(f, navx);
        try
        {
            var s = SymbolStore.Load(new[] { f });
            Assert.Equal(3, s.Tables.Count);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void MissingAppIdThrows()
        => Assert.Throws<InvalidDataException>(() => FromJson("""{ "Name": "x", "Tables": [] }"""));

    [SkippableFact]
    public void LoadsShippedBaseApplication()
    {
        var app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".bcartifacts.cache/sandbox/28.1.49838.50621/w1/Extensions/Microsoft_Base Application_28.1.49838.50621.app");
        Skip.If(!File.Exists(app), "BC artifact not present");
        var s = SymbolStore.Load(new[] { app });
        var cust = s.Find("Customer");
        Assert.Single(cust);
        Assert.Equal(18, cust[0].Id);
        Assert.Equal("437dbf0e-84ff-417a-965d-ed2bb9650972", cust[0].AppId);
        Assert.Equal("Code[20]", cust[0].Fields.Single(f => f.Id == 1).TypeName);
    }
}
