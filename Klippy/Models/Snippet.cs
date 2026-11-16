using System;
using System.Text.Json.Serialization;

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
    /// Whether running this snippet starts it as administrator — Windows' <em>Run as administrator</em>, with
    /// the consent prompt that goes with it. Only means anything alongside <see cref="IsExecutable"/>, and only
    /// for a script or an application on Windows: anything else marked so is refused when it is run rather
    /// than quietly started without it. Absent in older files, which read as false.
    /// </summary>
    public bool RunAsAdministrator { get; set; }

    /// <summary>
    /// Keys that trigger the snippet while Klippy's window is up — one press such as <c>Ctrl+Shift+L</c>, or a
    /// two-press chord such as <c>Ctrl+K, Ctrl+L</c>, written with a comma between the presses. Triggering
    /// does what Enter on the row does: a copy, or a run for a snippet marked Execute. Empty for none, which is
    /// what older files read as.
    /// </summary>
    public string Shortcut { get; set; } = "";

    /// <summary>
    /// A system-wide hotkey for the snippet, such as <c>Ctrl+Alt+1</c>: pressed in any application, it triggers
    /// the snippet without summoning Klippy. Written as the summon keys are (see <c>HotkeySpec</c>) — a letter,
    /// digit, Space, F1–F20 or punctuation key held with at least one modifier, e.g. <c>Ctrl+Alt+/</c>. Empty
    /// for none, which is what older files read as.
    /// </summary>
    public string Hotkey { get; set; } = "";

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

    /// <summary>
    /// When this version of the snippet was saved, in UTC. Stamped by the store on every add, edit and
    /// import, and what settles which of two versions wins when a sync client has kept both — see
    /// <c>SnippetStore</c>. Absent in older files, which read as the earliest possible time and so lose to
    /// any version that has one; <see cref="ModifiedAtOrCreated"/> is the value to compare.
    /// </summary>
    public DateTimeOffset ModifiedAt { get; set; }

    /// <summary>
    /// The machine that saved this version — <see cref="Environment.MachineName"/>. Only ever read by a
    /// person: it is what says, of the two versions a sync conflict left behind, which one came from where.
    /// Empty in older files.
    /// </summary>
    public string ModifiedBy { get; set; } = "";

    /// <summary>
    /// Last time the snippet was copied or run on <em>this</em> machine; ranks recent items first. Held in
    /// memory and in the export, but never in the snippet's own file in the shared folder: it changes on
    /// every copy, and a file that changes on every copy is one two machines are forever rewriting over each
    /// other. The store keeps it in a file of its own beside settings.json instead.
    /// </summary>
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// <see cref="ModifiedAt"/>, or <see cref="CreatedAt"/> for a snippet from before it was stamped: a
    /// snippet nobody has edited since it was made was last modified when it was made.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset ModifiedAtOrCreated => ModifiedAt == default ? CreatedAt : ModifiedAt;
}
