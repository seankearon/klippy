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
///
/// An argument may name a <see cref="KlippyVariables">local define</see> rather than
/// spelling a value out: with <c>%r% %P%</c> behind the code <c>r</c>, typing
/// <c>r pir</c> opens the solution <c>pir</c> names. Quoting it — <c>r "pir"</c> —
/// passes the word itself. See <see cref="TryParse"/> for why that line is drawn where
/// it is.
/// </summary>
/// <param name="Code">The first word typed, matched against quick-codes exactly.</param>
/// <param name="Arguments">
/// The rest of the line, split into positional arguments, each already standing for
/// whatever it named.
/// </param>
public readonly record struct QuickInvocation(string Code, string[] Arguments)
{
    /// <summary>
    /// Reads <paramref name="line"/> as "code argument…". False when there is no
    /// whitespace after a first word, i.e. the user is still typing the code and the
    /// line is an ordinary search. The arguments may be empty — "slf " is a code with
    /// nothing typed after it yet, and its item should stay on screen while it is.
    /// </summary>
    /// <param name="variables">
    /// What an argument may name, or null for a line read with none in force. A typed
    /// argument is the one piece of data Klippy does read for a name, and the asymmetry
    /// with a <c>%C%</c> is the whole of the reason: a clipboard value is something the
    /// machine handed over, while this is a word somebody stood at the prompt and typed
    /// — asking for it by name is the only thing they can have meant by it.
    ///
    /// The word has to <em>be</em> the name, not contain one, and quoting it takes the
    /// escape: see <see cref="Macros.SplitArguments"/> and
    /// <see cref="KlippyVariables.ValueOf"/>. A word that names nothing is passed as
    /// typed, so nothing changes for anyone until they define a name that collides with
    /// something they search for — and the quotes are there when it does.
    /// </param>
    public static bool TryParse(string? line, out QuickInvocation invocation, KlippyVariables? variables = null)
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

        // The code itself is matched as typed. It is a quick-code rather than a word with
        // a meaning, and a file that happened to define one would otherwise put the item
        // out of reach of the very line that invokes it.
        invocation = new QuickInvocation(
            line[..space],
            Macros.SplitArguments(line[(space + 1)..], variables is null ? null : variables.ValueOf));
        return true;
    }
}
