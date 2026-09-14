using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klippy.Services;

/// <summary>App preferences, kept separate from snippet data so an import can never clobber them.</summary>
public sealed class AppSettings
{
    /// <summary>Global hotkey that summons the window, e.g. "Ctrl+Alt+K".</summary>
    public string Hotkey { get; set; } = HotkeySpec.PlatformDefault;

    /// <summary>Set false to leave the key alone entirely.</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>
    /// Global hotkey that summons the clipboard history, e.g. "Ctrl+Alt+J". Empty turns
    /// this one off while leaving the snippet hotkey alone.
    /// </summary>
    public string HistoryHotkey { get; set; } = HotkeySpec.HistoryPlatformDefault;

    /// <summary>The history hotkey, or null when it is switched off or unparseable.</summary>
    [JsonIgnore]
    public HotkeySpec? ParsedHistoryHotkey =>
        HotkeySpec.TryParse(HistoryHotkey, out var spec) ? spec : null;

    /// <summary>Whether to record what gets copied. Desktop only; ignored where the OS forbids capture.</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>How many clips to keep before the oldest unpinned ones are dropped.</summary>
    public int HistoryLimit { get; set; } = ClipHistoryStore.DefaultCapacity;

    /// <summary>
    /// Keep history in memory only, so nothing copied is ever written to disk. The
    /// history is a record of everything that passed through the clipboard, and that is
    /// a thing a person is entitled to decline.
    /// </summary>
    public bool HistorySessionOnly { get; set; }

    /// <summary>Set false to leave screenshots and copied pictures out of the history.</summary>
    public bool HistoryCaptureImages { get; set; } = true;

    /// <summary>Set false to leave copied file selections out of the history.</summary>
    public bool HistoryCaptureFiles { get; set; } = true;

    /// <summary>
    /// Largest picture worth keeping, in megabytes. Images are blobs on disk, so this is
    /// about not filling a drive with copies of whole screens.
    /// </summary>
    public int HistoryImageLimitMb { get; set; } = 16;

    /// <summary>
    /// Applications never recorded from, by process name ("keepass"). Password managers
    /// already mark their own clipboard writes and those are honoured regardless — this
    /// is for anything else the user would rather not have kept.
    /// </summary>
    public string[] HistoryExcludedApps { get; set; } = Array.Empty<string>();

    /// <summary>
    /// How many typed command lines the MRU keeps — the lines recalled with the down
    /// arrow. Zero turns it off and forgets the ones already recorded, which is the
    /// answer for anyone who would rather not have a written record of what they type.
    /// </summary>
    public int CommandHistoryLimit { get; set; } = CommandHistory.DefaultCapacity;

    /// <summary>
    /// Whether copying a Markdown snippet also puts an HTML flavour on the clipboard.
    /// Off makes a Markdown snippet copy as its raw source everywhere — what you want
    /// when the target is another Markdown editor rather than a rich-text box.
    /// </summary>
    public bool MarkdownToHtml { get; set; } = true;

    /// <summary>
    /// Whether the HTML puts a blank line between blocks. On suits composers that strip
    /// paragraph margins (Zendesk), where the separation would otherwise vanish; off
    /// suits Outlook and Word, which honour those margins and so space it twice over.
    /// </summary>
    public bool MarkdownDoubleSpaced { get; set; } = true;

    /// <summary>
    /// Whether the plain-text flavour of a Markdown snippet has its links reduced to bare
    /// URLs: <c>[www.qwe.com](https://www.qwe.com)</c> becomes <c>https://www.qwe.com</c>.
    /// For targets that only take plain text (the Zendesk mobile app), where the raw
    /// Markdown link syntax would otherwise land verbatim. The HTML flavour keeps its
    /// links intact, so rich-text targets lose nothing. Off by default: a Markdown editor
    /// wants the source as written.
    /// </summary>
    public bool MarkdownSanitiseLinks { get; set; }

    /// <summary>
    /// Whether copying a clip from the history dismisses the window. On by default: the
    /// history is a launcher gesture — summon, pick, paste — and leaving the window in
    /// front of the app you are pasting into just means dismissing it by hand.
    /// </summary>
    public bool CloseAfterClipboardCopy { get; set; } = true;

