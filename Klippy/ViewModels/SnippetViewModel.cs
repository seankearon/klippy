using Klippy.Models;

namespace Klippy.ViewModels;

/// <summary>Row presentation of a snippet; one instance per snippet, reused across filtering.</summary>
public partial class SnippetViewModel : RowViewModel
{
    public Snippet Model { get; }

    public SnippetViewModel(Snippet model) => Model = model;

    public override string Label => Model.Label;
    public override string Content => Model.Content;
    public string Tag => Model.Tag;
    public string QuickCode => Model.QuickCode;
    public bool HasQuickCode => Model.QuickCode.Length > 0;
    public bool IsMarkdown => Model.IsMarkdown;
}
