using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Klippy.Services;

/// <summary>
/// The placeholders an item's text may carry, resolved when it is copied or executed:
///
/// <list type="bullet">
/// <item><c>%C%</c> — whatever text is on the clipboard right now.</item>
/// <item><c>%P%</c> — a positional argument typed after the quick-code.</item>
/// <item><c>%P:file%</c> — the same, saying which flavour of a
/// <see cref="KlippyVariables">define</see> it wants when the argument names one.</item>
/// <item><c>%P:exact%</c> — the same, saying the argument names nothing at all: whatever
/// was typed is passed as it was typed.</item>
/// </list>
///
/// With <c>https://www.google.com/search?q=%P%</c> behind the quick-code <c>?</c>,
/// typing <c>? stuff</c> gives <c>https://www.google.com/search?q=stuff</c>.
///
/// The <em>last</em> <c>%P%</c> in a text takes every argument still unused, so one
/// placeholder swallows a whole phrase ("? cats and dogs") instead of just its first
/// word. A placeholder left without an argument expands to nothing — a half-typed
/// invocation must not leave <c>%P%</c> on the clipboard.
///
/// <c>%C%</c> is left exactly as written when no clipboard text is supplied. That is
/// what lets a row preview its own expansion without Klippy reading the clipboard on
/// every keystroke: the arguments are already known, the clipboard is only read when
/// the user actually copies or runs the item.
///
/// Environment variables are not macros and are not resolved here — they belong to the
/// path a line names rather than to the item, and <see cref="EnvironmentProbe"/> is what
/// reads them, on the run path only.
/// </summary>
/// <summary>
/// One argument as it was typed: the word, and whether the quotes around it said to take
/// it as written rather than as the name of a <see cref="KlippyVariables">define</see>.
/// </summary>
public readonly record struct TypedArgument(string Word, bool AsWritten);

public static partial class Macros
{
    /// <summary>Expands to the clipboard's current text.</summary>
    public const string Clipboard = "%C%";

    /// <summary>Expands to a positional argument typed after the quick-code.</summary>
    public const string Positional = "%P%";

    /// <summary>
    /// The qualifier that says a <c>%P%</c> takes its argument exactly as typed:
    /// <c>%P:exact%</c>. The item's half of the escape that quoting is at the prompt.
    ///
    /// A snippet that searches for a word has no use for a lookup at any invocation of
    /// it, and saying so once on the item beats remembering the quotes every time —
    /// <c>? src</c> googles "src" under a <c>%P:exact%</c>, however many paths the
    /// variables file names.
    ///
    /// Not a flavour, and the one qualifier that is a veto rather than a default: where
    /// <c>file</c> and <c>folder</c> choose between the meanings a name has, this says
    /// the word is data and has none to choose between.
    /// </summary>
    public const string Exact = "exact";

    /// <summary>
    /// Whether <paramref name="qualifier"/> is <see cref="Exact"/>. Case-insensitively,
    /// since <c>%P:EXACT%</c> is the same placeholder to the scanner.
    /// </summary>
    public static bool IsExact(string? qualifier) =>
        string.Equals(qualifier, Exact, StringComparison.OrdinalIgnoreCase);

    // Source-generated rather than RegexOptions.Compiled, which NativeAOT cannot honour.
    //
    // A %P% may name the flavour it wants and a %C% may not: choosing between two defines
    // of a name is a question about an argument, and a clipboard value is text that has
    // already been fetched with nothing left to choose. The qualifier carries no colon of
    // its own, so where one ends is never a matter of opinion.
    [GeneratedRegex(@"%(?:c|p(?::(?<qualifier>[^%\s:]+))?)%", RegexOptions.IgnoreCase)]
    private static partial Regex AnyMacro();

    /// <summary>
    /// What each <c>%P%</c> in <paramref name="text"/> asks its argument to be, in the
    /// order they appear: <c>file</c> for a <c>%P:file%</c>, and null for a plain one.
    ///
    /// How an item says which of two defines of a name it means without the person
    /// invoking it having to — an item that opens a folder wants <c>klippy:folder</c> from
    /// <c>klippy</c>, and one that opens a solution wants <c>klippy:file</c>, and neither
    /// is worth typing twice a day. Or how it says it wants no define at all: see
    /// <see cref="Exact"/>.
    /// </summary>
    public static string?[] PositionalQualifiers(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string?>();

        var found = new List<string?>();
        foreach (Match match in AnyMacro().Matches(text))
        {
            if (IsClipboard(match)) continue;
            var qualifier = match.Groups["qualifier"];
            found.Add(qualifier.Success ? qualifier.Value : null);
        }

        return found.ToArray();
    }

    /// <summary>Whether <paramref name="text"/> carries any macro at all.</summary>
    public static bool IsPresent(string? text) =>
        !string.IsNullOrEmpty(text) && AnyMacro().IsMatch(text);

