using System;
using System.IO;
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
    /// The variables file, as configured: a bare name sits in the data folder, an absolute
    /// path is taken as given. Editable, because the whole reason to change it is to point
    /// at a file somewhere else — and hunting down settings.json to do that is the sort of
    /// errand this screen exists to spare you.
    /// </summary>
    [ObservableProperty]
    private string _variablesFile;

    /// <summary>Where these preferences are written. Shown so a desktop user can find the file.</summary>
    public string SettingsPath => _settings.SourcePath;

    /// <summary>
    /// Where the snippets, the history and the variables file live — the same folder
    /// unless <c>DataDirectory</c> says otherwise, in which case a user who set it months
    /// ago deserves to be told where their data actually went.
    /// </summary>
    public string DataFolder => StorageLocations.Directory;

    /// <summary>
    /// What <see cref="VariablesFile"/> actually resolves to, which is the thing the
    /// buttons act on and the only form worth showing for a bare name.
    /// </summary>
    public string VariablesPath { get; private set; } = "";

    /// <summary>
    /// What Klippy read back from it: the count is how you know a hand-edit parsed, and
    /// "no file yet" is how you know the button beside it is about to write one.
    /// </summary>
    public string VariablesSummary { get; private set; } = "";

    /// <summary>
    /// The line under the box: the count, with the resolved path in front of it only when
    /// that is not simply what was typed. A bare name is worth resolving on screen; an
    /// absolute one is already the answer, and repeating it reads like a second setting —
    /// including when it was typed with the other separator, which is a respelling of the
    /// answer rather than a new one.
    /// </summary>
    public string VariablesStatusText =>
        string.Equals(
            StorageLocations.NormalizeSeparators(VariablesFile?.Trim() ?? ""),
            VariablesPath,
            StringComparison.Ordinal)
            ? VariablesSummary
            : $"{VariablesPath}  —  {VariablesSummary}";

    /// <summary>Whether that file is there, which decides what the open button offers to do.</summary>
    public bool VariablesFileExists { get; private set; }

    /// <summary>"Open" once the file is there, "Create" while it is not.</summary>
    public string OpenVariablesVerb => VariablesFileExists ? "Open" : "Create";

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
    public SettingsViewModel(
        AppSettings settings,
        Action close,
        bool canExecuteUnmatched = false,
        Func<ExecutionPlan, Task<ExecutionResult>>? executor = null)
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

        _variablesFile = settings.VariablesFile;
        ReadVariables();

        _loaded = true;
    }

    /// <summary>
    /// Re-reads the variables file and refreshes what the screen says about it. Called as
    /// the overlay opens, after an edit to the path, and after the file is created — the
    /// count beside a path is only worth showing while it is current.
    /// </summary>
    private void ReadVariables()
    {
        // The settings being edited, not AppSettings.Current: those are the same instance
        // in the app, and a screen that read the global while writing to an instance would
        // be showing one file and editing another the moment they were not.
        var variables = KlippyVariables.Load(KlippyVariables.PathFor(_settings));

        VariablesPath = variables.FilePath;
        VariablesFileExists = variables.Exists;
        VariablesSummary =
            !variables.Exists ? "no file yet"
            : variables.Count == 0 ? "no variables in it"
            : variables.Count == 1 ? "1 variable"
            : $"{variables.Count} variables";

        OnPropertyChanged(nameof(VariablesPath));
        OnPropertyChanged(nameof(VariablesSummary));
        OnPropertyChanged(nameof(VariablesStatusText));
        OnPropertyChanged(nameof(VariablesFileExists));
        OnPropertyChanged(nameof(OpenVariablesVerb));
    }

    /// <summary>
    /// A blank box means the default name rather than nothing, since there is no such
    /// thing as "no variables file" — a file that is not there simply defines nothing.
    /// Saved as typed, so the box keeps showing what was written rather than rewriting it
    /// under the cursor.
    /// </summary>
    partial void OnVariablesFileChanged(string value)
    {
        if (!_loaded) return;

        _settings.VariablesFile = string.IsNullOrWhiteSpace(value)
            ? KlippyVariables.DefaultFileName
            : value.Trim();

        Save();
        ReadVariables(); // the path moved, so the count beside it is about a different file
    }

    /// <summary>
    /// Opens the variables file in whatever the machine opens a text file with, writing a
    /// commented example first when there is nothing there yet.
    ///
    /// Creating it here rather than at startup: an empty file in everyone's app-data
    /// folder would be a file to wonder about, whereas one written the moment you ask to
    /// see it is the answer to the question you just asked.
    /// </summary>
    [RelayCommand]
    private async Task OpenVariables()
    {
        if (!VariablesFileExists && !TryWriteTemplate()) return;

        await Open(new ExecutionPlan { Kind = ExecutionKind.Document, Target = VariablesPath });
    }

    /// <summary>Opens the folder the variables file is in, existing or not.</summary>
    [RelayCommand]
    private Task OpenVariablesFolder()
    {
        var folder = Path.GetDirectoryName(VariablesPath);
        return string.IsNullOrEmpty(folder)
            ? Task.CompletedTask
            : Open(new ExecutionPlan { Kind = ExecutionKind.Folder, Target = folder });
    }

    /// <summary>Opens the folder holding the snippets, the history and the clip images.</summary>
    [RelayCommand]
    private Task OpenDataFolder() =>
        Open(new ExecutionPlan { Kind = ExecutionKind.Folder, Target = DataFolder });

    private async Task Open(ExecutionPlan plan)
    {
        if (_executor is not { } run) return;

        var result = await run(plan);
        StatusText = result.Started ? null : result.Message;
    }

    /// <summary>
    /// Writes the commented example. Returns whether it worked — a folder that cannot be
    /// written is reported in the same line a failed save is, rather than opening an
    /// editor on a file that is not there.
    /// </summary>
    private bool TryWriteTemplate()
    {
        try
        {
            var folder = Path.GetDirectoryName(VariablesPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            File.WriteAllText(VariablesPath, VariablesTemplate);
            ReadVariables();
            StatusText = null;
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not create {VariablesPath}: {ex.Message}";
            return false;
        }
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
        # Then a snippet reading "%ws% %src%\shine" opens that folder in WebStorm.
        # Quote a path that has spaces in it — the quotes are kept.

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
