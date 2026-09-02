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

    [ObservableProperty]
    private string? _statusText;

    /// <summary>Where these preferences are written. Shown so a desktop user can find the file.</summary>
    public string SettingsPath => _settings.SourcePath;

    /// <summary>
    /// Whether to show that path at all. Mobile app storage is private and unreachable, so
    /// there the line is noise — which is the whole reason this screen exists.
    /// </summary>
    public bool ShowSettingsPath => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>
    /// Whether to offer the close-after-copy toggles. Only the desktop launcher has a
    /// window to dismiss; on mobile the app *is* the screen, so the choice would do nothing.
    /// </summary>
    public bool ShowCloseAfterCopy => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    public SettingsViewModel(AppSettings settings, Action close)
    {
        _settings = settings;
        _close = close;
        _markdownToHtml = settings.MarkdownToHtml;
        _markdownDoubleSpaced = settings.MarkdownDoubleSpaced;
        _markdownSanitiseLinks = settings.MarkdownSanitiseLinks;
        _closeAfterClipboardCopy = settings.CloseAfterClipboardCopy;
        _closeAfterSnippetCopy = settings.CloseAfterSnippetCopy;
        _loaded = true;
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
