using System;

namespace Klippy.Services;

/// <summary>
/// A search box line read as a command rather than a filter: a quick-code, then the
/// arguments that fill the item's <c>%P%</c> placeholders.
///
/// Typing <c>? stuff</c> asks for the item whose quick-code is <c>?</c>, with "stuff"
/// as its one argument. Only an <em>exact</em> quick-code counts as an invocation — a
/// prefix would turn every ordinary two-word search into an invocation of whatever code
/// the first word happens to start with.
/// </summary>
/// <param name="Code">The first word typed, matched against quick-codes exactly.</param>
/// <param name="Arguments">The rest of the line, split into positional arguments.</param>
public readonly record struct QuickInvocation(string Code, string[] Arguments)
{
    /// <summary>
    /// Reads <paramref name="line"/> as "code argument…". False when there is no
    /// whitespace after a first word, i.e. the user is still typing the code and the
    /// line is an ordinary search. The arguments may be empty — "slf " is a code with
    /// nothing typed after it yet, and its item should stay on screen while it is.
    /// </summary>
    public static bool TryParse(string? line, out QuickInvocation invocation)
    {
        invocation = new QuickInvocation("", Array.Empty<string>());
        if (string.IsNullOrEmpty(line)) return false;

        int space = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (char.IsWhiteSpace(line[i])) { space = i; break; }
        }

        // A leading space is a typo, not an invocation of an empty quick-code.
        if (space <= 0) return false;

        invocation = new QuickInvocation(line[..space], Macros.SplitArguments(line[(space + 1)..]));
        return true;
    }
}
