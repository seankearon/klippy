using System;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// What "Execute" decides an item means, and which process would carry it out.
///
/// The platform is a parameter rather than something read from the environment, so the
/// Windows rules are tested on Linux and the macOS ones on Windows — the alternative is
/// three quarters of this file only ever running on somebody else's machine.
/// </summary>
public class ExecutionTests
{
    private const string Search = "https://www.google.com/search?q=%P%";

    private static ExecutionPlan Plan(
        string text,
        string[]? arguments = null,
        string? clipboardText = null,
        ExecutionPlatform platform = ExecutionPlatform.Windows) =>
        ExecutionPolicy.Plan(text, arguments, clipboardText, platform);

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

        // file: and friends are programs waiting to be launched under another name; the
        // allow-list is what keeps Execute from becoming "run whatever this text says".
        Assert.Equal(ExecutionKind.None, Plan(@"file:///C:/Windows/System32/cmd.exe").Kind);
        Assert.Equal(ExecutionKind.None, Plan(@"C:\Windows\System32\cmd.exe").Kind);
        Assert.Equal(ExecutionKind.None, Plan("javascript:alert(1)").Kind);
    }

    [Fact]
    public void OrdinaryTextIsNotExecutable_AndSaysSo()
    {
        var plan = Plan("Best regards, Sam Rivera · Klippy Support");

        Assert.Equal(ExecutionKind.None, plan.Kind);
        Assert.Contains("not a URL or a script", plan.Problem);
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

    // ---- what the row offers ----

    [Fact]
    public void OnlyRunnableItemsOfferTheExecuteAction()
    {
        Assert.True(ExecutionPolicy.LooksExecutable(Search));
        Assert.True(ExecutionPolicy.LooksExecutable("www.klippy.app"));
        Assert.True(ExecutionPolicy.LooksExecutable("/home/sam/backup.sh --now"));

        // A %C% is taken on trust: what it holds is not known until it is run, and
        // reading the clipboard to decide whether to draw a button would be worse.
        Assert.True(ExecutionPolicy.LooksExecutable("%C%"));

        Assert.False(ExecutionPolicy.LooksExecutable("DE44 5001 0517 5407 3249 31"));
        Assert.False(ExecutionPolicy.LooksExecutable("docker system prune -af --volumes"));
        Assert.False(ExecutionPolicy.LooksExecutable(""));

        // .bat has nothing to run it on a Mac, so the row there does not pretend.
        Assert.False(ExecutionPolicy.LooksExecutable(@"C:\tools\build.bat", ExecutionPlatform.MacOS));
        Assert.True(ExecutionPolicy.LooksExecutable(@"C:\tools\build.bat", ExecutionPlatform.Windows));
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
        Assert.Equal(Plan("nope").Problem, Plan("nope").Description);
    }
}
