using System;

namespace Klippy.Models;

/// <summary>
/// Anything the search index can rank: a saved <see cref="Snippet"/> or a captured
/// clipboard entry. The index only ever reads these five values, so keeping them
/// behind an interface lets one search implementation serve both stores rather than
/// growing a second, subtly different copy.
/// </summary>
public interface ISearchable
{
    /// <summary>Short name shown in the list and weighted highest when matching.</summary>
    string Label { get; }

    /// <summary>The body text; matches here rank below label matches.</summary>
    string Content { get; }

    /// <summary>Chip-filter tag, or empty where the type has no notion of one.</summary>
    string Tag { get; }

    /// <summary>Short recall code, or empty where the type has no notion of one.</summary>
    string QuickCode { get; }

    /// <summary>Recency, used to break ties between equally-scoring items.</summary>
    DateTimeOffset LastUsedAt { get; }
}
