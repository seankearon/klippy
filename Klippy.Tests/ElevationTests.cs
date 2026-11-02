using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Run as administrator: a marker on an Execute snippet that starts its script or application through the
/// shell's <c>runas</c> verb, so Windows asks first. The rules are tested as ExecutionTests tests the rest —
/// the platform named rather than inhabited, so the Windows behaviour is pinned down from any machine — and
/// the marker as the user meets it, through the editor and the list.
/// </summary>
public class ElevationTests
{
    private static readonly EnvironmentProbe Env = new(_ => null, () => "/home/sam");

    private static readonly PathProbe Disk = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\work\invoices" }.Contains,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\logs\app.log" }.Contains);

    private static ExecutionPlan Plan(
        string text, ExecutionPlatform platform = ExecutionPlatform.Windows, string? clipboardText = null) =>
        ExecutionPolicy.Plan(text, null, clipboardText, platform, Env, Disk) with { Elevated = true };

    // ---- what starts ----

    [Fact]
    public void AnApplication_IsStartedThroughTheShell_WithItsArgumentsUnchanged()
    {
        var command = ExecutionPolicy.Resolve(Plan(@"C:\tools\regedit.exe /m ""a b"""), ExecutionPlatform.Windows)!;

        Assert.True(command.Elevated);
        Assert.True(command.UseShellExecute); // only the shell can raise the consent prompt
        Assert.Equal(@"C:\tools\regedit.exe", command.FileName);
        Assert.Equal(new[] { "/m", "a b" }, command.Arguments); // still a list, never a line to re-read
    }

    [Fact]
    public void AScript_KeepsItsInterpreter_AndOnlyGainsTheVerb()
    {
        var plain = ExecutionPolicy.Resolve(Plan(@"C:\tools\deploy.ps1 west") with { Elevated = false },
            ExecutionPlatform.Windows)!;
        var elevated = ExecutionPolicy.Resolve(Plan(@"C:\tools\deploy.ps1 west"), ExecutionPlatform.Windows)!;

        Assert.False(plain.Elevated);
        Assert.Equal(plain with { UseShellExecute = true, Elevated = true }, elevated with { Arguments = plain.Arguments });
        Assert.Equal(plain.Arguments, elevated.Arguments);
        Assert.Equal("powershell.exe", elevated.FileName);
    }

    [Fact]
    public void ABatchFile_RunsElevatedThroughCmd_AndKeepsItsRefusals()
    {
        var command = ExecutionPolicy.Resolve(Plan(@"C:\tools\build.bat release"), ExecutionPlatform.Windows)!;
        Assert.True(command.Elevated);
        Assert.Equal("cmd.exe", command.FileName);
        Assert.Equal(new[] { "/c", @"C:\tools\build.bat", "release" }, command.Arguments);

        // The shell pastes the list together by the same rules, so what cmd.exe would read as a second command
        // is refused exactly as it is unelevated — the more so with an administrator's rights behind it.
        var smuggled = Plan(@"C:\tools\build.bat %C%", clipboardText: "release & calc.exe");
        Assert.Equal(ExecutionKind.None, smuggled.Kind);
    }

    [Fact]
    public void WithoutTheMarker_NothingChanges()
    {
        var plan = ExecutionPolicy.Plan(@"C:\tools\regedit.exe", null, null, ExecutionPlatform.Windows, Env, Disk);
        Assert.False(plan.Elevated);

        var command = ExecutionPolicy.Resolve(plan, ExecutionPlatform.Windows)!;
        Assert.False(command.Elevated);
        Assert.False(command.UseShellExecute);
    }

    [Fact]
    public void TheToastSaysWithWhoseRights()
    {
        Assert.Equal("Running deploy.ps1 as administrator", Plan(@"C:\tools\deploy.ps1").Description);
        Assert.Equal("Starting regedit as administrator", Plan(@"C:\tools\regedit.exe").Description);
        Assert.Equal("Running deploy.ps1", (Plan(@"C:\tools\deploy.ps1") with { Elevated = false }).Description);
    }

    // ---- what is refused ----

    [Theory]
    [InlineData("https://example.com")]          // a browser with every right on the machine
    [InlineData(@"C:\Docs\notes.pdf")]           // a document opens in whatever opens its kind
    [InlineData(@"C:\work\invoices")]            // a folder opens in Explorer
    [InlineData(@"C:\logs\app.log")]             // a file shown where it lies
    public void OnlyAScriptOrAnApplication_CanRunAsAdministrator(string text)
    {
        var plan = Plan(text);
        Assert.NotEqual(ExecutionKind.None, plan.Kind);

        Assert.Equal("Only a script or an application can run as administrator.",
            ExecutionPolicy.WhyNotElevated(plan, ExecutionPlatform.Windows));
        // Refused, not started without it: whoever marked it meant it to have the rights.
        Assert.Null(ExecutionPolicy.Resolve(plan, ExecutionPlatform.Windows));
    }

    [Theory]
    [InlineData(ExecutionPlatform.MacOS)]
    [InlineData(ExecutionPlatform.Linux)]
    public void ElsewhereThanWindows_ItIsRefused_RatherThanRunWithoutTheRights(ExecutionPlatform platform)
    {
        var plan = Plan("/home/sam/backup.sh", platform);
        Assert.Equal(ExecutionKind.Script, plan.Kind);

        Assert.Equal("Running as administrator is Windows only.", ExecutionPolicy.WhyNotElevated(plan, platform));
        Assert.Null(ExecutionPolicy.Resolve(plan, platform));
    }

    [Fact]
    public void APlanThatAsksNothing_OrCannotRunAtAll_HasNoElevationProblem()
    {
        Assert.Null(ExecutionPolicy.WhyNotElevated(Plan(@"C:\tools\regedit.exe"), ExecutionPlatform.Windows));
        Assert.Null(ExecutionPolicy.WhyNotElevated(
            Plan("https://example.com") with { Elevated = false }, ExecutionPlatform.Windows));

        // A plan that runs nothing has its own reason, and that is the one worth saying.
        var nothing = Plan("docker system prune -af");
        Assert.Equal(ExecutionKind.None, nothing.Kind);
        Assert.Null(ExecutionPolicy.WhyNotElevated(nothing, ExecutionPlatform.Windows));
    }

    // ---- what the editor can tell before Enter ----

    [Theory]
    [InlineData(@"C:\tools\deploy.ps1 --env west", true)]
    [InlineData(@"C:\tools\build.bat", true)]
    [InlineData(@"C:\tools\regedit.exe", true)]
    [InlineData(@"file:///C:/tools/deploy.ps1", true)]
    [InlineData("%C%", true)] // taken on trust, as LooksExecutable takes it
    [InlineData("https://example.com", false)]
    [InlineData(@"C:\Docs\notes.pdf", false)]
    [InlineData(@"C:\logs\app.log", false)]
    [InlineData("docker system prune -af", false)]
    public void LooksElevatable_ReadsTheFirstWord_AsTheLauncherWill(string text, bool expected) =>
        Assert.Equal(expected, ExecutionPolicy.LooksElevatable(text, ExecutionPlatform.Windows, Env, Disk));

    // ---- the marker, as the user meets it ----

    /// <summary>
    /// A script rather than an .exe: a script is one on every platform, so the view model plans it the same
    /// way whichever machine runs the suite. Whether the rights can then be had is the launcher's question.
    /// </summary>
    private static readonly string DeployScript = Path.Combine(Path.GetTempPath(), "deploy.ps1");

    private static Snippet Deploy(bool executable = true, bool administrator = true, string label = "Deploy") => new()
    {
        Label = label,
        Content = DeployScript,
        QuickCode = "dep",
        IsExecutable = executable,
        RunAsAdministrator = administrator,
    };

    private static (MainViewModel Vm, List<ExecutionPlan> Ran) NewVm(params Snippet[] snippets)
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-elevation-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var snippet in snippets) store.Add(snippet);

        var ran = new List<ExecutionPlan>();
        var vm = new MainViewModel(store)
        {
            ClipboardWriter = _ => Task.CompletedTask,
            ClipboardReader = () => Task.FromResult<string?>(null),
            Executor = plan =>
            {
                ran.Add(plan);
                return Task.FromResult(new ExecutionResult(true, plan.Description));
            },
        };
        return (vm, ran);
    }

    [AvaloniaFact]
    public async Task AMarkedSnippet_AsksTheLauncherForTheRights()
    {
        var (vm, ran) = NewVm(Deploy());

        await vm.ActivateCommand.ExecuteAsync(vm.Filtered[0]);

        var plan = Assert.Single(ran);
        Assert.Equal(ExecutionKind.Script, plan.Kind);
        Assert.True(plan.Elevated);
    }

    [AvaloniaFact]
    public async Task AnUnmarkedSnippet_DoesNot()
    {
        var (vm, ran) = NewVm(Deploy(administrator: false));

        await vm.ActivateCommand.ExecuteAsync(vm.Filtered[0]);

        Assert.False(Assert.Single(ran).Elevated);
    }

    [AvaloniaFact]
    public async Task ItsHotkey_AsksForTheRightsToo()
    {
        var snippet = Deploy();
        var (vm, ran) = NewVm(snippet);

        Assert.Equal(TriggerOutcome.Done, await vm.TriggerSnippetAsync(snippet.Id));

        Assert.True(Assert.Single(ran).Elevated);
    }

    [AvaloniaFact]
    public async Task WithoutExecute_TheMarkerMeansNothing_AndTheSnippetIsCopied()
    {
        var (vm, ran) = NewVm(Deploy(executable: false));
        var row = vm.Filtered[0];

        Assert.False(row.RunsAsAdministrator);
        await vm.ActivateCommand.ExecuteAsync(row);

        Assert.Empty(ran);
    }

    [AvaloniaFact]
    public void TheRowSaysSo_BeforeWindowsDoes()
    {
        var (vm, _) = NewVm(Deploy(), Deploy(administrator: false, label: "Deploy, as me"));

        var admin = vm.Filtered.Single(r => r.Label == "Deploy");
        var plain = vm.Filtered.Single(r => r.Label == "Deploy, as me");

        Assert.Equal("run as admin", admin.RunMarker);
        Assert.Contains("run as admin", admin.EnterHint);
        Assert.Contains("copy", admin.EnterHint); // …and Ctrl+Enter still copies it

        Assert.Equal("run", plain.RunMarker);
        Assert.DoesNotContain("admin", plain.EnterHint);
    }

    [AvaloniaFact]
    public void TheEditor_LoadsAndSavesTheMarker()
    {
        var (vm, _) = NewVm(Deploy(administrator: false));
        var row = (SnippetViewModel)vm.Filtered[0];

        vm.EditCommand.Execute(row);
        var editor = vm.Editor!;
        Assert.False(editor.RunAsAdministrator);

        editor.RunAsAdministrator = true;
        editor.SaveCommand.Execute(null);

        Assert.True(row.Model.RunAsAdministrator);
        Assert.True(row.RunsAsAdministrator);
        Assert.Equal("run as admin", row.RunMarker); // the row follows the model it was given

        vm.EditCommand.Execute(row);
        Assert.True(vm.Editor!.RunAsAdministrator);
    }

    [AvaloniaFact]
    public void TakingExecuteOff_TakesTheMarkerWithIt()
    {
        var (vm, _) = NewVm(Deploy());
        var row = (SnippetViewModel)vm.Filtered[0];

        vm.EditCommand.Execute(row);
        vm.Editor!.IsExecutable = false;
        Assert.False(vm.Editor.ShowsRunAsAdministrator); // it qualifies Execute, so it goes with it
        vm.Editor.SaveCommand.Execute(null);

        // A copy has no rights to ask for, so the marker is not left behind to surprise anyone later.
        Assert.False(row.Model.IsExecutable);
        Assert.False(row.Model.RunAsAdministrator);
    }

    [AvaloniaFact]
    public void TheChip_IsOfferedOnWindows_AndWhereverASnippetAlreadyCarriesIt()
    {
        var fresh = new EditorViewModel(null, (_, _) => { }, () => { }) { Content = @"C:\tools\deploy.ps1" };
        Assert.False(fresh.ShowsRunAsAdministrator); // only beside Execute
        fresh.IsExecutable = true;
        Assert.Equal(OperatingSystem.IsWindows(), fresh.ShowsRunAsAdministrator);

        // Marked on a Windows box and synced elsewhere: it can still be taken off there.
        var synced = new EditorViewModel(Deploy(), (_, _) => { }, () => { });
        Assert.True(synced.ShowsRunAsAdministrator);
    }

    [AvaloniaFact]
    public void TheEditorWarns_WhenTheRightsCannotBeHad()
    {
        var editor = new EditorViewModel(null, (_, _) => { }, () => { })
        {
            Content = "https://example.com",
            IsExecutable = true,
        };
        Assert.False(editor.ExecuteHintIsWarning);

        // A link can run, but not as administrator — said now, rather than as a refusal at Enter.
        editor.RunAsAdministrator = true;
        Assert.True(editor.ExecuteHintIsWarning);
        Assert.Contains("administrator", editor.ExecuteHint);

        // A script can, on Windows; anywhere else the hint says it is Windows only.
        editor.Content = DeployScript;
        Assert.Equal(!OperatingSystem.IsWindows(), editor.ExecuteHintIsWarning);
        Assert.Contains(OperatingSystem.IsWindows() ? "once Windows has asked" : "Windows only", editor.ExecuteHint);

        // Something that cannot run at all says that first: the rights it would have had are beside the point.
        editor.Content = "docker system prune -af";
        Assert.True(editor.ExecuteHintIsWarning);
        Assert.Contains("not a link, a full path, an application or a script", editor.ExecuteHint);
    }

    [AvaloniaFact]
    public void TheMarkerSurvivesARoundTripThroughTheStore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"klippy-elevation-{Guid.NewGuid():N}.json");
        var store = new SnippetStore(path, seedIfEmpty: false);
        store.Add(Deploy());
        store.Add(Deploy(administrator: false, label: "Deploy, as me"));

        var reloaded = new SnippetStore(path, seedIfEmpty: false);

        Assert.True(reloaded.Snippets.Single(s => s.Label == "Deploy").RunAsAdministrator);
        Assert.False(reloaded.Snippets.Single(s => s.Label == "Deploy, as me").RunAsAdministrator);
    }
}
