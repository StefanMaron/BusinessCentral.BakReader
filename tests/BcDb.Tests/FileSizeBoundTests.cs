using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// The file-header "Size" column is a LOWER bound on the data file, not the end of the
/// copied data, and must never bound the region-1 extent walk.
///
/// PageFile used to break that walk at <c>firstPage &gt;= FilePages</c> with FilePages read
/// straight from the header. That held while the BC demo database had free space at the end
/// of its file — on 28.1 Size (116,240) sits 2,060 pages ABOVE the last allocated page — and
/// silently truncated the map the moment it did not: on 28.2, 28.3 and 28.4 the copy carries
/// extents past the recorded Size, the walk stopped early, and VerifyFillerTail then refused
/// the real pages nobody had mapped ("block 116504 of MSDA region is neither mapped by the
/// derived extent list nor padding filler"). See PROVENANCE.md, "Data-copy layout".
///
/// The hermetic half runs everywhere. The demo-backup half is artifact-gated the same way
/// SymbolTests.LoadsShippedBaseApplication is, because reproducing the truncation needs a
/// backup whose content has outgrown its header Size and the smallest of those is ~900 MB.
/// </summary>
public class FileSizeBoundTests
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

    /// <summary>
    /// The invariant, on the committed fixture: the derived file length covers every page the
    /// map placed. A FilePages taken from the header alone cannot promise this — that is the
    /// whole defect — so asserting it is asserting the fix, not the fixture.
    /// </summary>
    [Fact]
    public void DerivedFileLengthCoversEveryMappedPage()
    {
        using var pf = new PageFile(Path.Combine(Root, "fixtures", "typeprobe.bak"));
        Assert.True(pf.PageCount > 0, "fixture mapped no pages");
        Assert.True(pf.FilePages >= pf.PageCount,
            $"derived FilePages {pf.FilePages} is below the {pf.PageCount} pages the map placed");
        Assert.Equal(0, pf.FilePages % 8);
    }

    /// <summary>
    /// BC 28.4's demo backup, the case that refused outright before the fix. Concrete counts,
    /// not a "does not throw": the map must reach block 119,144 (14,893 extents) where the
    /// header Size would have stopped it at 116,504, and page 1:116512 — the exact page id the
    /// old refusal reported, one page past the recorded Size — must be mapped and readable.
    /// </summary>
    [SkippableFact]
    public void Bc284DemoBackup_MapsExtentsPastTheHeaderSize()
    {
        var bak = FindDemoBackup("28.4.53241.54318");
        Skip.If(bak is null, "BC 28.4 demo backup not present");

        using var pf = new PageFile(bak!);

        // Header Size reads 116,512 pages; the copy carries 119,144 blocks of real extents.
        Assert.Equal(119144, pf.PageCount);
        Assert.True(pf.FilePages >= 119152,
            $"derived FilePages {pf.FilePages} does not cover the last mapped extent");
        Assert.Equal(1, pf.GamIntervalCount);

        // The first page beyond the header's Size: unmapped before the fix, which is what
        // VerifyFillerTail refused on. Type 2 (index page) per its own header.
        var page = pf.GetPage(1, 116512);
        Assert.Equal(1, page[0]);
        Assert.Equal(2, page[1]);
    }

    /// <summary>
    /// The 28.1 control, which read correctly before the fix and must be unchanged by it:
    /// its header Size (116,240) sits above the last allocated page, so the old bound never
    /// bit and the derived map is the same map.
    /// </summary>
    [SkippableFact]
    public void Bc281DemoBackup_IsUnchangedByTheDerivedBound()
    {
        var bak = FindDemoBackup("28.1.49838.54308") ?? FindDemoBackup("28.1.49838.50621");
        Skip.If(bak is null, "BC 28.1 demo backup not present");

        using var pf = new PageFile(bak!);
        Assert.Equal(114176, pf.PageCount);
        // Free tail: the header legitimately claims more pages than the copy carries, and the
        // derived length must keep the header's larger figure rather than shrink to the data.
        Assert.Equal(116240, pf.FilePages);
    }

    /// <summary>
    /// The demo backups live in the BC artifact cache for verify.sh, and in the AL Runner
    /// test-data cache when they were fetched by tools/DownloadArtifacts. Look in both.
    /// </summary>
    static string? FindDemoBackup(string version)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[]
                 {
                     Path.Combine(home, ".bcartifacts.cache", "sandbox", version, "w1", "BusinessCentral-W1.bak"),
                     Path.Combine(home, ".al-runner", "test-data", version, "BusinessCentral-W1.bak"),
                 })
            if (File.Exists(p)) return p;
        return null;
    }
}