    /// <summary>
    /// Whether <paramref name="text"/> carries <c>%C%</c>, i.e. whether resolving it
    /// needs the clipboard read at all. Every other macro resolves without touching it.
    /// </summary>
    public static bool UsesClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (Match match in AnyMacro().Matches(text))
            if (IsClipboard(match)) return true;
        return false;
    }

    /// <param name="arguments">Positional values for <c>%P%</c>, in the order typed.</param>
    /// <param name="clipboardText">
    /// Text for <c>%C%</c>; null leaves every <c>%C%</c> as written.
    /// </param>
    /// <param name="encode">
    /// Applied to each substituted value, never to the surrounding text — that is how a
    /// URL gets percent-encoded arguments without its own slashes being mangled.
    /// </param>
    public static string Expand(
        string? text,
        IReadOnlyList<string>? arguments = null,
        string? clipboardText = null,
        Func<string, string>? encode = null) =>
        ExpandAll(new[] { text ?? "" }, arguments, clipboardText, encode)[0];

    /// <summary>
    /// Expands a whole command line one token at a time, sharing a single argument
    /// cursor across them. Token by token rather than over the joined line, so a
    /// multi-word argument stays <em>one</em> argument to a script instead of splitting
    /// into several at the space it happens to contain.
    /// </summary>
    public static string[] ExpandAll(
        IReadOnlyList<string> parts,
        IReadOnlyList<string>? arguments = null,
        string? clipboardText = null,
        Func<string, string>? encode = null)
    {
        var args = arguments ?? Array.Empty<string>();
        var result = new string[parts.Count];

        // Which %P% is the last one has to be known before the first is filled, since
        // that is the one that takes everything left over.
        int positionals = 0;
        foreach (var part in parts)
            foreach (Match match in AnyMacro().Matches(part))
                if (!IsClipboard(match)) positionals++;

        int filled = 0; // %P% placeholders seen so far
        int next = 0;   // next unused argument

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var matches = AnyMacro().Matches(part);
            if (matches.Count == 0)
            {
                result[i] = part;
                continue;
            }

            var builder = new StringBuilder(part.Length);
            int pos = 0;

            foreach (Match match in matches)
            {
                builder.Append(part, pos, match.Index - pos);
                pos = match.Index + match.Length;

                if (IsClipboard(match))
                {
                    // No clipboard text supplied: leave the macro visible rather than
                    // silently dropping it.
                    builder.Append(clipboardText is null ? match.Value : Encode(clipboardText, encode));
                    continue;
                }

                filled++;
                string value;
                if (filled == positionals)
                {
                    value = JoinFrom(args, next);
                    next = args.Count;
                }
                else
                {
                    value = next < args.Count ? args[next++] : "";
                }
                builder.Append(Encode(value, encode));
            }

            builder.Append(part, pos, part.Length - pos);
            result[i] = builder.ToString();
        }

        return result;
    }

    /// <summary>
    /// Splits a line into arguments on whitespace, double quotes grouping the words that
    /// belong together: <c>deploy.ps1 "two words" three</c> is three arguments, not four.
    /// </summary>
    public static string[] SplitArguments(string? text)
    {
        var typed = SplitTypedArguments(text);
        var words = new string[typed.Length];
        for (int i = 0; i < typed.Length; i++) words[i] = typed[i].Word;
        return words;
    }

    /// <summary>
    /// The same split, keeping what the quotes said. Only the splitter can still tell:
    /// once it is a word in a list, <c>"app"</c> and <c>app</c> are the same three
    /// letters, and one of them was somebody saying they meant the letters.
    ///
    /// A quote anywhere in an argument settles it. Half a quoted word is still somebody
    /// reaching for the escape, and picking over which half they meant would be a second
    /// rule to learn for no gain.
    /// </summary>
    public static TypedArgument[] SplitTypedArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<TypedArgument>();

        var parts = new List<TypedArgument>();
        var current = new StringBuilder();
        bool quoted = false;     // inside a pair right now
        bool asWritten = false;  // this argument carried a quote
        bool started = false;    // distinguishes "" (an empty argument) from no argument

        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                asWritten = true;
                started = true;
            }
            else if (!quoted && char.IsWhiteSpace(c))
            {
                if (started) parts.Add(new TypedArgument(current.ToString(), asWritten));
                current.Clear();
                started = false;
                asWritten = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started) parts.Add(new TypedArgument(current.ToString(), asWritten));
        return parts.ToArray();
    }

    /// <summary>
    /// The first argument of a line, without splitting the rest of it. Same rules as
    /// <see cref="SplitArguments"/>, but asking "could this run at all?" must not
    /// tokenize a whole snippet to find out.
    /// </summary>
    public static string FirstArgument(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var first = new StringBuilder();
        bool quoted = false;
        bool started = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (!quoted && char.IsWhiteSpace(c))
            {
                if (started) break;
            }
            else
            {
                first.Append(c);
                started = true;
            }
        }

        return first.ToString();
    }

    private static bool IsClipboard(Match match) => match.Value[1] is 'c' or 'C';

    private static string Encode(string value, Func<string, string>? encode) =>
        encode is null || value.Length == 0 ? value : encode(value);

    private static string JoinFrom(IReadOnlyList<string> args, int start)
    {
        if (start >= args.Count) return "";
        if (start == args.Count - 1) return args[start];

        var builder = new StringBuilder();
        for (int i = start; i < args.Count; i++)
        {
            if (i > start) builder.Append(' ');
            builder.Append(args[i]);
        }
        return builder.ToString();
    }
}
