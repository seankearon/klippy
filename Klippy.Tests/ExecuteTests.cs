using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Klippy.Views;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// Macros and the Execute action as the user meets them: a quick-code typed with
/// arguments after it, a copy that puts the expansion on the clipboard, and a run that
/// hands the item and those arguments to the execution engine.
/// </summary>
public class ExecuteTests
{
    private const string SearchUrl = "https://www.google.com/search?q=%P%";

    private static Snippet GoogleSearch => new()
    {
        Label = "Google search",
        Content = SearchUrl,
        Tag = "web",
        QuickCode = "?",
    };

    private static Snippet DockerPrune => new()
    {
        Label = "Docker prune",
        Content = "docker system prune -af --volumes",
        Tag = "dev",
        QuickCode = "dp",
    };

    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required bool Executable { get; init; }
        public required string? Clipboard { get; init; }
        public List<ExecutionPlan> Ran { get; } = new();
        public string? Copied { get; set; }
        public int Closes { get; set; }

        public RowViewModel Row(string label) => Vm.Filtered.First(r => r.Label == label);

        /// <summary>
        /// Puts the test's clipboard and execution engine in place. Called again after a
        /// window opens, since MainWindow wires the real ones on the way up — and the
        /// real one would go looking for a browser.
        /// </summary>
        public Fixture Attach()
        {
            Vm.ClipboardWriter = payload => { Copied = payload.Plain; return Task.CompletedTask; };
            Vm.ClipboardReader = () => Task.FromResult(Clipboard);
            Vm.Executor = Executable
                ? plan =>
                {
                    Ran.Add(plan);
                    return Task.FromResult(new ExecutionResult(true, plan.Description));
                }
                : null;
            return this;
        }
    }

    private static Fixture NewVm(bool executable = true, string? clipboard = null, params Snippet[] snippets)
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-exec-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var snippet in snippets.Length > 0 ? snippets : new[] { GoogleSearch, DockerPrune })
            store.Add(snippet);

        var fixture = new Fixture
        {
            Vm = new MainViewModel(store),
            Executable = executable,
            Clipboard = clipboard,
        };
        fixture.Vm.CloseRequested += () => fixture.Closes++;
        return fixture.Attach();
    }

    // ---- typing "code argument…" ----

    [AvaloniaFact]
    public void AQuickCodeWithArguments_ShowsThatSnippetAlone_AndPreviewsTheResult()
    {
        var f = NewVm();

        f.Vm.FilterText = "? stuff";

        var row = Assert.Single(f.Vm.Filtered);
        Assert.Equal("Google search", row.Label);
        // The row shows what the arguments make of it, so the result is visible before
        // Enter is pressed.
        Assert.Equal("https://www.google.com/search?q=stuff", row.Content);
        Assert.Equal(new[] { "stuff" }, row.Arguments);
        // …while the item itself is untouched: the engine wants the placeholders.
        Assert.Equal(SearchUrl, row.Template);
    }

    [AvaloniaFact]
    public void OnlyAnExactQuickCodeInvokes_SoOrdinarySearchesStillSearch()
    {
        var f = NewVm();

        // "dp" is a quick-code; "do" is not, so this stays a two-token search and finds
        // the snippet whose content has both words.
        f.Vm.FilterText = "do sys";
        Assert.Equal("Docker prune", Assert.Single(f.Vm.Filtered).Label);
        Assert.Empty(f.Vm.Filtered[0].Arguments);
    }

    [AvaloniaFact]
    public void ArgumentsAreDropped_WhenTheLineIsNoLongerAnInvocation()
    {
        var f = NewVm();

        f.Vm.FilterText = "? stuff";
        var row = f.Vm.Filtered[0];
        Assert.Equal("https://www.google.com/search?q=stuff", row.Content);

        f.Vm.FilterText = "google";
        Assert.Same(row, f.Vm.Filtered[0]); // same cached row…
        Assert.Equal(SearchUrl, row.Content); // …showing the snippet again, not yesterday's search
        Assert.Empty(row.Arguments);
    }

    [AvaloniaFact]
    public void ACodeWithNothingTypedAfterIt_KeepsItsSnippetOnScreen()
    {
        var f = NewVm();

        f.Vm.FilterText = "? ";

        Assert.Equal("Google search", Assert.Single(f.Vm.Filtered).Label);
        Assert.Empty(f.Vm.Filtered[0].Arguments);
    }

    // ---- copying ----

    [AvaloniaFact]
    public async Task Copying_PutsTheExpansionOnTheClipboard_NotThePlaceholders()
    {
        var f = NewVm();
        f.Vm.FilterText = "? stuff";

        await f.Vm.CopySelectedCommand.ExecuteAsync(null);

        Assert.Equal("https://www.google.com/search?q=stuff", f.Copied);
    }

    [AvaloniaFact]
    public async Task Copying_FillsTheClipboardMacroFromTheClipboard()
    {
        var f = NewVm(clipboard: "Sam", snippets: new Snippet
        {
            Label = "Greeting", Content = "Hi %C%, thanks!", QuickCode = "hi",
        });

        await f.Vm.CopyCommand.ExecuteAsync(f.Row("Greeting"));

        Assert.Equal("Hi Sam, thanks!", f.Copied);
    }

    [AvaloniaFact]
    public async Task AnEmptyClipboard_ExpandsToNothingRatherThanToTheMacro()
    {
        var f = NewVm(clipboard: null, snippets: new Snippet { Label = "Greeting", Content = "Hi %C%" });

        await f.Vm.CopyCommand.ExecuteAsync(f.Row("Greeting"));

        Assert.Equal("Hi ", f.Copied);
    }

    // ---- executing ----

    [AvaloniaFact]
    public async Task Executing_SendsTheItemAndItsArgumentsToTheEngine()
    {
        var f = NewVm();
        f.Vm.FilterText = "? stuff";
        var row = f.Vm.Filtered[0];

        // The two things the engine is given, as the feature request puts it: the item's
        // own text, placeholders and all, and what was typed after the quick-code.
        Assert.Equal(SearchUrl, row.Template);
        Assert.Equal(new[] { "stuff" }, row.Arguments);

        await f.Vm.ExecuteCommand.ExecuteAsync(row);

        var plan = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Url, plan.Kind);
        Assert.Equal("https://www.google.com/search?q=stuff", plan.Target);
    }

    [AvaloniaFact]
    public async Task Executing_DismissesTheLauncher_AndCountsAsUsingTheSnippet()
    {
        var f = NewVm();
        f.Vm.FilterText = "? stuff";
        var row = (SnippetViewModel)f.Vm.Filtered[0];
        var before = row.Model.LastUsedAt;

        await f.Vm.ExecuteCommand.ExecuteAsync(row);

        // Running something is a launcher gesture: the browser is where you are going.
        Assert.Equal(1, f.Closes);
        Assert.True(row.Model.LastUsedAt >= before);
    }

    [AvaloniaFact]
    public async Task ExecutingSomethingThatIsNotRunnable_SaysSo_AndStaysPut()
    {
        var f = NewVm();
        f.Vm.FilterText = "dp";
        var row = f.Vm.Filtered[0];

        await f.Vm.ExecuteCommand.ExecuteAsync(row);

        Assert.Empty(f.Ran);
        Assert.Equal(0, f.Closes);
        Assert.True(f.Vm.IsToastVisible);
        Assert.True(f.Vm.IsToastError);
        Assert.Contains("not a URL or a script", f.Vm.ToastText);
    }

    [AvaloniaFact]
    public async Task AFailedRun_ReportsWhatWentWrong_AndStaysPut()
    {
        var f = NewVm();
        f.Vm.Executor = _ => Task.FromResult(ExecutionResult.Failed("Script not found: /tmp/gone.sh"));
        f.Vm.FilterText = "? stuff";

        await f.Vm.ExecuteSelectedCommand.ExecuteAsync(null);

        Assert.Equal(0, f.Closes);
        Assert.True(f.Vm.IsToastError);
        Assert.Equal("Script not found: /tmp/gone.sh", f.Vm.ToastText);
    }

    [AvaloniaFact]
    public async Task WithNoExecutionEngine_NothingRuns()
    {
        // Mobile: no browser to hand a URL to and no shell to run a script in, so the
        // command is inert rather than pretending.
        var f = NewVm(executable: false);
        f.Vm.FilterText = "? stuff";

        await f.Vm.ExecuteCommand.ExecuteAsync(f.Vm.Filtered[0]);

        Assert.Empty(f.Ran);
        Assert.False(f.Vm.IsToastVisible);
    }

    [AvaloniaFact]
    public async Task TheClipboardMacroIsResolvedForTheEngineToo()
    {
        var f = NewVm(clipboard: "https://klippy.app/docs",
            snippets: new Snippet { Label = "Open copied link", Content = "%C%" });

        await f.Vm.ExecuteCommand.ExecuteAsync(f.Row("Open copied link"));

        Assert.Equal("https://klippy.app/docs", Assert.Single(f.Ran).Target);
    }

    [AvaloniaFact]
    public void DuplicatingAnInvokedSnippet_CopiesThePlaceholder_NotTheArgument()
    {
        var f = NewVm();
        f.Vm.FilterText = "? stuff";

        f.Vm.DuplicateCommand.Execute(f.Vm.Filtered[0]);

        // The row is showing the expansion; the snippet being duplicated is not.
        Assert.Equal(SearchUrl, f.Vm.Editor!.Content);
    }

    // ---- what the row offers ----

    [AvaloniaFact]
    public void OnlyRowsWithSomethingToRun_OfferTheAction_AndSayWhichKeyRunsIt()
    {
        var f = NewVm();

        f.Vm.FilterText = "google";
        Assert.True(f.Vm.Filtered[0].IsExecutable);
        Assert.Contains("run", f.Vm.Filtered[0].EnterHint);

        f.Vm.FilterText = "docker";
        Assert.False(f.Vm.Filtered[0].IsExecutable);
        Assert.Equal("↵ copy", f.Vm.Filtered[0].EnterHint);
    }

    [AvaloniaFact]
    public async Task AClipCanBeRunToo_WhenWhatWasCopiedIsALink()
    {
        var history = ClipHistoryStore.InMemory();
        history.Add(new ClipEntry { Text = "docker system prune", SourceApp = "WindowsTerminal" });
        history.Add(new ClipEntry { Text = "https://github.com/seankearon/Klippy", SourceApp = "chrome" });

        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-exec-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        var vm = new MainViewModel(store, history);
        var ran = new List<ExecutionPlan>();
        vm.Executor = plan => { ran.Add(plan); return Task.FromResult(new ExecutionResult(true, plan.Description)); };
        vm.ShowHistory();

        Assert.True(vm.Filtered[0].IsExecutable);   // newest first: the link
        Assert.False(vm.Filtered[1].IsExecutable);  // the shell command is text to paste

        await vm.ExecuteCommand.ExecuteAsync(vm.Filtered[0]);

        Assert.Equal("https://github.com/seankearon/Klippy", Assert.Single(ran).Target);
    }

    // ---- the keyboard ----

    [AvaloniaFact]
    public void CtrlEnterRunsTheSelectedRow_WhereEnterCopiesIt()
    {
        var f = NewVm();
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach(); // the window has just wired the real clipboard and launcher

        f.Vm.FilterText = "? stuff";

        window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, "\n");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(f.Copied); // it ran, rather than copying
        Assert.Equal("https://www.google.com/search?q=stuff", Assert.Single(f.Ran).Target);
    }
}
