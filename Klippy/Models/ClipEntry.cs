using System;
using System.Text.Json.Serialization;

namespace Klippy.Models;

/// <summary>
/// One captured clipboard copy.
///
/// Deliberately not a <see cref="Snippet"/>: it carries no label, tag or quick-code
/// the user chose, it is evicted once the history fills up, and it knows which
/// application it came from. The bridge between the two is promotion — turning a clip
/// worth keeping into a real snippet — not a shared type.
/// </summary>
public sealed class ClipEntry : ISearchable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The plain-text flavour, always present — a clip without text isn't stored.</summary>
    public string Text { get; set; } = "";

    /// <summary>The HTML flavour when the source app offered one, so a paste keeps its formatting.</summary>
    public string? Html { get; set; }

    /// <summary>
    /// Process the copy came from, e.g. "chrome". Recorded at capture time — the
    /// foreground window has usually moved on by the time anything reads this — and
    /// used for display and for auditing what the exclusion list is actually catching.
    /// </summary>
    public string SourceApp { get; set; } = "";

    /// <summary>Pinned clips are exempt from eviction, so the history cap can't lose them.</summary>
    public bool IsPinned { get; set; }

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// First non-blank line, trimmed and clipped — a clip has no name of its own, so the
    /// list needs something to show and the search needs something to weight above the body.
    /// </summary>
    [JsonIgnore]
    public string Label => FirstLine(Text);

    // A clip has no tag or quick-code; explicit implementations keep them off the public
    // surface so nothing is tempted to set one.
    string ISearchable.Content => Text;
    string ISearchable.Tag => "";
    string ISearchable.QuickCode => "";
    DateTimeOffset ISearchable.LastUsedAt => CapturedAt;

    private const int MaxLabelLength = 120;

    private static string FirstLine(string text)
    {
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.IsEmpty) continue;
            return trimmed.Length > MaxLabelLength
                ? string.Concat(trimmed[..MaxLabelLength].ToString(), "…")
                : trimmed.ToString();
        }
        return "";
    }
}
