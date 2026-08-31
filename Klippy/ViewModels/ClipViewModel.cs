using System;
using Klippy.Models;

namespace Klippy.ViewModels;

/// <summary>Row presentation of a captured clip.</summary>
public partial class ClipViewModel : RowViewModel
{
    public ClipEntry Model { get; }

    public ClipViewModel(ClipEntry model) => Model = model;

    public override string Label => Model.Label;
    public override string Content => Model.Text;

    public bool IsPinned => Model.IsPinned;

    /// <summary>Where it was copied from, e.g. "chrome". Empty when the owner could not be identified.</summary>
    public string SourceApp => Model.SourceApp;

    /// <summary>Marks clips that carry formatting, matching the "md" marker on snippet rows.</summary>
    public bool HasHtml => Model.Html is { Length: > 0 };

    /// <summary>
    /// Coarse age, e.g. "now", "5m", "3h", "2d". Rebuilt whenever the list refreshes
    /// rather than ticking on a timer — a clipboard history is read in glances, and a
    /// row that rewrites itself while being looked at is worse than one a minute stale.
    /// </summary>
    public string Age
    {
        get
        {
            var elapsed = DateTimeOffset.UtcNow - Model.CapturedAt;

            if (elapsed < TimeSpan.FromMinutes(1)) return "now";
            if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m";
            if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h";
            return $"{(int)elapsed.TotalDays}d";
        }
    }
}
