using System;
using System.Collections.Generic;
using Klippy.Models;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>Row presentation of a snippet; one instance per snippet, reused across filtering.</summary>
public partial class SnippetViewModel : RowViewModel
{
    private string[] _arguments = Array.Empty<string>();
    private string? _invokedContent;

    public Snippet Model { get; }

    public SnippetViewModel(Snippet model) => Model = model;

    public override string Label => Model.Label;

    /// <summary>
    /// What the row shows: the snippet as stored, or — once a quick-code has been typed
    /// with arguments after it — what those arguments make of it. Typing "? stuff"
    /// against <c>…/search?q=%P%</c> shows <c>…/search?q=stuff</c>, so the result is
    /// visible before Enter is pressed. The variables file is applied first, as copy and
    /// run apply it, so <c>%r% %P%</c> shows the program it will start rather than half
    /// of what it means. <c>%C%</c> stays as written: previewing it would mean reading
    /// the clipboard on every keystroke.
    /// </summary>
    public override string Content => _invokedContent ?? Model.Content;

    public override string Template => Model.Content;

    public override IReadOnlyList<string> Arguments => _arguments;

    /// <summary>Whether triggering the row runs the snippet instead of copying it.</summary>
    public override bool IsExecutable => Model.IsExecutable;

    /// <summary>
    /// The variables file first, as a copy applies it, so a flavour like
    /// <c>%app:folder%</c> is read the way the file means it and not as a machine name.
    /// </summary>
    protected override bool ReadsAsAPath()
    {
        var variables = KlippyVariables.Current;
        return ExecutionPolicy.NamesAPath(
            variables.Expand(Content), environment: variables.Ahead(EnvironmentProbe.Real));
    }

    public string Tag => Model.Tag;

    /// <summary>
    /// The keys that trigger the snippet, as the row shows them: its in-Klippy shortcut, then its system-wide
    /// hotkey marked as such. Empty when it has neither.
    /// </summary>
    public string KeysHint
    {
        get
        {
            var shortcut = Services.Shortcut.TryParse(Model.Shortcut, out var keys) ? keys.DisplayText : "";
            var hotkey = SnippetHotkeys.TryRead(Model.Hotkey, out var spec) ? $"{spec} anywhere" : "";
            return shortcut.Length > 0 && hotkey.Length > 0 ? $"{shortcut} · {hotkey}" : shortcut + hotkey;
        }
    }

    public bool HasKeys => KeysHint.Length > 0;

    public string QuickCode => Model.QuickCode;
    public bool HasQuickCode => Model.QuickCode.Length > 0;
    public bool IsMarkdown => Model.IsMarkdown;

    /// <summary>
    /// Hands the row the arguments typed after its quick-code, or clears them when the
    /// search box no longer invokes it. Rows are cached across searches, so yesterday's
    /// arguments have to go when the line they came from does.
    /// </summary>
    public void SetArguments(string[] arguments, KlippyVariables? variables = null)
    {
        if (_arguments.Length == 0 && arguments.Length == 0) return;

        _arguments = arguments;
        _invokedContent = arguments.Length == 0
            ? null
            : Macros.Expand(variables?.Expand(Model.Content) ?? Model.Content, arguments);
        NotifyModelChanged(); // Content, Preview, IsMultiline and IsExecutable all move with them
    }
}
