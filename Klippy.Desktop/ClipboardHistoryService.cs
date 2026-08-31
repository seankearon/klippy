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
        _rules = new CaptureRules
        {
            ExcludedApps = settings.HistoryExcludedApps,
            CaptureImages = settings.HistoryCaptureImages,
            CaptureFiles = settings.HistoryCaptureFiles,
            MaxImageBytes = Math.Max(1, settings.HistoryImageLimitMb) * 1024 * 1024,
        };

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

        // Copying a file or image clip back has to own the clipboard as this process, or
        // capture cannot tell our own write from anybody else's and records it.
        if (OperatingSystem.IsWindows())
            WindowsClipboardWriter.OwnerWindow = _monitor.Handle;

        _flushTimer = new DispatcherTimer { Interval = FlushInterval };
        _flushTimer.Tick += (_, _) => Store.Flush();
        _flushTimer.Start();
    }

    // Called on the monitor's pump thread.
    private void OnClipboardChanged(ClipboardSnapshot snapshot)
    {
        var context = new CaptureContext
        {
            Kind = snapshot.Kind,
            Formats = snapshot.Formats,
            CanIncludeInClipboardHistory = snapshot.CanIncludeInClipboardHistory,
            SourceApp = snapshot.SourceApp,
            TextLength = snapshot.Text.Length,
            ImageBytes = snapshot.Image?.Length ?? 0,
            FileCount = snapshot.Files.Length,
            IsSelfWrite = snapshot.IsSelfWrite,
        };

        if (!CapturePolicy.ShouldCapture(context, _rules)) return;

        var sourceApp = snapshot.SourceApp ?? "";

        // The store backs the visible list, so it is only ever mutated on the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (snapshot is { Kind: ClipKind.Image, Image: { } bytes })
            {
                Store.AddImage(bytes, snapshot.ImageFormat, snapshot.PixelWidth, snapshot.PixelHeight, sourceApp);
                return;
            }

            Store.Add(new ClipEntry
            {
                Kind = snapshot.Kind,
                Text = snapshot.Text,
                Html = snapshot.Html,
                Files = snapshot.Files,
                SourceApp = sourceApp,
            });
        });
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
