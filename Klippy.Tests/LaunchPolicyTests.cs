using System;
using System.Collections.Generic;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// What an unmatched search is taken to be, driven directly rather than through a window.
///
/// The filesystem is described rather than built: these are assertions about Windows paths,
/// and a test that created a real directory could only ever make one platform's shape exist.
/// </summary>
public class LaunchPolicyTests
{
    /// <summary>A filesystem that holds exactly what the test says it holds.</summary>
    private static LaunchProbe Probe(IEnumerable<string>? folders = null, IEnumerable<string>? files = null)
    {
        var dirs = new HashSet<string>(folders ?? [], StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(files ?? [], StringComparer.OrdinalIgnoreCase);
        return new LaunchProbe(dirs.Contains, names.Contains);
    }

    /// <summary>Nothing exists, so only the kinds that need no disk can come back.</summary>
    private static readonly LaunchProbe Empty = Probe();

    // ---- nothing to run ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("send log files")]
    [InlineData("slf")]
    [InlineData("restarting")]          // the word is not the command
    [InlineData("please restart")]      // nor is a sentence containing it
    [InlineData("readme.md")]           // relative: relative to what?
    [InlineData("docs/readme.md")]      // still relative
    [InlineData("C:")]                  // drive-relative, not a place
    public void OrdinarySearchesAreNotRunnable(string query)
    {
        var target = LaunchPolicy.Parse(query, probe: Empty);

        Assert.Equal(LaunchKind.None, target.Kind);
        Assert.False(target.IsRunnable);
    }

    // ---- OS controls ----

    [Theory]
    [InlineData("lock", SystemAction.Lock)]
    [InlineData("Sleep", SystemAction.Sleep)]
    [InlineData("HIBERNATE", SystemAction.Hibernate)]
    [InlineData("  restart  ", SystemAction.Restart)]
    public void TheFourOsControlsAreRecognised(string query, SystemAction expected)
    {
        var target = LaunchPolicy.Parse(query, probe: Empty);

        Assert.Equal(LaunchKind.System, target.Kind);
        Assert.Equal(expected, target.Action);
        // Nothing to hand the shell: the action names itself.
        Assert.Equal("", target.Target);
    }

    // ---- URLs ----

    [Fact]
    public void HttpsUrlsAreOffered()
    {
        var target = LaunchPolicy.Parse("https://github.com/seankearon/Klippy", probe: Empty);

        Assert.Equal(LaunchKind.Url, target.Kind);
        Assert.Equal("https://github.com/seankearon/Klippy", target.Target);
    }

    [Fact]
    public void BareWwwHostsBecomeHttps()
    {
        // The point of accepting www. at all: nobody types the scheme.
        var target = LaunchPolicy.Parse("www.bbc.co.uk/news", probe: Empty);

        Assert.Equal(LaunchKind.Url, target.Kind);
        Assert.Equal("https://www.bbc.co.uk/news", target.Target);
    }

    [Theory]
    [InlineData("http://example.com")]          // plaintext, deliberately not offered
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/Windows")]          // the shell would happily open it
    [InlineData("javascript:alert(1)")]
    [InlineData("shell:startup")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("https://")]                    // a scheme is not a URL
    [InlineData("www.")]
    [InlineData("https://localhost")]           // no dot: not what a filter box meant
    [InlineData("www.qwe.com and more words")]  // a sentence that starts with a host
    public void EverySchemeButHttpsIsLeftAlone(string query)
    {
        Assert.Equal(LaunchKind.None, LaunchPolicy.Parse(query, probe: Empty).Kind);
    }

    // ---- paths ----

    [Fact]
    public void AFolderThatExistsOpensAsAFolder()
    {
        var probe = Probe(folders: [@"C:\work\invoices"]);

        var target = LaunchPolicy.Parse(@"C:\work\invoices", probe: probe);

        Assert.Equal(LaunchKind.Folder, target.Kind);
        Assert.Equal(@"C:\work\invoices", target.Target);
    }

    [Theory]
    [InlineData(@"\\nas\share\build")]  // UNC
    [InlineData("/usr/local/bin")]      // unix absolute
    [InlineData("D:/media")]            // drive with a forward slash
    public void EveryShapeOfRootedPathIsRecognised(string path)
    {
        Assert.Equal(LaunchKind.Folder, LaunchPolicy.Parse(path, probe: Probe(folders: [path])).Kind);
    }

    [Fact]
    public void AFileThatExistsRuns()
    {
        var probe = Probe(files: [@"C:\tools\deploy.ps1"]);

        var target = LaunchPolicy.Parse(@"C:\tools\deploy.ps1", probe: probe);

        Assert.Equal(LaunchKind.File, target.Kind);
        Assert.True(LaunchPolicy.IsExecutable(target.Target));
    }

    [Fact]
    public void ADocumentIsAFileToo_ButNotOneToRun()
    {
        // Same kind, different verb on the offer: the shell opens it with whatever owns it.
        var target = LaunchPolicy.Parse(@"C:\work\notes.txt", probe: Probe(files: [@"C:\work\notes.txt"]));

        Assert.Equal(LaunchKind.File, target.Kind);
        Assert.False(LaunchPolicy.IsExecutable(target.Target));
    }

    // ---- verification ----

    [Fact]
    public void VerifyOn_MeansAPathThatIsNotThereIsNotOffered()
    {
        Assert.Equal(LaunchKind.None,
            LaunchPolicy.Parse(@"C:\work\gone.exe", verifyPaths: true, probe: Empty).Kind);
    }

    [Fact]
    public void VerifyOff_OffersItAnyway_AndTheShapeDecidesWhatItIs()
    {
        // For the share that is slow to answer, or the path that does not exist yet. The OS
        // then reports the failure, which is the trade the setting makes.
        Assert.Equal(LaunchKind.File,
            LaunchPolicy.Parse(@"C:\work\gone.exe", verifyPaths: false, probe: Empty).Kind);
        Assert.Equal(LaunchKind.Folder,
            LaunchPolicy.Parse(@"C:\work\gone\", verifyPaths: false, probe: Empty).Kind);
    }

    [Fact]
    public void VerifyOff_StillAsksTheDisk_SoAFolderIsStillCalledAFolder()
    {
        // Verification decides whether to refuse, not whether to look: without the probe a
        // real folder with no trailing separator would be offered as "Run".
        var target = LaunchPolicy.Parse(@"C:\work", verifyPaths: false, probe: Probe(folders: [@"C:\work"]));

        Assert.Equal(LaunchKind.Folder, target.Kind);
    }

    [Fact]
    public void VerifyOff_DoesNotTurnEveryUnmatchedWordIntoAProgram()
    {
        // The rooted-path rule is what stops this, and it has to hold with checking off.
        Assert.Equal(LaunchKind.None,
            LaunchPolicy.Parse("send log files", verifyPaths: false, probe: Empty).Kind);
        Assert.Equal(LaunchKind.None,
            LaunchPolicy.Parse("setup.exe", verifyPaths: false, probe: Empty).Kind);
    }

    // ---- environment variables ----

    [Fact]
    public void WindowsStyleVariablesExpand()
    {
        using var _ = EnvVar("KLIPPY_TEST_DIR", @"C:\Users\sean\AppData\Roaming");

        var target = LaunchPolicy.Parse("%KLIPPY_TEST_DIR%",
            probe: Probe(folders: [@"C:\Users\sean\AppData\Roaming"]));

        Assert.Equal(LaunchKind.Folder, target.Kind);
        Assert.Equal(@"C:\Users\sean\AppData\Roaming", target.Target);
    }

    [Fact]
    public void AVariableMayBeJustTheStartOfThePath()
    {
        using var _ = EnvVar("KLIPPY_TEST_DIR", @"C:\Users\sean\AppData\Roaming");

        var target = LaunchPolicy.Parse(@"%KLIPPY_TEST_DIR%\Klippy",
            probe: Probe(folders: [@"C:\Users\sean\AppData\Roaming\Klippy"]));

        Assert.Equal(LaunchKind.Folder, target.Kind);
    }

    [Theory]
    [InlineData("$KLIPPY_TEST_DIR")]
    [InlineData("${KLIPPY_TEST_DIR}")]
    public void UnixStyleVariablesExpandToo(string query)
    {
        using var _ = EnvVar("KLIPPY_TEST_DIR", "/Users/sean/Library");

        Assert.Equal(LaunchKind.Folder,
            LaunchPolicy.Parse(query, probe: Probe(folders: ["/Users/sean/Library"])).Kind);
    }

    [Fact]
    public void TildeMeansHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotEqual("", home); // otherwise the test below proves nothing

        Assert.Equal(LaunchKind.Folder, LaunchPolicy.Parse("~", probe: Probe(folders: [home])).Kind);
    }

    [Fact]
    public void AnUnknownVariableIsLeftAsWritten()
    {
        // Not expanded to nothing: "%nope%\Klippy" becoming "\Klippy" would offer to open a
        // folder at the root of the current drive, which is not what anybody typed.
        Assert.Equal(@"%KLIPPY_NO_SUCH_VAR%\Klippy", LaunchPolicy.Expand(@"%KLIPPY_NO_SUCH_VAR%\Klippy"));
        Assert.Equal(LaunchKind.None,
            LaunchPolicy.Parse(@"%KLIPPY_NO_SUCH_VAR%\Klippy", verifyPaths: false, probe: Empty).Kind);
    }

    [Fact]
    public void TextThatMerelyContainsAPercentIsUntouched()
    {
        Assert.Equal("50% off", LaunchPolicy.Expand("50% off"));
        Assert.Equal("$", LaunchPolicy.Expand("$"));
    }

    // ---- precedence ----

    [Fact]
    public void AnOsControlOutranksAPathThatHappensToExist()
    {
        // "sleep" is also a real file on plenty of Unix boxes. The word wins, because the
        // word is what someone typing four letters into a launcher meant.
        var target = LaunchPolicy.Parse("sleep", probe: Probe(files: ["sleep"]));

        Assert.Equal(LaunchKind.System, target.Kind);
    }

    /// <summary>Sets an environment variable for the duration of a test.</summary>
    private static IDisposable EnvVar(string name, string value)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        return new Restore(() => Environment.SetEnvironmentVariable(name, previous));
    }

    private sealed class Restore(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
