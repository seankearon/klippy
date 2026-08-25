using System;

namespace Klippy.Models;

/// <summary>A stored piece of text the user can copy to the clipboard.</summary>
public sealed class Snippet
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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last time the snippet was copied; used to rank recent items first.</summary>
    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;
}
