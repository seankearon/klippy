using System;
using System.Linq;
using Avalonia.Threading;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// Keeping the list in step with the snippets folder while another machine — through a sync client —
/// adds, edits and deletes in it too. See <see cref="SnippetStore.Rescan"/> for what is read and what is
/// settled; this half is about showing it.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The folder watcher, where there is one — see <see cref="WatchSnippetFolder"/>.</summary>
    private SnippetFolderWatcher? _snippetWatcher;

    /// <summary>
    /// Whether this view model was asked to follow the folder. Remembered, because a folder that was not
    /// there to watch at startup — nothing saved yet — is one the first save creates, and the next
    /// summons can start watching it.
    /// </summary>
    private bool _watchesSnippetFolder;

    /// <summary>
    /// Starts following the snippets folder as it changes, so an edit made on another machine shows up
    /// here without a restart, in a window that is already open. Desktop only: a phone has no folder anyone
    /// can point at a synced drive, and iOS has no file-system events to follow. Called by the app's own
    /// view model rather than by every one, so a test's throwaway store never leaves a watcher running.
    /// </summary>
    public void WatchSnippetFolder()
    {
        if (_snippetWatcher is not null || OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return;
        _watchesSnippetFolder = true;

        _snippetWatcher = SnippetFolderWatcher.TryStart(
            _store.FolderPath,
            // The watcher calls from a thread-pool thread; the store and the list live on the UI thread.
            () => Dispatcher.UIThread.Post(() => SyncSnippets()));
    }

    /// <summary>
    /// Looks at the snippets folder again and brings the list up to date — when the window is summoned, and
    /// when the folder watcher says the folder has settled. Cheap when nothing has changed, which is nearly
    /// always. Returns whether anything did.
    ///
    /// Rows are rebuilt only for the snippets that changed, and the selection stays on the row it was on if
    /// that row is still there: a list that jumped back to the top because a snippet changed on another
    /// machine would be one you could not arrow through.
    /// </summary>
    public bool SyncSnippets()
    {
        if (_watchesSnippetFolder && _snippetWatcher is null) WatchSnippetFolder();

        var changes = _store.Rescan();
        if (!changes.Any) return false;

        foreach (var changed in changes.Changed) _rowCache.Remove(changed);

        var selected = (SelectedSnippet as SnippetViewModel)?.Model.Id;
        RebuildTags();
        Refresh();
        if (selected is { } id && SelectedSnippet is not null &&
            Filtered.OfType<SnippetViewModel>().FirstOrDefault(r => r.Model.Id == id) is { } row)
            SelectedSnippet = row;

        OnSnippetsChanged(); // a snippet from elsewhere may bring shortcuts and hotkeys with it

        if (changes.ConflictsResolved > 0) ReportConflicts(changes.ConflictsResolved);
        return true;
    }

    /// <summary>
    /// Says that a sync client had kept two versions of a snippet and the older one has been put aside. Rare
    /// — it takes the same snippet edited on two machines before they synced — and worth saying when it
    /// happens: the version put aside is somebody's edit, and the conflicts folder is the only place to
    /// find it.
    /// </summary>
    private void ReportConflicts(int count) =>
        ShowToast(
            (count == 1
                ? "A snippet was edited on two machines. Kept the newer version; the other is in "
                : $"{count} snippets were edited on two machines. Kept the newer versions; the others are in ")
            + _store.ConflictsFolderPath,
            isError: true);
}
