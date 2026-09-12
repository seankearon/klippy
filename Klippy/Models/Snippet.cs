using System;

namespace Klippy.Models;

/// <summary>A stored piece of text the user can copy to the clipboard.</summary>
public sealed class Snippet : ISearchable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Short human name shown in the list, e.g. "Send log files".</summary>
    public string Label { get; set; } = "";

    /// <summary>The full text placed on the clipboard.</summary>
    public string Content { get; set; } = "";

    /// <summary>Single tag used by the chip filter, e.g. "work".</summary>
    public string Tag { get; set; } = "";

    /// <summary>Optional short code for instant recall, e.g. "slf".</summary>
    public string QuickCode { get; set; } = "";

    /// <summary>
    /// Whether <see cref="Content"/> is Markdown. Markdown snippets are copied with a
    /// rich-text (HTML) clipboard flavour alongside the plain text, so pasting into a
    /// WYSIWYG editor keeps the formatting. Absent in older files, which read as false
    /// (plain) — exactly the safe default for keys, commands and account numbers.
    /// </summary>
    public bool IsMarkdown { get; set; }

    /// <summary>
    /// Whether triggering this snippet runs it instead of copying it: its link opens in
    /// the browser, or the script it names runs. Absent in older files, which read as
    /// false — so every snippet that exists today goes on being copied, and running is
    /// something a snippet has to be marked for.
    /// </summary>
    public bool IsExecutable { get; set; }

    /// <summary>
    /// Where the snippet came from, e.g. "BoldDesk Aug 2026". Empty for snippets
    /// created in Klippy itself.
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// The originating system's identifier for this snippet. Together with
    /// <see cref="Source"/> it lets a re-import update the existing snippet instead of
    /// creating a duplicate, even though the external system knows nothing about
    /// Klippy's <see cref="Id"/>. Kept as a string because external ids are not always
    /// numeric.
    /// </summary>
    public string ExternalId { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last time the snippet was copied; used to rank recent items first.</summary>
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
}
