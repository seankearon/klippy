using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>
/// Backs the Settings overlay: the preferences that change what a copy puts on the
/// clipboard.
///
/// Desktop users could always edit settings.json by hand, but mobile has neither window
/// chrome to hang a footer link on nor a settings file anyone can reach — so the choices
/// that alter a paste need a screen of their own. Each toggle saves as it is flipped;
/// there is no OK button to forget to press, and a copy made straight afterwards uses
/// the new setting because the whole app shares <see cref="AppSettings.Current"/>.
///
/// The FILES block at the bottom names each of Klippy's files separately — see
/// <see cref="StorageFileViewModel"/> for why one folder for all of them is not enough.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action _close;

    /// <summary>
    /// Opens a file or a folder, through the same launcher an item marked Execute uses.
    /// Null on mobile and in tests that do not care, which is what <see cref="CanOpenFiles"/>
    /// reports so the buttons are absent rather than dead.
    /// </summary>
    private readonly Func<ExecutionPlan, Task<ExecutionResult>>? _executor;

    /// <summary>Suppresses saving while the constructor seeds the properties.</summary>
    private readonly bool _loaded;

    /// <summary>Whether a Markdown snippet also copies an HTML flavour for rich-text editors.</summary>
    [ObservableProperty]
    private bool _markdownToHtml;

    /// <summary>Whether that HTML puts a blank line between blocks.</summary>
    [ObservableProperty]
    private bool _markdownDoubleSpaced;

    /// <summary>Whether the plain-text flavour reduces Markdown links to their bare URL.</summary>
    [ObservableProperty]
    private bool _markdownSanitiseLinks;

    /// <summary>Whether copying a clip from the history dismisses the window.</summary>
    [ObservableProperty]
    private bool _closeAfterClipboardCopy;

    /// <summary>Whether copying a snippet dismisses the window.</summary>
    [ObservableProperty]
    private bool _closeAfterSnippetCopy;

    /// <summary>Where the window lands when a hotkey summons it.</summary>
    [ObservableProperty]
    private LauncherPlacement _summonPlacement;

    /// <summary>Whether running a script or an application pulls its folder first.</summary>
    [ObservableProperty]
    private bool _executePullFirst;

    /// <summary>Whether a search that matched nothing may be run instead.</summary>
    [ObservableProperty]
    private bool _executeUnmatched;

    /// <summary>Whether a path has to exist before it is offered.</summary>
    [ObservableProperty]
    private bool _executeVerifyPaths;

    /// <summary>Whether hibernate, sleep, lock and restart ask first.</summary>
    [ObservableProperty]
    private bool _executeConfirmSystemActions;

    [ObservableProperty]
    private string? _statusText;

    /// <summary>
    /// The data folder, as configured: the folder a file named by a bare name lands in,
    /// and empty for the platform's own app-data folder. Editable, like the files below
    /// it — hunting down settings.json to change a path is the sort of errand this screen
    /// exists to spare you.
    /// </summary>
    [ObservableProperty]
    private string _dataFolder;

    /// <summary>Why the typed folder is unusable, or null while it is fine.</summary>
    private string? _dataFolderProblem;

    /// <summary>What <see cref="DataFolder"/> resolves to, which is what the Open button acts on.</summary>
    public string DataFolderPath { get; private set; } = "";

    /// <summary>What an empty box means, shown in it while it is empty.</summary>
    public string DataFolderWatermark => StorageLocations.DefaultDirectory;

    /// <summary>
    /// The state of that folder in a few words, or what is wrong with the path. A folder
    /// is only taken up at startup, so one typed here is a promise about the next launch.
    /// </summary>
    public string DataFolderSummary { get; private set; } = "";

    /// <summary>
    /// The line under the box, as <see cref="StorageFileViewModel.StatusText"/> builds
    /// one, with two differences. A path Klippy will not use has nothing worth resolving,
    /// so the complaint stands on its own. And an empty box is already showing the answer:
    /// unlike a file, whose watermark is a bare name worth resolving on screen, this one
    /// watermarks the folder itself — printing it again underneath reads like a second
    /// setting.
    /// </summary>
    public string DataFolderStatusText =>
        _dataFolderProblem is not null ||
        string.Equals(
            StorageLocations.NormalizeSeparators(DataFolder?.Trim() ?? ""),
            DataFolderPath,
            StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(DataFolder)
            ? DataFolderSummary
            : $"{DataFolderPath}  —  {DataFolderSummary}";

    /// <summary>Where these preferences are written. Shown so a desktop user can find the file.</summary>
    public string SettingsPath => _settings.SourcePath;

    /// <summary>The snippet store.</summary>
    public StorageFileViewModel Snippets { get; }

    /// <summary>The captured clipboard history, and the clip images beside it.</summary>
    public StorageFileViewModel History { get; }

    /// <summary>The command MRU.</summary>
    public StorageFileViewModel Commands { get; }

    /// <summary>The local variables file.</summary>
    public StorageFileViewModel Variables { get; }

    /// <summary>
    /// The four, in the order the screen shows them. One list rather than four blocks of
    /// markup: the rows differ in what they say, not in what they look like.
    /// </summary>
    public IReadOnlyList<StorageFileViewModel> Files { get; }

    /// <summary>
    /// Whether to show those paths at all. Mobile app storage is private and unreachable, so
    /// there the lines are noise — which is the whole reason this screen exists.
    /// </summary>
    public bool ShowSettingsPath => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>
    /// Whether the buttons that open a file or a folder can do anything. They go through
    /// the same launcher an item marked Execute does, and mobile has none.
    /// </summary>
    public bool CanOpenFiles => _executor is not null;

    /// <summary>
    /// Whether to offer the close-after-copy toggles. Only the desktop launcher has a
    /// window to dismiss; on mobile the app *is* the screen, so the choice would do nothing.
    /// </summary>
    public bool ShowCloseAfterCopy => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>
    /// Whether to offer the placement choice. Only the desktop launcher has a window to
    /// place; on mobile the app fills the screen, so there is nothing to move.
    /// </summary>
    public bool ShowSummonPlacement => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>
    /// Whether to offer the pull-first choice. Only a desktop runs a script or an
    /// application at all — on a phone execution is a link, and a link has no checkout.
    /// </summary>
    public bool ShowExecutePullFirst => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>
    /// Whether to offer the run-unmatched choices. The same answer that decides whether
    /// the offer itself ever appears: it is a keyboard gesture in a launcher, and mobile
    /// has neither a shell for a path nor a machine of its own to lock.
    /// </summary>
    public bool ShowExecuteUnmatched { get; }

    /// <summary>
    /// One bool per choice, because the row is three separate Buttons carrying
    /// <c>Classes.selected</c> rather than one control holding a value. Nothing groups
    /// them, so each has to be told when the pick moves off it — see
    /// <see cref="OnSummonPlacementChanged"/>.
    /// </summary>
    public bool IsPlacementRemembered => SummonPlacement == LauncherPlacement.Remembered;

    /// <inheritdoc cref="IsPlacementRemembered"/>
    public bool IsPlacementCentre => SummonPlacement == LauncherPlacement.Centre;

    /// <inheritdoc cref="IsPlacementRemembered"/>
    public bool IsPlacementPointer => SummonPlacement == LauncherPlacement.Pointer;

    /// <param name="canExecuteUnmatched">
    /// Whether this app offers to run an unmatched search at all. Passed down from the
    /// main view model, so the toggles and the offer can never disagree.
    /// </param>
    /// <param name="executor">
    /// What opens a file or a folder. The main view model's own, so Settings cannot open
    /// anything a marked item could not.
    /// </param>
    /// <param name="open">
    /// The files the running app has open, so a path typed here can be told apart from the
    /// one in force. Null where nothing is known, which is a screen that promises nothing
    /// rather than one that claims a restart is needed.
    /// </param>
    public SettingsViewModel(
        AppSettings settings,
        Action close,
        bool canExecuteUnmatched = false,
        Func<ExecutionPlan, Task<ExecutionResult>>? executor = null,
        OpenFiles? open = null)
    {
        _settings = settings;
        _close = close;
        _executor = executor;
        ShowExecuteUnmatched = canExecuteUnmatched;
        _markdownToHtml = settings.MarkdownToHtml;
        _markdownDoubleSpaced = settings.MarkdownDoubleSpaced;
        _markdownSanitiseLinks = settings.MarkdownSanitiseLinks;
        _closeAfterClipboardCopy = settings.CloseAfterClipboardCopy;
        _closeAfterSnippetCopy = settings.CloseAfterSnippetCopy;
        _summonPlacement = settings.ParsedSummonPlacement;
        _executePullFirst = settings.ExecutePullFirst;
        _executeUnmatched = settings.ExecuteUnmatched;
        _executeVerifyPaths = settings.ExecuteVerifyPaths;
        _executeConfirmSystemActions = settings.ExecuteConfirmSystemActions;

        _dataFolder = settings.DataDirectory;
        RereadDataFolder();

        Snippets = new StorageFileViewModel(
            "SNIPPETS", StorageLocations.FileName,
            "Everything you have saved. Point it into a synced folder to share one set between machines — copy the file there yourself, Klippy moves nothing.",
            read: () => _settings.SnippetsFile,
            write: value => { _settings.SnippetsFile = value; Save(); },
            report: Report,
            executor: executor,
            inUse: () => open?.Snippets);

        History = new StorageFileViewModel(
            "CLIPBOARD HISTORY", StorageLocations.HistoryFileName,
            "What has been copied on this machine, with the clip images in a clips folder beside it.",
            read: () => _settings.HistoryFile,
            write: value => { _settings.HistoryFile = value; Save(); },
            report: Report,
            executor: executor,
            inUse: () => open?.History);

        Commands = new StorageFileViewModel(
            "RECENT COMMANDS", StorageLocations.CommandsFileName,
            "The lines the down arrow recalls in the search box.",
            read: () => _settings.CommandsFile,
            write: value => { _settings.CommandsFile = value; Save(); },
            report: Report,
            executor: executor,
            inUse: () => open?.Commands);

        Variables = new StorageFileViewModel(
            "VARIABLES FILE", KlippyVariables.DefaultFileName,
            "Local defines a snippet expands — %ws% and the like. Re-read as it changes, so no restart.",
            read: () => _settings.VariablesFile,
            write: value => { _settings.VariablesFile = value; Save(); },
            report: Report,
            executor: executor,
            // No inUse: the file is re-read whenever it changes, so there is never a
            // stale one open to warn about.
            describe: DescribeVariables,
            template: VariablesTemplate);

        Files = new[] { Snippets, History, Commands, Variables };

        _loaded = true;
    }

    /// <summary>The one status line the screen has, which every row reports through.</summary>
    private void Report(string? message) => StatusText = message;

    /// <summary>
    /// What Klippy read back from the variables file: the count is how you know a
    /// hand-edit parsed, and "no file yet" is how you know the button beside it is about
    /// to write one.
    ///
    /// Loaded from the path the row resolved rather than through
    /// <see cref="KlippyVariables.Current"/>: those read the app's own settings, and a
    /// screen that read the global while writing to an instance would be showing one file
    /// and editing another the moment they were not the same.
    /// </summary>
    private static string DescribeVariables(string path)
    {
        var variables = KlippyVariables.Load(path);
        return
            !variables.Exists ? "no file yet"
            : variables.Count == 0 ? "no variables in it"
            : variables.Count == 1 ? "1 variable"
            : $"{variables.Count} variables";
    }

    /// <summary>
    /// Works out where the data folder lands and whether Klippy can use it. The folder is
    /// only taken up at startup — a store that changed folder mid-session would have to
    /// decide what to do with the file it already had open — so this reports rather than
    /// acts: nothing is created, and nothing moves.
    /// </summary>
    private void RereadDataFolder()
    {
        var configured = DataFolder?.Trim() ?? "";

        _dataFolderProblem =
            configured.Length > 0 && !StorageLocations.IsUsableDirectory(configured, out var problem)
                ? problem
                : null;

        DataFolderPath = configured.Length == 0
            ? StorageLocations.DefaultDirectory
            : StorageLocations.ExpandPath(configured);

        DataFolderSummary =
            _dataFolderProblem is { } bad
                ? $"{bad} — Klippy stays in {StorageLocations.Directory}"
            : string.Equals(DataFolderPath, StorageLocations.Directory, StringComparison.OrdinalIgnoreCase)
                ? "in use"
                : "takes effect on restart";

        OnPropertyChanged(nameof(DataFolderPath));
        OnPropertyChanged(nameof(DataFolderSummary));
        OnPropertyChanged(nameof(DataFolderStatusText));
    }

    /// <summary>
    /// An empty box means the platform's own app-data folder, which is what Klippy did
    /// before anyone asked for anything else. A folder Klippy cannot use is saved anyway
    /// and said to be unusable: it is what the person typed, and losing it under the
    /// cursor teaches them nothing about why it was wrong.
    /// </summary>
    partial void OnDataFolderChanged(string value)
    {
        if (!_loaded) return;

        _settings.DataDirectory = value?.Trim() ?? "";
        Save();
        RereadDataFolder();
    }

    /// <summary>
    /// Opens the folder a bare file name lands in — the one the box above the files
    /// names, which is not necessarily the one in use until Klippy is restarted.
    /// </summary>
    [RelayCommand]
    private async Task OpenDataFolder()
    {
        if (_executor is not { } run) return;

        var result = await run(new ExecutionPlan { Kind = ExecutionKind.Folder, Target = DataFolderPath });
        StatusText = result.Started ? null : result.Message;
    }

    /// <summary>
    /// What a new variables file says. Entirely comments, so a file created by accident
    /// defines nothing and changes nothing — and the syntax is in front of you at the
    /// moment you have the file open to use it.
    /// </summary>
    private const string VariablesTemplate =
        """
        # Klippy variables — local defines for this machine.
        #
        # One name=value per line. A snippet expands %name% when it is copied, and a
        # snippet marked Execute resolves one in its first word. Names Klippy does not
        # know are left exactly as written, so ordinary percent signs are safe.
        #
        # A value may use other variables, and anything not defined here falls through to
        # this machine's environment:
        #
        #   ws=%localappdata%\Programs\WebStorm\bin\webstorm64.exe
        #   src=D:\src
        #
        # Then a snippet reading "%ws% %src%\myapp" opens that folder in WebStorm.
        # Quote a path that has spaces in it — the quotes are kept.
        #
        # A name typed as an argument stands for its value too — "ws src" against an item
        # reading "%ws% %P%" — and "ws \"src\"" passes the word itself. An item that never
        # wants one says so with %P:exact%, and takes its argument as typed. Define a name
        # twice and an item picks between them with %P:file% or %P:folder%, by which of
        # the values reads as a file:
        #
        #   klippy=D:\main\Klippy
        #   klippy=D:\main\Klippy\Klippy.slnx

        """;

    partial void OnExecutePullFirstChanged(bool value)
    {
        if (!_loaded) return;
        _settings.ExecutePullFirst = value;
        Save();
    }

    partial void OnExecuteUnmatchedChanged(bool value)
    {
        if (!_loaded) return;
        _settings.ExecuteUnmatched = value;
        Save();
    }

    partial void OnExecuteVerifyPathsChanged(bool value)
    {
        if (!_loaded) return;
        _settings.ExecuteVerifyPaths = value;
        Save();
    }

    partial void OnExecuteConfirmSystemActionsChanged(bool value)
    {
        if (!_loaded) return;
        _settings.ExecuteConfirmSystemActions = value;
        Save();
    }

    partial void OnMarkdownSanitiseLinksChanged(bool value)
    {
        if (!_loaded) return;
        _settings.MarkdownSanitiseLinks = value;
        Save();
    }

    partial void OnMarkdownToHtmlChanged(bool value)
    {
        if (!_loaded) return;
        _settings.MarkdownToHtml = value;
        Save();
    }

    partial void OnMarkdownDoubleSpacedChanged(bool value)
    {
        if (!_loaded) return;
        _settings.MarkdownDoubleSpaced = value;
        Save();
    }

    partial void OnCloseAfterClipboardCopyChanged(bool value)
    {
        if (!_loaded) return;
        _settings.CloseAfterClipboardCopy = value;
        Save();
    }

    partial void OnCloseAfterSnippetCopyChanged(bool value)
    {
        if (!_loaded) return;
        _settings.CloseAfterSnippetCopy = value;
        Save();
    }

    partial void OnSummonPlacementChanged(LauncherPlacement value)
    {
        // Ahead of the _loaded guard: the three chips are independent controls, and the
        // one losing the pick has to be told or it keeps its accent. That is a view
        // concern, not a persistence one, so it happens whether or not we save.
        OnPropertyChanged(nameof(IsPlacementRemembered));
        OnPropertyChanged(nameof(IsPlacementCentre));
        OnPropertyChanged(nameof(IsPlacementPointer));

        if (!_loaded) return;
        // ToString on a defined member is exactly the name PlacementPolicy.Parse reads back.
        _settings.SummonPlacement = value.ToString();
        Save();
    }

    [RelayCommand]
    private void PickPlacement(LauncherPlacement placement) => SummonPlacement = placement;

    /// <summary>
    /// A preference that cannot be written still applies for this session — the in-memory
    /// settings have already changed — so a failure is reported rather than reverted.
    /// </summary>
    private void Save()
    {
        try
        {
            _settings.Save();
            StatusText = null;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not save settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Close() => _close();
}

/// <summary>
/// The files the running app actually has open. Every file but the variables one is read
/// at startup and held from then on, so a path typed into Settings is a promise about the
/// next launch — and the only way to say so honestly is to know what is open now.
///
/// Null members for what nothing holds: a clipboard history that is switched off, a
/// command MRU on a platform that has no command line to recall into.
/// </summary>
public sealed record OpenFiles(string? Snippets = null, string? History = null, string? Commands = null);