    /// <summary>
    /// Whether copying a snippet dismisses the window. Off by default: snippets are
    /// browsed as much as they are used, and copying two in a row is ordinary.
    /// </summary>
    public bool CloseAfterSnippetCopy { get; set; }

    /// <summary>
    /// Whether running a script or an application first brings its folder up to date with
    /// <c>git pull</c>. Off by default: it only makes sense where the things you run are
    /// kept in a checkout, it costs a round trip to the remote on every run, and it is a
    /// network call made on your behalf — all three are things to opt into. A target that
    /// is not in a repository is left alone either way, and a pull that fails is reported
    /// rather than cancelling the run.
    /// </summary>
    public bool ExecutePullFirst { get; set; }

    /// <summary>
    /// Whether a search that matched nothing may be run instead: a URL, a folder, a
    /// program, or one of the OS controls. On by default — the offer only ever appears
    /// once the list is empty, and it takes a deliberate Enter on top of that.
    /// </summary>
    public bool ExecuteUnmatched { get; set; } = true;

    /// <summary>
    /// Whether a path has to exist before it is offered. On by default: an offer to run
    /// something should mean there is something there. Off suits a network share that is
    /// slow to answer, or a path that only exists once something else has run — the OS
    /// then reports the failure instead.
    /// </summary>
    public bool ExecuteVerifyPaths { get; set; } = true;

    /// <summary>
    /// Whether hibernate, sleep, lock and restart ask first. On by default: three of the
    /// four interrupt everything you are doing, and a launcher is a place where people
    /// type fast. Off makes a typed "lock" plus Enter lock the screen outright, which is
    /// the point of the setting.
    /// </summary>
    public bool ExecuteConfirmSystemActions { get; set; } = true;

    /// <summary>
    /// Where the window lands when a hotkey summons it: "Remembered", "Centre" or
    /// "Pointer". Remembered by default, and not only because an upgrade should change
    /// nothing: a resident launcher that always comes back to the same corner becomes
    /// muscle memory, and that is worth keeping as a choice rather than a fallback.
    ///
    /// A string rather than the enum itself, for the reason the hotkeys are strings — one
    /// typo in a hand-edited file must cost this preference and nothing else.
    /// </summary>
    public string SummonPlacement { get; set; } = nameof(LauncherPlacement.Remembered);

    /// <summary>
    /// The one instance the app reads and writes. Loaded on first touch, because the
    /// shared UI needs preferences on platforms whose head never loads them itself
    /// (Android, iOS) — and where there is no settings.json a user could edit by hand.
    /// </summary>
    public static AppSettings Current
    {
        get => _current ??= Load();
        set => _current = value;
    }

    private static AppSettings? _current;

    [JsonIgnore]
    public HotkeySpec ParsedHotkey =>
        HotkeySpec.TryParse(Hotkey, out var spec) ? spec : HotkeySpec.Default;

    /// <summary>The placement, or Remembered when the value is missing or unreadable.</summary>
    [JsonIgnore]
    public LauncherPlacement ParsedSummonPlacement => PlacementPolicy.Parse(SummonPlacement);

    public static string FilePath => StorageLocations.SettingsPath;

    /// <summary>
    /// Where this instance came from, and where <see cref="Save"/> writes back. Carried on
    /// the instance so a caller that loaded from somewhere else — a test, mostly — cannot
    /// have its writes land on the real settings file.
    /// </summary>
    [JsonIgnore]
    public string SourcePath { get; set; } = FilePath;

    /// <summary>Never throws: unreadable settings fall back to defaults.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) is { } loaded)
                {
                    loaded.SourcePath = path;
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // fall through to defaults
        }
        return new AppSettings { SourcePath = path };
    }

    public void Save(string? path = null)
    {
        path ??= SourcePath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        using (var stream = File.Create(tmp))
            JsonSerializer.Serialize(stream, this, SettingsJsonContext.Default.AppSettings);
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
public sealed partial class SettingsJsonContext : JsonSerializerContext;
