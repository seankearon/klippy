using System;
using System.Collections.Generic;

namespace Klippy.Services;

/// <summary>
/// A keyboard shortcut: one <see cref="KeyStroke"/> — <c>F5</c>, <c>Ctrl+3</c> — or a <em>chord</em> of two
/// pressed one after the other, <c>Ctrl+K, Ctrl+S</c>, as Visual Studio and VS Code have them. Two is the
/// limit, as it is there: it is enough to give every action a key without running out of letters, and a
/// third press would be a sequence nobody remembers.
///
/// Written <c>Ctrl+K, Ctrl+S</c> — the strokes joined by a comma — which is the stored form and what
/// <see cref="TryParse"/> reads back, along with the space-separated <c>ctrl+k ctrl+s</c> VS Code writes.
/// </summary>
/// <param name="First">The press that starts it — and, for a single-stroke shortcut, the whole of it.</param>
/// <param name="Second">The press that completes a chord; null for a single stroke.</param>
public readonly record struct Shortcut(KeyStroke First, KeyStroke? Second = null)
{
    /// <summary>True for a two-press chord.</summary>
    public bool IsChord => Second is not null;

    /// <summary>
    /// Whether this can be bound at all: it starts with a stroke that can't be mistaken for typing (see
    /// <see cref="KeyStroke.CanBegin"/>), and a chord's second press is a real key other than <c>Esc</c>.
    /// </summary>
    public bool IsValid => First.CanBegin && (Second is not { } second || second.CanFollow);

    /// <summary>True when <paramref name="prefix"/> is this shortcut, or the first press of this chord.</summary>
    public bool StartsWith(Shortcut prefix) =>
        prefix.IsChord ? this == prefix : First == prefix.First;

    /// <summary>
    /// Whether the two can't both be bound: they are the same keys, or one is where the other starts — with
    /// <c>Ctrl+K</c> bound on its own, <c>Ctrl+K, Ctrl+S</c> could never be reached, because the first press
    /// would already have done something.
    /// </summary>
    public bool CollidesWith(Shortcut other) => StartsWith(other) || other.StartsWith(this);

    /// <summary>The stored form, e.g. <c>Ctrl+K, Ctrl+S</c>.</summary>
    public override string ToString() => Second is { } second ? $"{First}, {second}" : First.ToString();

    /// <summary>The on-screen form: <see cref="ToString"/> with this platform's name for the Meta key.</summary>
    public string DisplayText => Second is { } second ? $"{First.DisplayText}, {second.DisplayText}" : First.DisplayText;

    /// <summary>
    /// Reads a shortcut as <see cref="ToString"/> writes it — or as VS Code does, space-separated, or as people
    /// write it in prose, <c>Ctrl + K, Ctrl + L</c>. A comma right after a '+' is the comma <em>key</em>
    /// (<c>Ctrl+,</c>); any other comma, and any space not beside a '+', is the gap between the strokes. False
    /// for a blank string, a stroke that won't parse, or more than two.
    /// </summary>
    public static bool TryParse(string? text, out Shortcut shortcut)
    {
        shortcut = default;
        var strokes = new List<KeyStroke>(2);
        foreach (var part in SplitStrokes(TightenPluses(text ?? "")))
        {
            if (!KeyStroke.TryParse(part, out var stroke)) return false;
            strokes.Add(stroke);
        }

        switch (strokes.Count)
        {
            case 1: shortcut = new Shortcut(strokes[0]); return true;
            case 2: shortcut = new Shortcut(strokes[0], strokes[1]); return true;
            default: return false;
        }
    }

    /// <summary>Drops the spaces either side of each '+', so <c>Ctrl + K</c> reads as the one stroke it is.</summary>
    private static string TightenPluses(string text)
    {
        var tight = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                var next = i + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                var beforePlus = next < text.Length && text[next] == '+';
                var afterPlus = tight.Length > 0 && tight[^1] == '+';
                if (beforePlus || afterPlus) continue;
            }
            tight.Append(text[i]);
        }
        return tight.ToString();
    }

    private static IEnumerable<string> SplitStrokes(string text)
    {
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var atEnd = i == text.Length;
            var gap = !atEnd && (char.IsWhiteSpace(text[i]) || (text[i] == ',' && (i == 0 || text[i - 1] != '+')));
            if (!atEnd && !gap) continue;

            if (i > start) yield return text[start..i];
            start = i + 1;
        }
    }
}
