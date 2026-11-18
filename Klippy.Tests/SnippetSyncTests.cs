using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless.XUnit;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// The list following the snippets folder while another machine — through a sync client — writes to it
/// too: what a summons, or the folder watcher, brings in.
/// </summary>
public class SnippetSyncTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"klippy-sync-{Guid.NewGuid():N}");
    private string Folder => Path.Combine(_data, "snippets");

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private (MainViewModel Vm, SnippetStore Elsewhere) NewVm(params string[] labels)
    {
        var store = new SnippetStore(Folder, seedIfEmpty: false);
        foreach (var label in labels) store.Add(new Snippet { Label = label, Content = label });
        var vm = new MainViewModel(store, settings: new AppSettings());
        // Another machine's Klippy, as far as this one can tell: the same folder, its own memory.
        return (vm, new SnippetStore(Folder, seedIfEmpty: false));
    }

    [AvaloniaFact]
    public void ASnippetAddedElsewhere_ShowsUp_TagAndAll()
    {
        var (vm, elsewhere) = NewVm("here");

        elsewhere.Add(new Snippet { Label = "from elsewhere", Content = "x", Tag = "remote" });

        Assert.True(vm.SyncSnippets());
        Assert.Contains(vm.Filtered, r => r.Label == "from elsewhere");
        Assert.Contains(vm.Tags, t => t.Name == "remote");
    }

    [AvaloniaFact]
    public void AnEditElsewhere_ShowsInItsRow()
    {
        var (vm, elsewhere) = NewVm("before");
        var theirs = elsewhere.Snippets.Single();
        theirs.Label = "after";
        elsewhere.Update(theirs);

        vm.SyncSnippets();

        Assert.Equal("after", Assert.Single(vm.Filtered).Label);
    }

    [AvaloniaFact]
    public void ADeleteElsewhere_TakesTheRowAway()
    {
        var (vm, elsewhere) = NewVm("doomed", "kept");

        elsewhere.Delete(elsewhere.Snippets.Single(s => s.Label == "doomed").Id);
        vm.SyncSnippets();

        Assert.Equal("kept", Assert.Single(vm.Filtered).Label);
    }

    [AvaloniaFact]
    public void TheSelection_StaysOnTheRowItWasOn()
    {
        // A list that jumped back to the top because something changed on another machine would be one you
        // could not arrow through.
        var (vm, elsewhere) = NewVm("a", "b", "c");
        var picked = vm.Filtered[2];
        vm.SelectedSnippet = picked;

        elsewhere.Add(new Snippet { Label = "d", Content = "d" });
        vm.SyncSnippets();

        Assert.Equal(picked.Label, vm.SelectedSnippet?.Label);
    }

    [AvaloniaFact]
    public void NothingChanged_IsNotARefresh()
    {
        var (vm, _) = NewVm("a");
        Assert.False(vm.SyncSnippets());
    }

    [AvaloniaFact]
    public void ASettledConflict_IsSaid()
    {
        // The version put aside is somebody's edit, and the conflicts folder is the only place to find it.
        var (vm, elsewhere) = NewVm("mine");
        var snippet = elsewhere.Snippets.Single();
        var older = new Snippet
        {
            Id = snippet.Id, Label = "theirs", Content = "x", CreatedAt = snippet.CreatedAt,
            ModifiedAt = snippet.ModifiedAt.AddHours(-1),
        };
        File.WriteAllText(
            Path.Combine(Folder, $"{snippet.Id:D} (# Edit conflict 2026-09-01 KNHTB #).json"),
            System.Text.Json.JsonSerializer.Serialize(older, KlippyJsonContext.Default.Snippet));

        Assert.True(vm.SyncSnippets());

        Assert.True(vm.IsToastError);
        Assert.Contains("edited on two machines", vm.ToastText);
        Assert.Contains(Path.Combine(_data, "conflicts"), vm.ToastText);
        Assert.Equal("mine", Assert.Single(vm.Filtered).Label);
    }

    [AvaloniaFact]
    public void SavingOverAnEditMadeElsewhere_SaysWhereTheOtherWent()
    {
        var (vm, elsewhere) = NewVm("original");
        var row = (SnippetViewModel)vm.Filtered.Single();
        vm.EditCommand.Execute(row);

        var theirs = elsewhere.Snippets.Single();
        theirs.Label = "edited there";
        elsewhere.Update(theirs);
        vm.SyncSnippets();

        vm.Editor!.Label = "edited here";
        vm.Editor.SaveCommand.Execute(null);

        Assert.Equal("edited here", Assert.Single(vm.Filtered).Label);
        Assert.True(vm.IsToastError);
        Assert.Contains("changed on another machine", vm.ToastText);
        Assert.Single(Directory.GetFiles(Path.Combine(_data, "conflicts")));
    }

    [Fact]
    public void TheWatcher_SaysOnce_WhenTheFolderHasSettled()
    {
        Directory.CreateDirectory(Folder);
        var calls = 0;
        using var settled = new ManualResetEventSlim();
        using var watcher = SnippetFolderWatcher.TryStart(Folder, () =>
        {
            Interlocked.Increment(ref calls);
            settled.Set();
        }, TimeSpan.FromMilliseconds(200));
        Assert.NotNull(watcher);

        // A sync client's delivery: a burst of writes to one file.
        var path = Path.Combine(Folder, $"{Guid.NewGuid():D}.json");
        for (var i = 0; i < 5; i++) File.WriteAllText(path, $"{{ \"n\": {i} }}");

        Assert.True(settled.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Thread.Sleep(400);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AFolderThatIsNotThere_IsNotWatched()
    {
        // Costs only the live update: the look on every summons still happens.
        Assert.Null(SnippetFolderWatcher.TryStart(Path.Combine(_data, "nowhere"), () => { }));
    }
}
