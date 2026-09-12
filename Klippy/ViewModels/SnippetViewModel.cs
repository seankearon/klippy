using System;
using System.Collections.Generic;
using Klippy.Models;
using Klippy.Services;

namespace Klippy.ViewModels;

/// <summary>Row presentation of a snippet; one instance per snippet, reused across filtering.</summary>
public partial class SnippetViewModel : RowViewModel
{
    private string[] _arguments = Array.Empty<string>();

    public Snippet Model { get; }

    public SnippetViewModel(Snippet model) => Model = model;

    public override string Label => Model.Label;

    /// <summary>
    /// What the row shows: the snippet as stored, or — once a quick-code has been typed
    /// with arguments after it — what those arguments make of it. Typing "? stuff"
    /// against <c>…/search?q=%P%</c> shows <c>…/search?q=stuff</c>, so the result is
    /// visible before Enter is pressed. <c>%C%</c> stays as written: previewing it would
    /// mean reading the clipboard on every keystroke.
    /// </summary>
    public override string Content =>
        _arguments.Length == 0 ? Model.Content : Macros.Expand(Model.Content, _arguments);

    public override string Template => Model.Content;

    public override IReadOnlyList<string> Arguments => _arguments;

    public string Tag => Model.Tag;
    public string QuickCode => Model.QuickCode;
    public bool HasQuickCode => Model.QuickCode.Length > 0;
    public bool IsMarkdown => Model.IsMarkdown;

    /// <summary>
    /// Hands the row the arguments typed after its quick-code, or clears them when the
    /// search box no longer invokes it. Rows are cached across searches, so yesterday's
    /// arguments have to go when the line they came from does.
    /// </summary>
    public void SetArguments(string[] arguments)
    {
        if (_arguments.Length == 0 && arguments.Length == 0) return;

        _arguments = arguments;
        NotifyModelChanged(); // Content, Preview, IsMultiline and IsExecutable all move with them
    }
}
