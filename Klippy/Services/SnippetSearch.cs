using System;
using System.Collections.Generic;
using Klippy.Models;

namespace Klippy.Services;

/// <summary>
/// Fast in-memory snippet search.
///
/// Matching rules:
///  - The query is split into whitespace-separated tokens; every token must
///    prefix-match a word in the snippet's label, content or tag
///    ("log fil" finds "…the log files, as per…").
///  - A query with no spaces is additionally matched against each snippet's
///    quick-code; exact and prefix quick-code hits rank above everything else
///    ("slf" jumps straight to the snippet with that code).
///
/// Words are precomputed and lowercased once per snippet (see <see cref="Entry"/>),
/// so a search is a linear scan doing ordinal StartsWith checks — microseconds
/// for thousands of snippets, no allocation beyond the result list.
/// </summary>
public static class SnippetSearch
{
    /// <summary>Precomputed, lowercased word index for one snippet.</summary>
    public sealed class Entry
    {
        public Snippet Snippet { get; }
        public string[] LabelWords { get; }
        public string[] ContentWords { get; }
        public string TagLower { get; }
        public string QuickCodeLower { get; }

        public Entry(Snippet snippet)
        {
            Snippet = snippet;
            LabelWords = Tokenize(snippet.Label);
            ContentWords = Tokenize(snippet.Content);
            TagLower = snippet.Tag.ToLowerInvariant();
            QuickCodeLower = snippet.QuickCode.ToLowerInvariant();
        }
    }

    private const int QuickCodeExactScore = 1_000_000;
    private const int QuickCodePrefixScore = 500_000;

    /// <summary>Splits text into lowercase alphanumeric words ("log-files.txt" → log, files, txt).</summary>
    public static string[] Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

        var words = new List<string>();
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool isWordChar = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (isWordChar && start < 0)
            {
                start = i;
            }
            else if (!isWordChar && start >= 0)
            {
                words.Add(text.Substring(start, i - start).ToLowerInvariant());
                start = -1;
            }
        }
        return words.ToArray();
    }

    /// <summary>
    /// Returns entries matching <paramref name="query"/>, best first.
    /// An empty query returns everything, most recently used first.
    /// </summary>
    public static List<Entry> Search(IReadOnlyList<Entry> entries, string? query)
    {
        var scored = new List<(Entry Entry, int Score)>();
        var tokens = Tokenize(query ?? "");
        // Quick-codes are a single run of characters; only a spaceless query can be one.
        string quickQuery = query is null || query.Contains(' ') ? "" : query.Trim().ToLowerInvariant();

        foreach (var entry in entries)
        {
            int score = Score(entry, tokens, quickQuery);
            if (score >= 0)
                scored.Add((entry, score));
        }

        scored.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            return b.Entry.Snippet.LastUsedAt.CompareTo(a.Entry.Snippet.LastUsedAt);
        });

        var result = new List<Entry>(scored.Count);
        foreach (var (entry, _) in scored)
            result.Add(entry);
        return result;
    }

    private static int Score(Entry entry, string[] tokens, string quickQuery)
    {
        if (tokens.Length == 0) return 0; // empty query: everything matches equally

        if (quickQuery.Length > 0 && entry.QuickCodeLower.Length > 0)
        {
            if (entry.QuickCodeLower == quickQuery) return QuickCodeExactScore;
            if (entry.QuickCodeLower.StartsWith(quickQuery, StringComparison.Ordinal)) return QuickCodePrefixScore;
        }

        int total = 0;
        foreach (var token in tokens)
        {
            int best = 0;

            for (int i = 0; i < entry.LabelWords.Length; i++)
            {
                if (entry.LabelWords[i].StartsWith(token, StringComparison.Ordinal))
                {
                    // First-word label hits rank a touch higher so "doc" prefers "Docker prune".
                    best = Math.Max(best, i == 0 ? 150 : 100);
                    break;
                }
            }

            if (best == 0 && entry.TagLower.StartsWith(token, StringComparison.Ordinal))
                best = 20;

            if (best < 100)
            {
                foreach (var word in entry.ContentWords)
                {
                    if (word.StartsWith(token, StringComparison.Ordinal))
                    {
                        best = Math.Max(best, 10);
                        break;
                    }
                }
            }

            if (best == 0) return -1; // every token must match somewhere
            total += best;
        }
        return total;
    }
}
