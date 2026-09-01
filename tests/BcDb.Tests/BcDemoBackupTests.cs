using BusinessCentral.DbReader;
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

    [SkippableTheory]
    [InlineData(Bak275, 3774)]
    [InlineData(Bak281, 3955)]
    public void ClusteredSeekAgreesWithTheFullScanOnEveryTable(string rel, int expectedTables)
    {
        // The hermetic version of this runs against the 22 probe tables. Real BC databases
        // are what make it worth trusting: multi-level indexes (sysrscols is three levels
        // deep on 28.1), object ids spread over the whole int range, and objects whose
        // columns span many leaf pages. A seek that disagrees with the scan here is a
        // table silently getting another table's schema.
        var path = Find(rel);
        Skip.If(path is null, $"BC demo backup not present: ~/{rel}");
        using var pf = new PageFile(path!);

        var scanned = new Catalog(pf);
        scanned.LoadColumnMetadata();
        var seeking = new Catalog(pf);

        int rowsets = 0;
        foreach (var obj in scanned.Objects.Values.OrderBy(o => o.ObjectId))
        {
            seeking.LoadColumnMetadata(obj.ObjectId);
            scanned.Columns.TryGetValue(obj.ObjectId, out var wantCols);
            seeking.Columns.TryGetValue(obj.ObjectId, out var gotCols);
            Assert.Equal(wantCols ?? new List<SysColumn>(), gotCols ?? new List<SysColumn>());
            scanned.IndexColumns.TryGetValue(obj.ObjectId, out var wantIdx);
            seeking.IndexColumns.TryGetValue(obj.ObjectId, out var gotIdx);
            Assert.Equal(wantIdx ?? new List<SysIndexCol>(), gotIdx ?? new List<SysIndexCol>());

            long rsid;
            try { rsid = scanned.RowsetFor(obj.ObjectId, 1, 0).RowSetId; }
            catch (InvalidDataException) { continue; }      // internal object with no rowset
            Assert.Equal(scanned.RowsetColumnsByScan(rsid), seeking.RowsetColumns(rsid));
            rowsets++;
        }

        Assert.Equal(expectedTables, rowsets);
        // Every one of those lookups must actually have been a seek: a silent fall back to
        // scanning would still pass every assertion above and quietly undo the point.
        Assert.Equal(0, seeking.ClusteredSeekDeclines);
        Assert.True(seeking.ClusteredSeeks >= rowsets, $"only {seeking.ClusteredSeeks} seeks for {rowsets} rowsets");
    }
}
