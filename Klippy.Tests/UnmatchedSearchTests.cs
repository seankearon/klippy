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

    /// <summary>
    /// An environment described rather than set. SetEnvironmentVariable is process-wide and
    /// xunit runs test classes in parallel, so a variable named "C" set for one assertion is
    /// a variable every other test in the run can see.
    /// </summary>
    private static readonly Dictionary<string, string> Variables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KLIPPY_TEST_DIR"] = @"C:\Users\sean\AppData\Roaming",
        ["KLIPPY_UNIX_DIR"] = "/Users/sean/Library",
        ["C"] = @"C:\somewhere",
    };

    private static readonly EnvironmentProbe Machine = new(
        name => Variables.TryGetValue(name, out var value) ? value : null,
        () => "/home/sean");

    private static ExecutionPlan Plan(string query, bool verifyPaths = true,
        ExecutionPlatform platform = Windows, PathProbe? probe = null,
        EnvironmentProbe? environment = null) =>
        UnmatchedSearch.Plan(query, verifyPaths, platform, probe ?? Empty, environment ?? Machine);

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

    [Fact]
    public void HttpLinksAreOfferedOnTheSchemeTheyWereTypedWith()
    {
        // Plenty of LAN devices and intranet boxes answer on http and nothing else, so a
        // typed one is offered — and offered as typed, since promoting it to https would
        // send the user to a port those hosts are not listening on.
        var plan = Plan("http://192.168.1.10:8080/status");

        Assert.Equal(ExecutionKind.Url, plan.Kind);
        Assert.Equal("http://192.168.1.10:8080/status", plan.Target);

        // And the two routes now agree on http, where the typed one used to refuse it.
        Assert.Equal(ExecutionKind.Url, ExecutionPolicy.Plan("http://example.com").Kind);
    }

    [Theory]
    [InlineData("mailto:someone@example.com")]  // deliberately narrower than a marked item
    [InlineData("ftp://example.com")]
    [InlineData("file:///C:/Windows")]          // the shell would happily open it
    [InlineData("javascript:alert(1)")]
    [InlineData("shell:startup")]
    [InlineData("https://")]                    // a scheme is not a URL
    [InlineData("http://")]
    [InlineData("www.")]
    [InlineData("https://localhost")]           // no dot: not what a filter box meant
    [InlineData("http://localhost")]
    [InlineData("www.qwe.com and more words")]  // a sentence that starts with a host
    public void TypedLinksAreTheWebSchemesAndWwwOnly(string query)
    {
        Assert.Equal(ExecutionKind.None, Plan(query).Kind);
    }

    [Fact]
    public void AMarkedItemStillTakesTheWiderList()
    {
        // The narrowing belongs to typed text, and must not have leaked into the rules a
        // snippet marked Execute goes through.
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

    // ---- pasted paths ----

    [Fact]
    public void AQuotedPathIsOffered_BecauseThatIsWhatCopyAsPathGivesYou()
    {
        // Explorer's Shift+right-click → Copy as path always wraps the path in quotes, so
        // without this a pasted path is a string starting with a quote and matches nothing.
        var plan = Plan("\"C:\\work\\invoices\"", probe: Probe(folders: [@"C:\work\invoices"]));

        Assert.Equal(ExecutionKind.Folder, plan.Kind);
        Assert.Equal(@"C:\work\invoices", plan.Target); // and the quotes do not reach the launcher
    }

    [Fact]
    public void AQuotedPathWithSpacesIsStillOnePath()
    {
        // The case the quotes are actually for. A space is not an argument separator here:
        // the whole line is the target.
        var plan = Plan("\"C:\\Program Files\\tools\\go.ps1\"",
            probe: Probe(files: [@"C:\Program Files\tools\go.ps1"]));

        Assert.Equal(ExecutionKind.Script, plan.Kind);
        Assert.Equal(@"C:\Program Files\tools\go.ps1", plan.Target);
    }

    [Fact]
    public void QuotesComeOffBeforeAVariableIsExpanded()
    {
        var plan = Plan("\"%KLIPPY_TEST_DIR%\\Klippy\"",
            probe: Probe(folders: [@"C:\Users\sean\AppData\Roaming\Klippy"]));

        Assert.Equal(ExecutionKind.Folder, plan.Kind);
    }

    [Fact]
    public void AnUnmatchedQuoteIsLeftAlone()
    {
        // Half a paste. Guessing which end to trim would be worse than leaving it the
        // search it still is.
        Assert.Equal(ExecutionKind.None,
            Plan("\"C:\\work\\invoices", probe: Probe(folders: [@"C:\work\invoices"])).Kind);
        Assert.Equal(ExecutionKind.None,
            Plan("C:\\work\\invoices\"", probe: Probe(folders: [@"C:\work\invoices"])).Kind);
    }

    [Theory]
    [InlineData("\"lock\"", "lock")]                      // whatever the quotes wrap
    [InlineData("\"https://qwe.com/a\"", "https://qwe.com/a")]
    [InlineData("  \" C:\\work \"  ", @"C:\work")]        // trimmed inside as well as out
    [InlineData("\"\"", "")]
    [InlineData("\"", "\"")]                              // one quote is not a pair
    [InlineData("say \"hello\" twice", "say \"hello\" twice")]
    public void OnlyAWrappingPairComesOff(string typed, string expected)
    {
        Assert.Equal(expected, UnmatchedSearch.Unquote(typed.Trim()));
    }

    [Fact]
    public void AQuotedMachineControlStillWorks()
    {
        // Falls out of unquoting the whole line rather than only a path — which is what the
        // other route into execution already does with its first word.
        Assert.Equal(ExecutionKind.System, Plan("\"restart\"").Kind);
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
        var plan = Plan("%KLIPPY_TEST_DIR%", probe: Probe(folders: [@"C:\Users\sean\AppData\Roaming"]));

        Assert.Equal(ExecutionKind.Folder, plan.Kind);
        Assert.Equal(@"C:\Users\sean\AppData\Roaming", plan.Target);
    }

    [Fact]
    public void AVariableMayBeJustTheStartOfThePath()
    {
        Assert.Equal(ExecutionKind.Folder,
            Plan(@"%KLIPPY_TEST_DIR%\Klippy",
                probe: Probe(folders: [@"C:\Users\sean\AppData\Roaming\Klippy"])).Kind);
    }

    [Theory]
    [InlineData("$KLIPPY_UNIX_DIR")]
    [InlineData("${KLIPPY_UNIX_DIR}")]
    public void UnixStyleVariablesExpandToo(string query)
    {
        Assert.Equal(ExecutionKind.Folder,
            Plan(query, platform: Mac, probe: Probe(folders: ["/Users/sean/Library"])).Kind);
    }

    [Fact]
    public void TildeMeansHome()
    {
        Assert.Equal(ExecutionKind.Folder,
            Plan("~", platform: Linux, probe: Probe(folders: ["/home/sean"])).Kind);
    }

    [Fact]
    public void AnUnknownVariableIsLeftAsWritten()
    {
        // Not expanded to nothing: "%nope%\Klippy" becoming "\Klippy" would offer to open a
        // folder at the root of the current drive, which is not what anybody typed.
        Assert.Equal(@"%KLIPPY_NO_SUCH_VAR%\Klippy", Machine.Expand(@"%KLIPPY_NO_SUCH_VAR%\Klippy"));
        Assert.Equal(ExecutionKind.None, Plan(@"%KLIPPY_NO_SUCH_VAR%\Klippy", verifyPaths: false).Kind);
    }

    [Fact]
    public void AnItemsOwnMacrosAreLeftAlone()
    {
        // %C% is the clipboard placeholder, filled when an item runs. Reading it here as an
        // environment variable named C would quietly rewrite it — and the environment
        // described above defines one, so this is the trap rather than merely its absence.
        Assert.Equal("%C%", Machine.Expand("%C%"));
        Assert.Equal("%P%", Machine.Expand("%P%"));
    }

    [Fact]
    public void TextThatMerelyContainsAPercentIsUntouched()
    {
        Assert.Equal("50% off", Machine.Expand("50% off"));
        Assert.Equal("$", Machine.Expand("$"));
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

}
