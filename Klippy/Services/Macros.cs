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
public static partial class Macros
{
    /// <summary>Expands to the clipboard's current text.</summary>
    public const string Clipboard = "%C%";

    /// <summary>Expands to a positional argument typed after the quick-code.</summary>
    public const string Positional = "%P%";

    // Source-generated rather than RegexOptions.Compiled, which NativeAOT cannot honour.
    [GeneratedRegex("%[cp]%", RegexOptions.IgnoreCase)]
    private static partial Regex AnyMacro();

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
    /// <param name="resolve">
    /// Asked what each <em>unquoted</em> argument stands for; null back from it — or no
    /// resolver at all — means the word itself. That is what lets an argument typed after
    /// a quick-code name a <see cref="KlippyVariables">local define</see>, and quoting it
    /// is how you say you meant the word rather than the name. The quotes cost nothing
    /// to spend that way: a name can hold no whitespace, so it never needed them to stay
    /// one argument.
    ///
    /// Only a caller splitting a <em>typed</em> line passes one. An item's own text is
    /// split with nothing here, because a word written into an item is not somebody
    /// standing at the prompt asking for a name.
    /// </param>
    public static string[] SplitArguments(string? text, Func<string, string?>? resolve = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;  // inside a pair right now
        bool literal = false; // this argument carried a quote, so it is taken as written
        bool started = false; // distinguishes "" (an empty argument) from no argument

        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                literal = true;
                started = true;
            }
            else if (!quoted && char.IsWhiteSpace(c))
            {
                if (started) parts.Add(Stands(current.ToString(), literal, resolve));
                current.Clear();
                started = false;
                literal = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started) parts.Add(Stands(current.ToString(), literal, resolve));
        return parts.ToArray();
    }

    /// <summary>
    /// What one split-out argument stands for. A quote anywhere in it settles the
    /// question before the resolver is asked: half a quoted argument is still the user
    /// reaching for the escape, and picking over which half they meant would be a second
    /// rule to learn for no gain.
    /// </summary>
    private static string Stands(string argument, bool literal, Func<string, string?>? resolve) =>
        literal || resolve is null ? argument : resolve(argument) ?? argument;

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
