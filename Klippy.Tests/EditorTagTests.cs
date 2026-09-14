using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Xunit;

namespace Klippy.Tests;

/// <summary>The tags already in use, offered under the editor's TAG field.</summary>
public class EditorTagTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"klippy-editor-tags-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SnippetStore NewStore(params (string Label, string Tag)[] snippets)
    {
        var store = new SnippetStore(Path.Combine(_dir, $"store-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        foreach (var (label, tag) in snippets)
            store.Add(new Snippet { Label = label, Content = "x", Tag = tag });
        return store;
    }

    private static EditorViewModel NewEditor(params string[] knownTags) =>
        new(null, (_, _) => { }, () => { }, knownTags: knownTags);

    private static EditorViewModel EditorFor(Snippet existing, params string[] knownTags) =>
        new(existing, (_, _) => { }, () => { }, knownTags: knownTags);

    private static string[] Offered(EditorViewModel editor) =>
        editor.TagSuggestions.Select(t => t.Name).ToArray();

    /// <summary>The highlighted chip, i.e. the tag the field currently holds. Null when none is.</summary>
    private static string? Selected(EditorViewModel editor) =>
        editor.TagSuggestions.FirstOrDefault(t => t.IsSelected)?.Name;

    private static void Click(EditorViewModel editor, string tag) =>
        editor.SelectTagCommand.Execute(editor.TagSuggestions.First(t => t.Name == tag));

    // ---- what the editor offers ----

    [Fact]
    public void ANewSnippetIsOfferedEveryTagInUse()
    {
        var editor = NewEditor("banking", "dev", "work");

        Assert.True(editor.HasTagSuggestions);
        Assert.Equal(new[] { "banking", "dev", "work" }, Offered(editor));
        Assert.Null(Selected(editor)); // nothing typed, so no tag is the snippet's yet
    }

    [Fact]
    public void EditingASnippetHighlightsTheTagItAlreadyCarries()
    {
        var editor = EditorFor(new Snippet { Label = "A", Content = "x", Tag = "dev" },
            "banking", "dev", "work");

        Assert.Equal("dev", Selected(editor));
        Assert.Equal(new[] { "banking", "dev", "work" }, Offered(editor));
    }

    [Fact]
    public void NoTagsInUseMeansNothingToOffer()
    {
        var editor = NewEditor();

        Assert.False(editor.HasTagSuggestions); // the row hides rather than showing an empty gap
        Assert.Empty(editor.TagSuggestions);
    }

    // ---- picking one ----

    [Fact]
    public void ClickingATagPutsItInTheField_AndClickingItAgainTakesItOut()
    {
        var editor = NewEditor("banking", "dev", "work");

        Click(editor, "work");
        Assert.Equal("work", editor.Tag);
        Assert.Equal("work", Selected(editor));

        Click(editor, "work");
        Assert.Equal("", editor.Tag); // the pointer-only way back to untagged
        Assert.Null(Selected(editor));
    }

    [Fact]
    public void ClickingAnotherTagMovesTheSnippetToIt()
    {
        var editor = EditorFor(new Snippet { Label = "A", Content = "x", Tag = "dev" },
            "banking", "dev", "work");

        Click(editor, "banking");

        Assert.Equal("banking", editor.Tag);
        Assert.Equal("banking", Selected(editor));
    }

    // ---- what typing does to the list ----

    [Fact]
    public void APartTypedTagNarrowsToWhatItCouldStillBecome()
    {
        var editor = NewEditor("dev", "devops", "work");

        editor.Tag = "de";

        Assert.Equal(new[] { "dev", "devops" }, Offered(editor));
        Assert.Null(Selected(editor)); // "de" is not a tag in its own right
    }

    [Fact]
    public void ACompleteTagBringsTheWholeSetBack()
    {
        var editor = NewEditor("dev", "devops", "work");

        editor.Tag = "dev";

        // narrowing to dev/devops here would strand the snippet with no chip to click
        // across to work
        Assert.Equal(new[] { "dev", "devops", "work" }, Offered(editor));
        Assert.Equal("dev", Selected(editor));
    }

    [Fact]
    public void ATagLikeNothingInUseStillLeavesEveryTagOnOffer()
    {
        var editor = NewEditor("dev", "work");

        editor.Tag = "holiday";

        Assert.Equal(new[] { "dev", "work" }, Offered(editor));
        Assert.Null(Selected(editor));
    }

    [Fact]
    public void MatchingIgnoresCase_SinceSavingLowercasesTheTagAnyway()
    {
        var editor = NewEditor("dev", "work");

        editor.Tag = "WORK";

        Assert.Equal("work", Selected(editor));
    }

    [Fact]
    public void SurroundingSpaceIsIgnored_AsItIsOnSave()
    {
        var editor = NewEditor("dev", "work");

        editor.Tag = "  work ";

        Assert.Equal("work", Selected(editor));
    }

    // ---- through the list ----

    [AvaloniaFact]
    public void EveryEditorTheListOpensCarriesTheStoresTags()
    {
        var store = NewStore(("A", "work"), ("B", "dev"));
        var vm = new MainViewModel(store);

        vm.NewCommand.Execute(null);
        Assert.Equal(new[] { "dev", "work" }, Offered(vm.Editor!));

        vm.EditCommand.Execute(vm.Filtered.First(r => r.Label == "A"));
        Assert.Equal(new[] { "dev", "work" }, Offered(vm.Editor!));
        Assert.Equal("work", Selected(vm.Editor!));

        vm.DuplicateCommand.Execute(vm.Filtered.First(r => r.Label == "A"));
        Assert.Equal(new[] { "dev", "work" }, Offered(vm.Editor!));
        Assert.Equal("work", Selected(vm.Editor!)); // a duplicate starts on the original's tag
    }

    [AvaloniaFact]
    public void AnEditorOpenedFromAClipOffersTheTagsToo()
    {
        var history = ClipHistoryStore.InMemory();
        history.Add(new ClipEntry { Text = "a captured line" });
        var vm = new MainViewModel(NewStore(("A", "work")), history);
        vm.ShowHistory();

        vm.PromoteToSnippetCommand.Execute(vm.Filtered[0]);

        Assert.Equal(new[] { "work" }, Offered(vm.Editor!));
        Assert.Equal("a captured line", vm.Editor!.Content); // and still prefilled from the clip
    }

    [AvaloniaFact]
    public void APickedTagIsTheTagThatGetsSaved()
    {
        var store = NewStore(("A", "work"), ("B", "dev"));
        var vm = new MainViewModel(store);

        vm.NewCommand.Execute(null);
        var editor = vm.Editor!;
        editor.Label = "C";
        editor.Content = "z";
        Click(editor, "dev");
        editor.SaveCommand.Execute(null);

        Assert.Equal("dev", store.Entries.Single(e => e.Item.Label == "C").Item.Tag);
        // no new chip: the snippet joined a tag that was already there
        Assert.Equal(new[] { MainViewModel.AllTag, "dev", "work" }, vm.Tags.Select(t => t.Name));
    }

    [AvaloniaFact]
    public void ATagCoinedInOneEditIsOnOfferInTheNext()
    {
        var store = NewStore(("A", "work"));
        var vm = new MainViewModel(store);

        vm.NewCommand.Execute(null);
        vm.Editor!.Label = "B";
        vm.Editor.Content = "y";
        vm.Editor.Tag = "holiday";
        vm.Editor.SaveCommand.Execute(null);

        vm.NewCommand.Execute(null);
        Assert.Equal(new[] { "holiday", "work" }, Offered(vm.Editor!));
    }
}
