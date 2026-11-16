using System;
using System.IO;
using System.Threading;

namespace Klippy.Services;

/// <summary>
/// Says when the snippets folder has changed under Klippy — a snippet added, edited or deleted on another
/// machine and brought here by a sync client — so the list can follow it without a restart.
///
/// Debounced: one call once the folder has been quiet for <see cref="Settle"/>, however many events led up
/// to it. A sync client delivering an edit typically creates a temp file, writes it, renames it and stamps
/// it, and a rescan per event would read the same snippet four times over — some of those while it is still
/// half there. What the caller does then is <see cref="SnippetStore.Rescan"/>, which reads only what
/// actually changed, so a call for a change Klippy made itself costs a listing and nothing more.
///
/// Not the only way a change is noticed. Sync clients do not all raise file-system events reliably — a
/// virtual drive may raise none — so the window also looks again whenever it is summoned, and this is the
/// part that makes a change show up in a window that is already open.
///
/// The callback is called on a thread-pool thread: the caller marshals it to wherever its store
/// lives.
/// </summary>
public sealed class SnippetFolderWatcher : IDisposable
{
    /// <summary>How long the folder has to be quiet before the callback is called.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly TimeSpan _settle;
    private readonly Action _changed;

    private SnippetFolderWatcher(FileSystemWatcher watcher, TimeSpan settle, Action changed)
    {
        _watcher = watcher;
        _settle = settle;
        _changed = changed;
        _timer = new Timer(_ => _changed(), null, Timeout.Infinite, Timeout.Infinite);

        _watcher.Created += (_, _) => Poke();
        _watcher.Changed += (_, _) => Poke();
        _watcher.Deleted += (_, _) => Poke();
        _watcher.Renamed += (_, _) => Poke();
        // An overflowed buffer has lost events, not changes: looking again finds them all.
        _watcher.Error += (_, _) => Poke();
    }

    /// <summary>
    /// Starts watching <paramref name="folder"/>, or returns null where it cannot be: a platform without
    /// file-system events (iOS), a folder that is not there yet, or one the OS will not watch — a network
    /// share, say. Null costs only the live update; the look on every summons still happens.
    /// </summary>
    public static SnippetFolderWatcher? TryStart(string folder, Action changed, TimeSpan? settle = null)
    {
        if (OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsBrowser()) return null;

        try
        {
            if (!Directory.Exists(folder)) return null;

            var watcher = new FileSystemWatcher(folder, "*.json")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            var started = new SnippetFolderWatcher(watcher, settle ?? Settle, changed);
            watcher.EnableRaisingEvents = true;
            return started;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException
                                    or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Restarts the quiet period: the call comes <see cref="Settle"/> after the last event, not the first.</summary>
    private void Poke()
    {
        try
        {
            _timer.Change(_settle, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // An event that arrived as the watcher was being put away.
        }
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer.Dispose();
    }
}
