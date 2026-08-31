using BcBak;
using Xunit;

/// <summary>
/// Full-file tests against the real BC demo backups from the BC artifact cache.
/// These files are ~900 MB and are NOT in the repository; when absent the tests
/// report as SKIPPED (never silently passed) via Skip.If. verify.sh is the
/// stricter local gate that FAILS when they are absent.
/// </summary>
public class BcDemoBackupTests
{
    const string Bak275 = ".bcartifacts.cache/sandbox/27.5.46862.48827/w1/BusinessCentral-W1.bak";
    const string Bak281 = ".bcartifacts.cache/sandbox/28.1.49838.50621/w1/BusinessCentral-W1.bak";

    static string? Find(string rel)
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rel);
        return File.Exists(p) ? p : null;
    }

    [SkippableTheory]
    [InlineData(Bak275, 109_984, 320, 20)]
    [InlineData(Bak281, 114_120, 56, 9)]
    public void StructuralMapAndCrossCheck(string rel, int pages, int superseded, int lastWinsWrong)
    {
        var path = Find(rel);
        Skip.If(path is null, $"BC demo backup not present: ~/{rel}");
        using var pf = new PageFile(path!);
        Assert.Equal(pages, pf.PageCount);
        Assert.Equal(superseded, pf.SupersededPageCount);
        var (_, _, _, disagreements) = pf.CrossCheck();
        // The "last self-identified image wins" heuristic picks a stale image for
        // exactly these pages; the structural map matches RESTORE on all of them
        // (validated against fresh restores, PROVENANCE.md).
        Assert.Equal(lastWinsWrong, disagreements.Count);
    }

    [SkippableTheory]
    [InlineData(Bak275, "CRONUS International Ltd_")]
    [InlineData(Bak281, "CRONUS International Ltd_", "My Company")]
    public void EnumeratesCompanies(string rel, params string[] expected)
    {
        var path = Find(rel);
        Skip.If(path is null, $"BC demo backup not present: ~/{rel}");
        using var pf = new PageFile(path!);
        var cat = new Catalog(pf);
        var companies = cat.Objects.Values
            .Where(o => o.Type == "U" && o.Name.EndsWith("$437dbf0e-84ff-417a-965d-ed2bb9650972", StringComparison.Ordinal))
            .Select(o => o.Name.Split('$')[0]).Where(c => c.Length > 0).Distinct().ToHashSet();
        foreach (var c in expected) Assert.Contains(c, companies);
    }
}
