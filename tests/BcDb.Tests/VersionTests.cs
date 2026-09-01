using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// `bcdb --version`. Once binaries are downloaded from a release, a report that the
/// reader decoded something wrongly is only actionable if the binary can say which build
/// it is — and which flavor, because a Native AOT build and a JIT build of the same commit
/// have very different performance characteristics and a user comparing timings needs to
/// know which one they ran.
/// </summary>
public class VersionTests
{
    [Fact]
    public void VersionNamesTheToolTheVersionThePlatformAndTheBuildFlavor()
    {
        var output = new StringWriter();
        Assert.Equal(0, Program.Version(output));
        var line = output.ToString().TrimEnd('\n', '\r');

        // The version is asserted concretely rather than against the assembly's own
        // attribute: read back from the assembly this passes even when the csproj carries
        // no <Version> at all and the SDK's "1.0.0" placeholder is what ships. Bumping the
        // release version is meant to require touching this line.
        Assert.Matches(new Regex(@"^bcdb 0\.1\.0(\+[0-9a-f]+)? \(.+, (native aot|jit)\)$"), line);

        // The platform is the running RID, not a compile-time guess.
        Assert.Contains(RuntimeInformation.RuntimeIdentifier, line);

        // The test suite runs against `dotnet build`, which is the JIT build.
        Assert.Contains("jit", line);
        Assert.DoesNotContain("native aot", line);
    }

    [Fact]
    public void VersionIsAskedForWithoutAFileAndSucceeds()
    {
        // Every other command takes a file as its second argument; --version must not be
        // dragged into that check and answered with the usage screen and exit 64.
        var saved = Console.Out;
        try
        {
            var captured = new StringWriter();
            Console.SetOut(captured);
            Assert.Equal(0, Program.Main(new[] { "--version" }));
            Assert.StartsWith("bcdb ", captured.ToString());
        }
        finally { Console.SetOut(saved); }
    }

    [Theory]
    [InlineData("--versionn")]
    [InlineData("-version")]
    [InlineData("version")]
    public void AMisspelledVersionFlagIsNotQuietlyTreatedAsVersion(string arg)
    {
        // Loud failures: a near-miss is the usage screen and a non-zero exit, never a
        // version banner that makes a mistyped command look like it worked.
        var saved = Console.Out;
        try
        {
            var captured = new StringWriter();
            Console.SetOut(captured);
            Assert.Equal(64, Program.Main(new[] { arg }));
            Assert.DoesNotContain("bcdb 0.", captured.ToString());
        }
        finally { Console.SetOut(saved); }
    }
}
