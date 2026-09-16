using System;
using System.Collections.Generic;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// What "Execute" decides an item means, and which process would carry it out.
///
/// The platform and the environment are both parameters rather than things read off the
/// machine, so the Windows rules are tested on Linux and the macOS ones on Windows — the
/// alternative is three quarters of this file only ever running on somebody else's
/// machine. A test about %LOCALAPPDATA% describes an environment instead of setting one,
/// which every other test in the run would otherwise be able to see.
/// </summary>
public class ExecutionTests
{
    private const string Search = "https://www.google.com/search?q=%P%";

    /// <summary>
    /// The machine these rules are asked about, named rather than inhabited. Looked up
    /// without regard to case, as Windows does it — the platform whose spelling of
    /// %LOCALAPPDATA% these tests are mostly about.
    /// </summary>
    private static readonly Dictionary<string, string> Variables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LOCALAPPDATA"] = @"C:\Users\sam\AppData\Local",
        ["HOME"] = "/home/sam",
        ["SPACED"] = @"C:\Users\John Smith",
        ["EDITOR"] = @"C:\apps\code.exe",
        ["BUNDLE"] = "/Applications/Klippy.app/",
        ["SITE"] = "https://klippy.app",
        ["AMPERSAND"] = @"C:\a&calc",         // a folder name cmd.exe would read as two commands
        ["QUOTED"] = "C:\\x\\payload.scr\"y", // a value that would decide what starts
        ["MACRO"] = @"C:\tools\%P%.ps1",      // a value that spells a macro
        ["C"] = @"C:\somewhere",              // the trap: a variable shadowing a macro
    };

    private static readonly EnvironmentProbe Env = new(
        name => Variables.TryGetValue(name, out var value) ? value : null,
        () => "/home/sam");

    private static ExecutionPlan Plan(
        string text,
        string[]? arguments = null,
        string? clipboardText = null,
        ExecutionPlatform platform = ExecutionPlatform.Windows,
        EnvironmentProbe? environment = null) =>
        ExecutionPolicy.Plan(text, arguments, clipboardText, platform, environment ?? Env);

    /// <summary>
    /// The editor's question, asked of the same described environment. Direct calls to
    /// <see cref="ExecutionPolicy.LooksExecutable"/> would read the real machine, which is
    /// what the class comment above promises this file does not do.
    /// </summary>
    private static bool LooksExecutable(
        string? text,
        ExecutionPlatform platform = ExecutionPlatform.Windows,
        EnvironmentProbe? environment = null) =>
        ExecutionPolicy.LooksExecutable(text, platform, environment ?? Env);

    // ---- URLs ----

    [Fact]
    public void AUrlIsOpened_WithItsArgumentInTheQueryString()
    {
        // The feature request's example, from the other end: the engine is handed the
        // snippet and "stuff", and works the URL out itself.
        var plan = Plan(Search, new[] { "stuff" });

        Assert.Equal(ExecutionKind.Url, plan.Kind);
        Assert.Equal("https://www.google.com/search?q=stuff", plan.Target);
    }

    [Fact]
    public void AMultiWordArgument_IsPercentEncodedIntoTheUrl()
    {
        // A raw space would break the URL in two before the browser ever saw it.
        var plan = Plan(Search, new[] { "cats", "and", "dogs" });

        Assert.Equal("https://www.google.com/search?q=cats%20and%20dogs", plan.Target);
    }

    [Fact]
    public void AUrlArrivingThroughAMacro_IsTakenAsItStands()
    {
        // Encoding this one would turn its own slashes into %2F and open nothing.
        var plan = Plan("%C%", clipboardText: "https://example.com/a b");

        Assert.Equal(ExecutionKind.Url, plan.Kind);
        Assert.Equal("https://example.com/a b", plan.Target);
    }

    [Fact]
    public void BareWwwGetsAnHttps_AsABrowserWould()
    {
        Assert.Equal("https://www.klippy.app", Plan("www.klippy.app").Target);
    }

    [Fact]
    public void MailtoCounts_ButOtherSchemesDoNot()
    {
        Assert.Equal(ExecutionKind.Url, Plan("mailto:ops@klippy.app").Kind);

        // A scheme is not a path, however it ends: the .exe on the end of this one does
        // not make it the application rules' business, and javascript: names nothing.
        Assert.Equal(ExecutionKind.None, Plan(@"file:///C:/Windows/System32/cmd.exe").Kind);
        Assert.Equal(ExecutionKind.None, Plan("javascript:alert(1)").Kind);

        // A drive letter is the one colon a path may carry, and it stays a path.
        Assert.Equal(ExecutionKind.Application, Plan(@"C:\Windows\System32\cmd.exe").Kind);
    }

    [Fact]
    public void OrdinaryTextIsNotExecutable_AndSaysSo()
    {
        var plan = Plan("Best regards, Sam Rivera · Klippy Support");

        Assert.Equal(ExecutionKind.None, plan.Kind);
        Assert.Contains("not a URL, an application or a script", plan.Problem);
    }

    [Fact]
    public void ASnippetThatOpensWithALink_IsOpenedAtThatLink()
    {
        // The rest of a multi-line snippet is prose around the link, not part of it.
        var plan = Plan("https://meet.example.com/j/882-441-veo\nDial-in: 555 0133");

        Assert.Equal("https://meet.example.com/j/882-441-veo", plan.Target);
    }

    // ---- scripts ----

    [Fact]
    public void AScriptPathRuns_WithTheRestOfTheLineAsArguments()
    {
        var plan = Plan(@"C:\tools\deploy.ps1 --env %P%", new[] { "staging" });

        Assert.Equal(ExecutionKind.Script, plan.Kind);
        Assert.Equal(@"C:\tools\deploy.ps1", plan.Target);
        Assert.Equal(new[] { "--env", "staging" }, plan.Arguments);
    }

    [Fact]
    public void AMultiWordArgument_StaysOneArgumentToAScript()
    {
        var plan = Plan("/home/sam/backup.sh %P%", new[] { "two words" }, platform: ExecutionPlatform.Linux);

        Assert.Equal(new[] { "two words" }, plan.Arguments);
    }

    [Fact]
    public void AQuotedPathWithSpaces_IsOneScriptPath()
    {
        var plan = Plan("\"C:\\Program Files\\tools\\go.ps1\" now");

        Assert.Equal(@"C:\Program Files\tools\go.ps1", plan.Target);
        Assert.Equal(new[] { "now" }, plan.Arguments);
    }

    [Fact]
    public void BatchFilesAreWindowsOnly()
    {
        Assert.Equal(ExecutionKind.Script, Plan(@"C:\tools\build.bat").Kind);

        var plan = Plan(@"C:\tools\build.bat", platform: ExecutionPlatform.MacOS);
        Assert.Equal(ExecutionKind.None, plan.Kind);
        Assert.Contains("only run on Windows", plan.Problem);
    }

    [Fact]
    public void ShellAndPowerShellScriptsRunEverywhere()
    {
        foreach (var platform in new[] { ExecutionPlatform.Windows, ExecutionPlatform.MacOS, ExecutionPlatform.Linux })
        {
            Assert.Equal(ExecutionKind.Script, Plan("/home/sam/backup.sh", platform: platform).Kind);
            Assert.Equal(ExecutionKind.Script, Plan("/home/sam/backup.ps1", platform: platform).Kind);
        }
    }

    [Fact]
    public void ABatchArgumentCarryingCmdSyntax_IsRefusedRatherThanEscaped()
    {
        // cmd.exe re-parses its command line after .NET has quoted it, so "& calc" would
        // start a second command. Clipboard text is not always the user's own words.
        var plan = Plan(@"C:\tools\build.bat %C%", clipboardText: "release & calc.exe");

        Assert.Equal(ExecutionKind.None, plan.Kind);
        Assert.Contains("cmd.exe", plan.Problem);

        // The same argument is fine for a .ps1, which is started directly.
        Assert.Equal(ExecutionKind.Script,
            Plan(@"C:\tools\build.ps1 %C%", clipboardText: "release & calc.exe").Kind);
    }

    [Fact]
    public void AMacroMayCarryAWholeCommandLine()
    {
        // "%C%" alone: run whatever command is on the clipboard, arguments and all.
        var plan = Plan("%C%", clipboardText: @"C:\tools\deploy.ps1 --env staging");

        Assert.Equal(ExecutionKind.Script, plan.Kind);
        Assert.Equal(@"C:\tools\deploy.ps1", plan.Target);
        Assert.Equal(new[] { "--env", "staging" }, plan.Arguments);
    }

    [Fact]
    public void NothingToRun_IsSaidPlainly()
    {
        Assert.Equal("There is nothing to run.", Plan("   ").Problem);
        Assert.Equal("There is nothing to run.", Plan("%P%").Problem);
    }

    // ---- applications ----

    [Fact]
    public void AnApplicationPathStarts_WithTheRestOfTheLineAsArguments()
    {
        // The feature request's own example: a program named by its full path, quoted
        // because "Program Files" has a space in it and is one path all the same.
        var plan = Plan(@"""C:\Program Files\Klippy\Klippy.Desktop.exe"" --minimised");

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal(@"C:\Program Files\Klippy\Klippy.Desktop.exe", plan.Target);
        Assert.Equal(new[] { "--minimised" }, plan.Arguments);
    }

    [Fact]
    public void AnApplicationTakesItsArgumentsFromTheMacrosToo()
    {
        var plan = Plan(@"C:\tools\editor.exe %P%", new[] { "notes from today" });

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal(new[] { "notes from today" }, plan.Arguments);
    }

    [Fact]
    public void EachPlatformStartsOnlyItsOwnKindOfApplication()
    {
        Assert.Equal(ExecutionKind.Application, Plan(@"C:\apps\klippy.exe").Kind);
        Assert.Equal(ExecutionKind.Application,
            Plan("/Applications/Klippy.app", platform: ExecutionPlatform.MacOS).Kind);
        Assert.Equal(ExecutionKind.Application,
            Plan("/opt/Klippy.AppImage", platform: ExecutionPlatform.Linux).Kind);

        // An .exe is no more startable on a Mac than a .bat is, and says so the same way.
        var onMac = Plan(@"C:\apps\klippy.exe", platform: ExecutionPlatform.MacOS);
        Assert.Equal(ExecutionKind.None, onMac.Kind);
        Assert.Contains(".exe applications only run on Windows", onMac.Problem);

        Assert.Contains("only run on macOS",
            Plan("/Applications/Klippy.app", platform: ExecutionPlatform.Linux).Problem);
        Assert.Contains("only run on Linux",
            Plan("/opt/Klippy.AppImage", platform: ExecutionPlatform.Windows).Problem);
    }

    [Fact]
    public void ABundlesTrailingSeparatorIsTrimmed()
    {
        // A bundle is a directory, so its path arrives from a shell's tab-completion
        // with the separator still on the end.
        var plan = Plan("/Applications/Klippy.app/", platform: ExecutionPlatform.MacOS);

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal("/Applications/Klippy.app", plan.Target);
    }

    [Fact]
    public void AMacroMayCarryAWholeApplicationCommandLine()
    {
        var plan = Plan("%C%", clipboardText: @"C:\apps\klippy.exe --minimised");

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal(@"C:\apps\klippy.exe", plan.Target);
        Assert.Equal(new[] { "--minimised" }, plan.Arguments);
    }

    [Fact]
    public void SomethingThatOnlyEndsInAnApplicationExtension_IsStillNotOne()
    {
        // The extension is read off the last path segment, so a folder called .app or a
        // hidden file named for one is not an application.
        Assert.Equal(ExecutionKind.None, Plan(".exe").Kind);
        Assert.Equal(ExecutionKind.None, Plan("/Applications/.app", platform: ExecutionPlatform.MacOS).Kind);
        Assert.Equal(ExecutionKind.None, Plan("klippy.exe.txt").Kind);
    }

    // ---- environment variables ----

    [Fact]
    public void AVariableInAPath_IsExpandedBeforeAnythingJudgesIt()
    {
        // The bug this section exists for: CreateProcess takes a file name rather than a
        // command line, so an unexpanded %localappdata% is a folder with percent signs in
        // its name and the start fails on a path that plainly exists.
        var plan = Plan(@"%localappdata%\Programs\WebStorm\bin\webstorm64.exe");

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal(@"C:\Users\sam\AppData\Local\Programs\WebStorm\bin\webstorm64.exe", plan.Target);

        // The toast names the program, not the path, so it reads the same either way.
        Assert.Equal("Starting webstorm64", plan.Description);
    }

    [Fact]
    public void BothDialectsAreUnderstood_AndSoIsALeadingTilde()
    {
        // Windows and Unix spellings both work everywhere: the cost of honouring the
        // wrong one is a name that does not resolve, which is what happens anyway.
        foreach (var text in new[] { "$HOME/bin/deploy.sh", "${HOME}/bin/deploy.sh", "~/bin/deploy.sh" })
        {
            var plan = Plan(text, platform: ExecutionPlatform.Linux);
            Assert.Equal(ExecutionKind.Script, plan.Kind);
            Assert.Equal("/home/sam/bin/deploy.sh", plan.Target);
        }
    }

    [Fact]
    public void AVariableMaySupplyTheWholeCommand_AndTheEditorSaysTheSame()
    {
        // Nothing about "%EDITOR%" looks like an application until it resolves, which is
        // why the allow-list has to be read after the expansion rather than before it.
        var plan = Plan("%EDITOR%");

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal(@"C:\apps\code.exe", plan.Target);

        // And the marker's warning has to agree with what Enter will do, or the editor
        // refuses to let you tick Execute on an item that would have run perfectly well.
        Assert.True(LooksExecutable("%EDITOR%", ExecutionPlatform.Windows, Env));
    }

    [Fact]
    public void AnUnknownVariableIsLeftAsWritten()
    {
        // Not expanded to nothing: "%nope%\go.ps1" becoming "\go.ps1" would run whatever
        // sits at the root of the current drive. Left whole, the launcher says it is not
        // there and names it, which is the message that tells the user what to fix.
        var plan = Plan(@"%KLIPPY_NO_SUCH_VAR%\go.ps1");
        Assert.Equal(ExecutionKind.Script, plan.Kind);
        Assert.Equal(@"%KLIPPY_NO_SUCH_VAR%\go.ps1", plan.Target);

        // On its own it names neither script nor application, so it is simply not runnable.
        Assert.Equal(ExecutionKind.None, Plan("%KLIPPY_NO_SUCH_VAR%").Kind);
    }

    [Fact]
    public void AVariableWithASpaceInIt_IsStillOnePath_AndItsArgumentsStayArguments()
    {
        // Expansion happens after the line has been split into words, so a value carrying
        // a space cannot turn one path into a command plus a stray argument.
        var plan = Plan(@"%SPACED%\tools\build.ps1 --now");

        Assert.Equal(@"C:\Users\John Smith\tools\build.ps1", plan.Target);
        Assert.Equal(new[] { "--now" }, plan.Arguments);
    }

    [Fact]
    public void AnItemsOwnMacrosAreNotVariables()
    {
        // The environment above defines a variable named C. The macro still wins, and the
        // whole command line it hands back is still re-split as if it had been typed.
        var plan = Plan("%C%", clipboardText: @"C:\apps\klippy.exe --minimised");

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal(@"C:\apps\klippy.exe", plan.Target);
        Assert.Equal(new[] { "--minimised" }, plan.Arguments);
    }

    [Fact]
    public void AClipboardValueIsNotReadForVariables()
    {
        // A macro's value is data rather than more text to read — the same rule that stops
        // it becoming a second command. A clipboard holding C:\100%discount%off\tool.exe
        // is a real path, and reading it twice would eat the middle of it silently.
        var plan = Plan("%C%", clipboardText: @"%localappdata%\Programs\WebStorm\bin\webstorm64.exe");

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal(@"%localappdata%\Programs\WebStorm\bin\webstorm64.exe", plan.Target);
    }

    [Fact]
    public void ArgumentsKeepTheirPercents_SoTheBatchRefusalStillHolds()
    {
        // The cmd.exe refusal has to judge the argument as typed: a variable defined as
        // "a&b" would otherwise smuggle an ampersand past the one check that exists to
        // catch it.
        var batch = Plan(@"C:\tools\deploy.bat %localappdata%\out");
        Assert.Equal(ExecutionKind.None, batch.Kind);
        Assert.Contains(".bat file", batch.Problem);

        // Nothing re-reads a PowerShell argument, so it is simply passed along as written.
        var script = Plan(@"C:\tools\deploy.ps1 %localappdata%\out");
        Assert.Equal(ExecutionKind.Script, script.Kind);
        Assert.Equal(new[] { @"%localappdata%\out" }, script.Arguments);
    }

    [Fact]
    public void APercentInAUrlIsNeverAVariableName()
    {
        // A URL is recognised before anything expands, or a %XX escape — or an item's own
        // %P% in a query string — would be read as a name to look up.
        var plan = Plan("https://example.com/?p=%localappdata%");

        Assert.Equal(ExecutionKind.Url, plan.Kind);
        Assert.Equal("https://example.com/?p=%localappdata%", plan.Target);
    }

    [Fact]
    public void AVariableThatNamesALinkOpensIt()
    {
        // The second URL check, after expansion: a variable may hold a link as easily as
        // a path, and the editor has to say the same about it.
        var plan = Plan("%SITE%");

        Assert.Equal(ExecutionKind.Url, plan.Kind);
        Assert.Equal("https://klippy.app", plan.Target);
        Assert.True(LooksExecutable("%SITE%", ExecutionPlatform.Windows, Env));
    }

    [Fact]
    public void AnExpansionThatEndsInASeparator_LeavesNoneBehind()
    {
        // The trailing separator a shell's tab-completion leaves behind can arrive through
        // a variable too, so the trim has to happen after the expansion rather than before.
        var plan = Plan("%BUNDLE%", platform: ExecutionPlatform.MacOS);

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal("/Applications/Klippy.app", plan.Target);
    }

    [Fact]
    public void ABareNameForWindowsToFindOnPathIsUntouched()
    {
        // Expansion leaves anything it does not recognise exactly as it stands, so a name
        // Windows is meant to resolve on PATH still reaches it unchanged.
        var plan = Plan("notepad.exe");

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal("notepad.exe", plan.Target);
    }

    [Fact]
    public void TextThatMerelyContainsAPercentIsUntouched()
    {
        // One percent sign is not a pair, so a folder someone named after a discount is
        // still a folder.
        var plan = Plan(@"C:\100%off\tool.exe");

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal(@"C:\100%off\tool.exe", plan.Target);
    }

    [Fact]
    public void AVariableIsFoundWhicheverWayItIsSpelled()
    {
        // Windows looks a name up without regard to case, and the snippet that turned this
        // up was written in lower case while every document spells it in upper.
        Assert.Equal(
            @"C:\Users\sam\AppData\Local\go.ps1",
            Plan(@"%localappdata%\go.ps1").Target);
        Assert.Equal(
            @"C:\Users\sam\AppData\Local\go.ps1",
            Plan(@"%LOCALAPPDATA%\go.ps1").Target);
    }

    [Fact]
    public void AVariableWithASpaceInIt_IsNotMistakenForAWholeCommandLine()
    {
        // The re-split is for a macro that handed back a whole command line. A space that
        // came out of the environment is part of a folder's name, and cutting the line
        // there would leave "C:\Program" as the command — which is what quoting exists to
        // prevent, so a variable must not reintroduce it just because a macro sits nearby.
        var plan = Plan(@"%SPACED%\%P%\app.exe", new[] { "JetBrains" });

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal(@"C:\Users\John Smith\JetBrains\app.exe", plan.Target);
        Assert.Empty(plan.Arguments);
    }

    [Fact]
    public void AnAmpersandInABatchPath_IsRefusedAsItsArgumentsAre()
    {
        // cmd.exe reads the path off the same line it reads the arguments off, and .NET
        // only quotes what carries a space — so "C:\a&calc\run.bat" would start calc.
        // Refusing is the same honest answer the arguments already get.
        var plan = Plan(@"%AMPERSAND%\run.bat");

        Assert.Equal(ExecutionKind.None, plan.Kind);
        Assert.Contains("cmd.exe", plan.Problem);

        // Only cmd re-reads its line, so the same folder is fine for anything else.
        Assert.Equal(ExecutionKind.Script, Plan(@"%AMPERSAND%\run.ps1").Kind);
    }

    [Fact]
    public void AQuoteInThePath_IsRefusedBeforeItCanChooseWhatStarts()
    {
        // Windows takes the whole string as a command line and stops the program name at
        // the quote, while the allow-list only ever read the extension off the tail: this
        // passes as an .exe and would start the .scr. A path carries no quotes anyway.
        var plan = Plan("%QUOTED%.exe");

        Assert.Equal(ExecutionKind.None, plan.Kind);
        Assert.Contains("no quotes", plan.Problem);
    }

    [Fact]
    public void AMacroSpeltInsideAVariablesValue_IsStillFilledIn()
    {
        // Pinned rather than prevented, and stated so it is not mistaken for containment:
        // once the variable resolves, its value is part of one string and the macros are
        // filled from it like any other. Reaching this needs someone to have put a Klippy
        // macro inside an environment variable, and whoever can do that can set PATH.
        var plan = Plan("%MACRO%", new[] { "deploy" });

        Assert.Equal(ExecutionKind.Script, plan.Kind);
        Assert.Equal(@"C:\tools\deploy.ps1", plan.Target);
    }

    // ---- what the editor checks before the marker goes on ----

    [Fact]
    public void TextThatCouldRun_IsToldApartFromTextThatCouldNot()
    {
        Assert.True(LooksExecutable(Search));
        Assert.True(LooksExecutable("www.klippy.app"));
        Assert.True(LooksExecutable("/home/sam/backup.sh --now"));

        // A %C% is taken on trust: what it holds is not known until it is run, and
        // reading the clipboard to answer a question about the editor would be worse.
        Assert.True(LooksExecutable("%C%"));

        // An application counts on the platform it belongs to, and nowhere else.
        Assert.True(LooksExecutable(
            @"""C:\Program Files\Klippy\Klippy.Desktop.exe""", ExecutionPlatform.Windows));
        Assert.False(LooksExecutable(
            @"C:\Program Files\Klippy\Klippy.Desktop.exe", ExecutionPlatform.MacOS));
        Assert.True(LooksExecutable("/Applications/Klippy.app", ExecutionPlatform.MacOS));
        Assert.True(LooksExecutable("/opt/Klippy.AppImage", ExecutionPlatform.Linux));

        Assert.False(LooksExecutable("DE44 5001 0517 5407 3249 31"));
        Assert.False(LooksExecutable("docker system prune -af --volumes"));
        Assert.False(LooksExecutable(""));

        // A scheme the URL rules turned down stays turned down, .exe on the end or not.
        Assert.False(LooksExecutable(@"file:///C:/Windows/System32/cmd.exe"));

        // .bat has nothing to run it on a Mac, so marking one there is worth a warning.
        Assert.False(LooksExecutable(@"C:\tools\build.bat", ExecutionPlatform.MacOS));
        Assert.True(LooksExecutable(@"C:\tools\build.bat", ExecutionPlatform.Windows));
    }

    // ---- the process each plan becomes ----

    [Fact]
    public void AUrlIsHandedToWhateverOpensLinksOnThePlatform()
    {
        var plan = Plan("https://klippy.app");

        var windows = ExecutionPolicy.Resolve(plan, ExecutionPlatform.Windows)!;
        Assert.Equal("https://klippy.app", windows.FileName);
        Assert.True(windows.UseShellExecute); // the shell is what knows the default browser

        Assert.Equal("open", ExecutionPolicy.Resolve(plan, ExecutionPlatform.MacOS)!.FileName);
        Assert.Equal("xdg-open", ExecutionPolicy.Resolve(plan, ExecutionPlatform.Linux)!.FileName);
    }

    [Fact]
    public void ScriptsAreStartedByTheirInterpreter_NeverThroughAShellString()
    {
        var batch = ExecutionPolicy.Resolve(Plan(@"C:\t\b.bat one"), ExecutionPlatform.Windows)!;
        Assert.Equal("cmd.exe", batch.FileName);
        Assert.Equal(new[] { "/c", @"C:\t\b.bat", "one" }, batch.Arguments);
        Assert.False(batch.UseShellExecute);

        var powershell = ExecutionPolicy.Resolve(Plan(@"C:\t\b.ps1 one"), ExecutionPlatform.Windows)!;
        Assert.Equal("powershell.exe", powershell.FileName);
        // -File, not -Command: the arguments stay arguments instead of becoming script.
        Assert.Equal(new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\t\b.ps1", "one" },
            powershell.Arguments);

        var pwsh = ExecutionPolicy.Resolve(
            Plan("/home/sam/b.ps1", platform: ExecutionPlatform.MacOS), ExecutionPlatform.MacOS)!;
        Assert.Equal("pwsh", pwsh.FileName);

        var gitBash = ExecutionPolicy.Resolve(
            Plan(@"C:\t\b.sh one"), ExecutionPlatform.Windows)!;
        Assert.Equal("bash.exe", gitBash.FileName);
        Assert.Equal(new[] { @"C:\t\b.sh", "one" }, gitBash.Arguments);
    }

    [Fact]
    public void AnExecutableShellScriptRunsItself_SoItsShebangChoosesTheInterpreter()
    {
        var plan = Plan("/home/sam/backup.sh one", platform: ExecutionPlatform.Linux);

        var executable = ExecutionPolicy.Resolve(plan, ExecutionPlatform.Linux, _ => true)!;
        Assert.Equal("/home/sam/backup.sh", executable.FileName);
        Assert.Equal(new[] { "one" }, executable.Arguments);

        // Without the execute bit there is no shebang to honour, so sh runs it.
        var plain = ExecutionPolicy.Resolve(plan, ExecutionPlatform.Linux, _ => false)!;
        Assert.Equal("/bin/sh", plain.FileName);
        Assert.Equal(new[] { "/home/sam/backup.sh", "one" }, plain.Arguments);
    }

    [Fact]
    public void AnApplicationIsStartedDirectly_SoItsArgumentsStayArguments()
    {
        var plan = Plan(@"""C:\Program Files\Klippy\Klippy.Desktop.exe"" --minimised");

        var windows = ExecutionPolicy.Resolve(plan, ExecutionPlatform.Windows)!;
        Assert.Equal(@"C:\Program Files\Klippy\Klippy.Desktop.exe", windows.FileName);
        Assert.Equal(new[] { "--minimised" }, windows.Arguments);
        // No shell reads this line, so a macro's value can never become a second command.
        Assert.False(windows.UseShellExecute);
    }

    [Fact]
    public void AMacOsBundleIsHandedToOpen_WhichKnowsWhatIsInsideIt()
    {
        var bare = ExecutionPolicy.Resolve(
            Plan("/Applications/Klippy.app", platform: ExecutionPlatform.MacOS), ExecutionPlatform.MacOS)!;
        Assert.Equal("open", bare.FileName);
        Assert.Equal(new[] { "-a", "/Applications/Klippy.app" }, bare.Arguments);

        // What follows --args reaches the application as its own argv.
        var withArguments = ExecutionPolicy.Resolve(
            Plan("/Applications/Klippy.app --minimised", platform: ExecutionPlatform.MacOS),
            ExecutionPlatform.MacOS)!;
        Assert.Equal(new[] { "-a", "/Applications/Klippy.app", "--args", "--minimised" },
            withArguments.Arguments);
    }

    [Fact]
    public void AnAppImageRunsItself()
    {
        var plan = Plan("/opt/Klippy.AppImage --minimised", platform: ExecutionPlatform.Linux);

        var linux = ExecutionPolicy.Resolve(plan, ExecutionPlatform.Linux)!;
        Assert.Equal("/opt/Klippy.AppImage", linux.FileName);
        Assert.Equal(new[] { "--minimised" }, linux.Arguments);
    }

    [Fact]
    public void NothingToRunResolvesToNoProcess()
    {
        Assert.Null(ExecutionPolicy.Resolve(Plan("just some text"), ExecutionPlatform.Windows));
    }

    // ---- what the user is told ----

    [Fact]
    public void ThePlanSaysWhatIsAboutToHappen()
    {
        Assert.Equal("Opening www.google.com", Plan(Search, new[] { "stuff" }).Description);
        Assert.Equal("Running deploy.ps1", Plan(@"C:\tools\deploy.ps1").Description);

        // An application is named as a person names it: Klippy.Desktop, not the file.
        Assert.Equal("Starting Klippy.Desktop",
            Plan(@"""C:\Program Files\Klippy\Klippy.Desktop.exe""").Description);
        Assert.Equal("Starting Klippy",
            Plan("/Applications/Klippy.app/", platform: ExecutionPlatform.MacOS).Description);
        Assert.Equal(Plan("nope").Problem, Plan("nope").Description);
    }
}
