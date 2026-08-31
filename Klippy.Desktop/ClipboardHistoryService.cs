using System;
using Avalonia.Threading;
using Klippy.Models;
using Klippy.Services;

namespace Klippy.Desktop;

/// <summary>
/// Ties clipboard capture to the history store: watches the clipboard, asks
/// <see cref="CapturePolicy"/> whether each change may be recorded, and flushes the
/// store to disk on a timer.
///
/// The store defers its writes, so something has to drive them. A timer plus a flush on
/// shutdown means the cost of a copy is a list insert rather than a file rewrite, at the
/// price of losing the last couple of seconds of history to a hard crash.
/// </summary>
internal sealed class ClipboardHistoryService : IDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    private readonly AppSettings _settings;
    private readonly CaptureRules _rules;
    private IClipboardMonitor? _monitor;
    private DispatcherTimer? _flushTimer;

    public ClipHistoryStore Store { get; }

    public ClipboardHistoryService(AppSettings settings)
    {
        _settings = settings;
        _rules = new CaptureRules { ExcludedApps = settings.HistoryExcludedApps };

        Store = settings.HistorySessionOnly
            ? ClipHistoryStore.InMemory(settings.HistoryLimit)
            : new ClipHistoryStore(capacity: settings.HistoryLimit);
    }

    /// <summary>True once the platform is actually delivering clipboard changes.</summary>
    public bool IsCapturing => _monitor?.IsRunning == true;

    public void Start()
    {
        if (!_settings.HistoryEnabled) return;

        _monitor = ClipboardMonitor.TryStart(OnClipboardChanged);
        if (_monitor is null) return;

        _flushTimer = new DispatcherTimer { Interval = FlushInterval };
        _flushTimer.Tick += (_, _) => Store.Flush();
        _flushTimer.Start();
    }

    // Called on the monitor's pump thread.
    private void OnClipboardChanged(ClipboardSnapshot snapshot)
    {
        var context = new CaptureContext
        {
            Formats = snapshot.Formats,
            CanIncludeInClipboardHistory = snapshot.CanIncludeInClipboardHistory,
            SourceApp = snapshot.SourceApp,
            TextLength = snapshot.Text.Length,
            IsSelfWrite = snapshot.IsSelfWrite,
        };

        if (!CapturePolicy.ShouldCapture(context, _rules)) return;

        // The store backs the visible list, so it is only ever mutated on the UI thread.
        Dispatcher.UIThread.Post(() => Store.Add(new ClipEntry
        {
            Text = snapshot.Text,
            Html = snapshot.Html,
            SourceApp = snapshot.SourceApp ?? "",
        }));
    }

    public void Dispose()
    {
        _flushTimer?.Stop();
        _flushTimer = null;

        _monitor?.Dispose();
        _monitor = null;

        // Last chance to persist: everything since the previous tick is still only in memory.
        Store.Flush();
    }
}
