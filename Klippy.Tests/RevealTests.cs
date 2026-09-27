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
/// Showing what a row names in Explorer or Finder, as the user meets it: any row, marked
/// or not, snippet or clip, through the same engine a run goes through.
///
/// The paths are real ones in a temp folder, because the gesture asks the real disk whether
/// a file is there — a made-up Windows path would simply not be, when these run on Linux.
/// </summary>
public class RevealTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), $"klippy-reveal-{Guid.NewGuid():N}");

    private string File(string name)
    {
        var path = Path.Combine(_folder, name);
        System.IO.File.WriteAllText(path, "");
        return path;
    }

    public RevealTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a temp folder left behind is not a failed test */ }
    }

    private sealed class Fixture
    {
        public required MainViewModel Vm { get; init; }
        public required ClipHistoryStore History { get; init; }
        public List<ExecutionPlan> Ran { get; } = new();
        public string? Copied { get; set; }
        public int Closes { get; set; }

        public RowViewModel Row(string label) => Vm.Filtered.First(r => r.Label == label);

        /// <summary>
        /// Puts the test's clipboard and execution engine in place. Called again after a
        /// window opens, since MainWindow wires the real ones on the way up — and the real
        /// one would open Explorer.
        /// </summary>
        public Fixture Attach()
        {
            Vm.ClipboardWriter = payload => { Copied = payload.Plain; return Task.CompletedTask; };
            Vm.Executor = plan =>
            {
                Ran.Add(plan);
                return Task.FromResult(new ExecutionResult(true, plan.Description));
            };
            return this;
        }
    }

    private static Fixture NewVm(params Snippet[] snippets)
    {
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-reveal-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var snippet in snippets)
            store.Add(snippet);

        var history = ClipHistoryStore.InMemory();
        var fixture = new Fixture { Vm = new MainViewModel(store, history), History = history };
        fixture.Vm.CloseRequested += () => fixture.Closes++;
        return fixture.Attach();
    }

    // ---- snippets ----

    [AvaloniaFact]
    public async Task AnUnmarkedSnippetNamingAFolder_OpensIt_AndIsNotCopied()
    {
        var f = NewVm(new Snippet { Label = "Work", Content = _folder });

        await f.Vm.RevealCommand.ExecuteAsync(f.Row("Work"));

        var plan = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Folder, plan.Kind);
        Assert.Equal(_folder, plan.Target);
        Assert.Null(f.Copied);          // showing is not copying
        Assert.Equal(1, f.Closes);      // the file manager is where you are going next
    }

    [AvaloniaFact]
    public async Task ASnippetNamingAFile_ShowsItInItsFolder_SpacesAndAll()
    {
        var file = File("release notes.txt");
        var f = NewVm(new Snippet { Label = "Notes", Content = file });

        await f.Vm.RevealCommand.ExecuteAsync(f.Row("Notes"));

        var plan = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Reveal, plan.Kind);
        Assert.Equal(file, plan.Target);
    }

    [AvaloniaFact]
    public async Task AMarkedSnippetIsShown_NotRun()
    {
        // Whatever a row is marked for, this is where it is rather than what it does.
        var script = File("deploy.sh");
        var f = NewVm(new Snippet { Label = "Deploy", Content = $"{script} --now", IsExecutable = true });

        await f.Vm.RevealCommand.ExecuteAsync(f.Row("Deploy"));

        var plan = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Reveal, plan.Kind);
        Assert.Equal(script, plan.Target);
    }

    [AvaloniaFact]
    public async Task TheArgumentsTypedAfterAQuickCodeAreFilledIn()
    {
        // Read as a copy would put it on the clipboard: "n notes.txt" is that file.
        var file = File("notes.txt");
        var f = NewVm(new Snippet
        {
            Label = "In the work folder", Content = Path.Combine(_folder, "%P%"), QuickCode = "n",
        });
        f.Vm.FilterText = "n notes.txt";

        await f.Vm.RevealSelectedCommand.ExecuteAsync(null);

        Assert.Equal(file, Assert.Single(f.Ran).Target);
    }

    [AvaloniaFact]
    public async Task TextThatNamesNoPath_SaysSo_AndStaysPut()
    {
        var f = NewVm(new Snippet { Label = "Sign-off", Content = "Best regards, Sam" });

        await f.Vm.RevealCommand.ExecuteAsync(f.Row("Sign-off"));

        Assert.Empty(f.Ran);
        Assert.Equal(0, f.Closes); // or the message is never read
        Assert.True(f.Vm.IsToastError);
        Assert.Contains("full path", f.Vm.ToastText);
    }

    [AvaloniaFact]
    public async Task APathThatIsNotThere_SaysWhichOne()
    {
        var gone = Path.Combine(_folder, "gone.log");
        var f = NewVm(new Snippet { Label = "Gone", Content = gone });

        await f.Vm.RevealCommand.ExecuteAsync(f.Row("Gone"));

        Assert.Empty(f.Ran);
        Assert.True(f.Vm.IsToastError);
        Assert.Equal($"Not found: {gone}", f.Vm.ToastText);
    }

    [AvaloniaFact]
    public async Task WithNothingToShowThingsWith_ItSaysSo()
    {
        var f = NewVm(new Snippet { Label = "Work", Content = _folder });
        f.Vm.Executor = null;

        await f.Vm.RevealCommand.ExecuteAsync(f.Row("Work"));

        Assert.True(f.Vm.IsToastError);
        Assert.Equal(0, f.Closes);
    }

    // ---- clips ----

    [AvaloniaFact]
    public async Task AClipOfFiles_ShowsTheFirstOfThem()
    {
        var first = File("invoice.pdf");
        var second = File("receipts.zip");
        var f = NewVm();
        f.History.Add(new ClipEntry
        {
            Kind = ClipKind.Files,
            Files = new[] { first, second },
            Text = first + "\r\n" + second,
        });
        f.Vm.ShowHistory();

        await f.Vm.RevealSelectedCommand.ExecuteAsync(null);

        var plan = Assert.Single(f.Ran);
        Assert.Equal(ExecutionKind.Reveal, plan.Kind);
        Assert.Equal(first, plan.Target);
    }

    [AvaloniaFact]
    public async Task AClipOfText_IsThePathItHolds()
    {
        var f = NewVm();
        f.History.Add(new ClipEntry { Text = $"\"{_folder}\"" }); // Copy as path, quotes and all
        f.Vm.ShowHistory();

        await f.Vm.RevealSelectedCommand.ExecuteAsync(null);

        Assert.Equal(_folder, Assert.Single(f.Ran).Target);
    }

    // ---- which rows offer it ----

    [AvaloniaFact]
    public void OnlyARowThatNamesAPath_OffersToShowIt()
    {
        var f = NewVm(
            new Snippet { Label = "Work", Content = _folder },
            new Snippet { Label = "Sign-off", Content = "Best regards, Sam" },
            new Snippet { Label = "Gone", Content = Path.Combine(_folder, "gone.log") });

        Assert.True(f.Row("Work").NamesAPath);
        Assert.False(f.Row("Sign-off").NamesAPath);
        Assert.True(f.Row("Gone").NamesAPath); // the text says path; the click finds out it is gone

        f.History.Add(new ClipEntry { Kind = ClipKind.Files, Files = new[] { _folder }, Text = _folder });
        f.History.Add(new ClipEntry { Text = "Best regards, Sam" });
        f.Vm.ShowHistory();

        Assert.True(f.Vm.Filtered.OfType<ClipViewModel>().Single(c => c.IsFiles).NamesAPath);
        Assert.False(f.Vm.Filtered.OfType<ClipViewModel>().Single(c => !c.IsFiles).NamesAPath);
    }

    [AvaloniaFact]
    public void EditingARow_AsksTheQuestionAgain()
    {
        var f = NewVm(new Snippet { Label = "Work", Content = "Best regards, Sam" });
        var row = (SnippetViewModel)f.Row("Work");
        Assert.False(row.NamesAPath);

        row.Model.Content = _folder;
        row.NotifyModelChanged();

        Assert.True(row.NamesAPath);
    }

    // ---- the keyboard ----

    [AvaloniaFact]
    public void CtrlR_ShowsTheSelectedRow()
    {
        var f = NewVm(new Snippet { Label = "Work", Content = _folder });
        var window = new MainWindow { DataContext = f.Vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        f.Attach();

        var cmdMod = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPress(Key.R, cmdMod, PhysicalKey.R, "r");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(_folder, Assert.Single(f.Ran).Target);
        Assert.Null(f.Copied);
    }
}
