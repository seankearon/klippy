using System;
using System.Collections.Generic;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// What a search that matched nothing is taken to mean, driven directly rather than
/// through a window.
///
/// The platform is a parameter and the filesystem is described rather than built — these
/// are assertions about Windows paths, and a test that made real directories could only
/// ever exercise the one platform CI happens to run on.
/// </summary>
public class UnmatchedSearchTests
{
    private const ExecutionPlatform Windows = ExecutionPlatform.Windows;
    private const ExecutionPlatform Mac = ExecutionPlatform.MacOS;
    private const ExecutionPlatform Linux = ExecutionPlatform.Linux;

    /// <summary>A filesystem holding exactly what the test says it holds.</summary>
    private static PathProbe Probe(IEnumerable<string>? folders = null, IEnumerable<string>? files = null)
    {
        var dirs = new HashSet<string>(folders ?? [], StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(files ?? [], StringComparer.OrdinalIgnoreCase);
        return new PathProbe(dirs.Contains, names.Contains);
    }

    /// <summary>Nothing exists, so only the kinds that need no disk can come back.</summary>
    private static readonly PathProbe Empty = Probe();

    private static ExecutionPlan Plan(string query, bool verifyPaths = true,
        ExecutionPlatform platform = Windows, PathProbe? probe = null) =>
        UnmatchedSearch.Plan(query, verifyPaths, platform, probe ?? Empty);

    // ---- nothing to run ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("send log files")]
    [InlineData("slf")]
    [InlineData("restarting")]          // the word is not the command
    [InlineData("please restart")]      // nor is a sentence containing it
    [InlineData("setup.exe")]           // relative: relative to what?
    [InlineData("tools/build.ps1")]     // still relative
    [InlineData("C:")]                  // drive-relative, not a place
    public void OrdinarySearchesAreNotRunnable(string query)
    {
        Assert.Equal(ExecutionKind.None, Plan(query).Kind);
    }

    [Fact]
    public void ADocumentIsNotOfferedEvenWhenItIsThere()
    {
        // The allow-list is the point, and it is the app's, not this feature's: a path
        // that is neither script nor application is text, however real the file is.
        Assert.Equal(ExecutionKind.None,
            Plan(@"C:\work\notes.txt", probe: Probe(files: [@"C:\work\notes.txt"])).Kind);
    }

    // ---- machine controls ----

    [Theory]
    [InlineData("lock", SystemAction.Lock)]
    [InlineData("Sleep", SystemAction.Sleep)]
    [InlineData("HIBERNATE", SystemAction.Hibernate)]
    [InlineData("  restart  ", SystemAction.Restart)]
    public void TheFourMachineControlsAreRecognised(string query, SystemAction expected)
    {
        var plan = Plan(query);

        Assert.Equal(ExecutionKind.System, plan.Kind);
        Assert.Equal(expected, plan.Action);
        Assert.Equal("", plan.Target); // nothing to hand over: the action names itself
    }

    [Fact]
    public void HibernateIsNotOfferedOnMacOs_WhereItIsNotACommand()
    {
        // A sleep *mode* there, not something to ask for. Offering it and then refusing
        // would be worse than leaving the word an ordinary search.
        Assert.Equal(ExecutionKind.System, Plan("hibernate", platform: Linux).Kind);
        Assert.Equal(ExecutionKind.None, Plan("hibernate", platform: Mac).Kind);
        Assert.Equal(ExecutionKind.System, Plan("sleep", platform: Mac).Kind);
    }

    [Fact]
    public void EveryMachineControlResolvesToSomethingToStart()
    {
        // The whole point of planning these as processes: ProcessLauncher runs them with
        // no new path down it, so a gap here would be a silent no-op at the keyboard.
        foreach (var platform in new[] { Windows, Mac, Linux })
            foreach (var action in new[] { SystemAction.Lock, SystemAction.Sleep, SystemAction.Hibernate, SystemAction.Restart })
            {
                if (!ExecutionPolicy.Supports(action, platform)) continue;

                var plan = new ExecutionPlan { Kind = ExecutionKind.System, Action = action };
                Assert.NotNull(ExecutionPolicy.Resolve(plan, platform));
            }
    }

    // ---- links ----

    [Fact]
    public void HttpsLinksAreOffered()
    {
        var plan = Plan("https://github.com/seankearon/Klippy");

        Assert.Equal(ExecutionKind.Url, plan.Kind);
        Assert.Equal("https://github.com/seankearon/Klippy", plan.Target);
    }

    [Fact]
    public void BareWwwHostsBecomeHttps()
    {
        // The point of accepting www. at all: nobody types the scheme.
        var plan = Plan("www.bbc.co.uk/news");

        Assert.Equal(ExecutionKind.Url, plan.Kind);
        Assert.Equal("https://www.bbc.co.uk/news", plan.Target);
    }

    [Theory]
    [InlineData("http://example.com")]          // plaintext, deliberately narrower than a marked item
    [InlineData("mailto:someone@example.com")]  // likewise
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/Windows")]          // the shell would happily open it
    [InlineData("javascript:alert(1)")]
    [InlineData("shell:startup")]
    [InlineData("https://")]                    // a scheme is not a URL
    [InlineData("www.")]
    [InlineData("https://localhost")]           // no dot: not what a filter box meant
    [InlineData("www.qwe.com and more words")]  // a sentence that starts with a host
    public void TypedLinksAreHttpsAndWwwOnly(string query)
    {
        Assert.Equal(ExecutionKind.None, Plan(query).Kind);
    }

    [Fact]
    public void AMarkedItemStillTakesTheWiderList()
    {
        // The narrowing belongs to typed text, and must not have leaked into the rules a
        // snippet marked Execute goes through.
        Assert.Equal(ExecutionKind.Url, ExecutionPolicy.Plan("http://example.com").Kind);
        Assert.Equal(ExecutionKind.Url, ExecutionPolicy.Plan("mailto:someone@example.com").Kind);
    }

    // ---- paths ----

    [Fact]
    public void AFolderThatExistsOpensAsAFolder()
    {
        var plan = Plan(@"C:\work\invoices", probe: Probe(folders: [@"C:\work\invoices"]));

        Assert.Equal(ExecutionKind.Folder, plan.Kind);
        Assert.Equal(@"C:\work\invoices", plan.Target);
        Assert.Equal("Opening invoices", plan.Description);
    }

    [Theory]
    [InlineData(@"\\nas\share\build", Windows)]  // UNC
    [InlineData("/usr/local/bin", Linux)]        // unix absolute
    [InlineData("D:/media", Windows)]            // drive with a forward slash
    public void EveryShapeOfRootedPathIsRecognised(string path, ExecutionPlatform platform)
    {
        Assert.Equal(ExecutionKind.Folder,
            Plan(path, platform: platform, probe: Probe(folders: [path])).Kind);
    }

    [Fact]
    public void AScriptThatExistsRuns()
    {
        var plan = Plan(@"C:\tools\deploy.ps1", probe: Probe(files: [@"C:\tools\deploy.ps1"]));

        Assert.Equal(ExecutionKind.Script, plan.Kind);
        Assert.Equal(@"C:\tools\deploy.ps1", plan.Target);
    }

    [Fact]
    public void AnApplicationThatExistsStarts()
    {
        var plan = Plan(@"D:\apps\thing.exe", probe: Probe(files: [@"D:\apps\thing.exe"]));

        Assert.Equal(ExecutionKind.Application, plan.Kind);
    }

    [Fact]
    public void AMacBundleIsStarted_NotOpenedInFinder()
    {
        // A .app *is* a directory, so asking the disk before the extension would offer to
        // show Safari in Finder rather than to start it.
        var plan = Plan("/Applications/Safari.app", platform: Mac,
            probe: Probe(folders: ["/Applications/Safari.app"]));

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal("Starting Safari", plan.Description);
    }

    [Fact]
    public void SomethingThisPlatformCannotRunIsNotOffered()
    {
        var exe = Probe(files: [@"C:\apps\thing.exe"]);
        Assert.Equal(ExecutionKind.Application, Plan(@"C:\apps\thing.exe", probe: exe).Kind);
        Assert.Equal(ExecutionKind.None, Plan(@"C:\apps\thing.exe", platform: Mac, probe: exe).Kind);

        var bat = Probe(files: [@"C:\tools\go.bat"]);
        Assert.Equal(ExecutionKind.Script, Plan(@"C:\tools\go.bat", probe: bat).Kind);
        Assert.Equal(ExecutionKind.None, Plan(@"C:\tools\go.bat", platform: Linux, probe: bat).Kind);
    }

    // ---- verification ----

    [Fact]
    public void VerifyOn_MeansAPathThatIsNotThereIsNotOffered()
    {
        Assert.Equal(ExecutionKind.None, Plan(@"C:\work\gone.exe", verifyPaths: true).Kind);
    }

    [Fact]
    public void VerifyOff_OffersItAnyway_AndTheLauncherReportsTheFailure()
    {
        // For the share that is slow to answer, or the path that does not exist yet.
        Assert.Equal(ExecutionKind.Application, Plan(@"C:\work\gone.exe", verifyPaths: false).Kind);
        Assert.Equal(ExecutionKind.Script, Plan(@"C:\work\gone.ps1", verifyPaths: false).Kind);
        Assert.Equal(ExecutionKind.Folder, Plan(@"C:\work\gone\", verifyPaths: false).Kind);
    }

    [Fact]
    public void VerifyOff_StillAsksTheDisk_SoAFolderIsStillCalledAFolder()
    {
        // Verification decides whether to refuse, not whether to look: without the probe a
        // real folder with no trailing separator would not be recognised at all.
        Assert.Equal(ExecutionKind.Folder,
            Plan(@"C:\work", verifyPaths: false, probe: Probe(folders: [@"C:\work"])).Kind);
    }

    [Fact]
    public void VerifyOff_DoesNotTurnEveryUnmatchedWordIntoAProgram()
    {
        // The rooted-path rule is what stops this, and it has to hold with checking off.
        Assert.Equal(ExecutionKind.None, Plan("send log files", verifyPaths: false).Kind);
        Assert.Equal(ExecutionKind.None, Plan("setup.exe", verifyPaths: false).Kind);
    }

    // ---- environment variables ----

    [Fact]
    public void WindowsStyleVariablesExpand()
    {
        using var _ = EnvVar("KLIPPY_TEST_DIR", @"C:\Users\sean\AppData\Roaming");

        var plan = Plan("%KLIPPY_TEST_DIR%", probe: Probe(folders: [@"C:\Users\sean\AppData\Roaming"]));

        Assert.Equal(ExecutionKind.Folder, plan.Kind);
        Assert.Equal(@"C:\Users\sean\AppData\Roaming", plan.Target);
    }

    [Fact]
    public void AVariableMayBeJustTheStartOfThePath()
    {
        using var _ = EnvVar("KLIPPY_TEST_DIR", @"C:\Users\sean\AppData\Roaming");

        Assert.Equal(ExecutionKind.Folder,
            Plan(@"%KLIPPY_TEST_DIR%\Klippy",
                probe: Probe(folders: [@"C:\Users\sean\AppData\Roaming\Klippy"])).Kind);
    }

    [Theory]
    [InlineData("$KLIPPY_TEST_DIR")]
    [InlineData("${KLIPPY_TEST_DIR}")]
    public void UnixStyleVariablesExpandToo(string query)
    {
        using var _ = EnvVar("KLIPPY_TEST_DIR", "/Users/sean/Library");

        Assert.Equal(ExecutionKind.Folder,
            Plan(query, platform: Mac, probe: Probe(folders: ["/Users/sean/Library"])).Kind);
    }

    [Fact]
    public void TildeMeansHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotEqual("", home); // otherwise the assertion below proves nothing

        Assert.Equal(ExecutionKind.Folder,
            Plan("~", platform: Linux, probe: Probe(folders: [home])).Kind);
    }

    [Fact]
    public void AnUnknownVariableIsLeftAsWritten()
    {
        // Not expanded to nothing: "%nope%\Klippy" becoming "\Klippy" would offer to open a
        // folder at the root of the current drive, which is not what anybody typed.
        Assert.Equal(@"%KLIPPY_NO_SUCH_VAR%\Klippy", UnmatchedSearch.Expand(@"%KLIPPY_NO_SUCH_VAR%\Klippy"));
        Assert.Equal(ExecutionKind.None, Plan(@"%KLIPPY_NO_SUCH_VAR%\Klippy", verifyPaths: false).Kind);
    }

    [Fact]
    public void AnItemsOwnMacrosAreLeftAlone()
    {
        // %C% is the clipboard placeholder, filled when an item runs. Reading it here as an
        // environment variable named C would quietly rewrite it.
        using var _ = EnvVar("C", @"C:\somewhere");

        Assert.Equal("%C%", UnmatchedSearch.Expand("%C%"));
        Assert.Equal("%P%", UnmatchedSearch.Expand("%P%"));
    }

    [Fact]
    public void TextThatMerelyContainsAPercentIsUntouched()
    {
        Assert.Equal("50% off", UnmatchedSearch.Expand("50% off"));
        Assert.Equal("$", UnmatchedSearch.Expand("$"));
    }

    // ---- precedence ----

    [Fact]
    public void AMachineControlOutranksAPathThatHappensToExist()
    {
        // "sleep" is also a real file on plenty of Unix boxes. The word wins, because the
        // word is what someone typing five letters into a launcher meant.
        Assert.Equal(ExecutionKind.System,
            Plan("sleep", platform: Linux, probe: Probe(files: ["sleep"])).Kind);
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
