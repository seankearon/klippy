using System;
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

    /// <summary>Where these preferences are written. Shown so a desktop user can find the file.</summary>
    public string SettingsPath => _settings.SourcePath;

    /// <summary>
    /// Where the snippets, the history and the variables file live — the same folder
    /// unless <c>DataDirectory</c> says otherwise, in which case a user who set it months
    /// ago deserves to be told where their data actually went.
    /// </summary>
    public string DataFolderText => $"{StorageLocations.Directory}  —  snippets, history";

    /// <summary>
    /// The variables file and what Klippy found in it. Both halves matter: the path is
    /// where to create the file, and the count is how you know a hand-edit parsed.
    /// </summary>
    public string VariablesText { get; }

    /// <summary>
    /// Whether to show those paths at all. Mobile app storage is private and unreachable, so
    /// there the lines are noise — which is the whole reason this screen exists.
    /// </summary>
    public bool ShowSettingsPath => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

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
    public SettingsViewModel(AppSettings settings, Action close, bool canExecuteUnmatched = false)
    {
        _settings = settings;
        _close = close;
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

        // Read once, as the overlay opens: Current re-reads the file when it has changed,
        // so reopening Settings after an edit is how you check what Klippy made of it.
        var variables = KlippyVariables.Current;
        VariablesText = $"{variables.FilePath}  —  " + (
            !variables.Exists ? "no file yet"
            : variables.Count == 1 ? "1 variable"
            : $"{variables.Count} variables");

        _loaded = true;
    }

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
