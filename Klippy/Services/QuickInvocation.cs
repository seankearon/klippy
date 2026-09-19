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
/// passes the word itself, and where a name is defined once per flavour either side can
/// say which is wanted: <c>%P:file%</c> on the item, <c>r pir:file</c> at the prompt.
/// See <see cref="ValuesFor"/>.
/// </summary>
/// <param name="Code">The first word typed, matched against quick-codes exactly.</param>
/// <param name="Arguments">
/// The rest of the line, split into positional arguments, each still as it was typed:
/// what a word names cannot be settled until the item it is for is known, since the item
/// is half of the question.
/// </param>
public readonly record struct QuickInvocation(string Code, TypedArgument[] Arguments)
{
    /// <summary>
    /// Reads <paramref name="line"/> as "code argument…". False when there is no
    /// whitespace after a first word, i.e. the user is still typing the code and the
    /// line is an ordinary search. The arguments may be empty — "slf " is a code with
    /// nothing typed after it yet, and its item should stay on screen while it is.
    /// </summary>
    public static bool TryParse(string? line, out QuickInvocation invocation)
    {
        invocation = new QuickInvocation("", Array.Empty<TypedArgument>());
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
        invocation = new QuickInvocation(line[..space], Macros.SplitTypedArguments(line[(space + 1)..]));
        return true;
    }

    /// <summary>
    /// The values the item actually receives: a word that names a define standing for its
    /// value, and everything else as typed.
    ///
    /// A typed argument is the one piece of data Klippy reads for a name, and the
    /// asymmetry with a <c>%C%</c> is the whole of the reason — a clipboard value is
    /// something the machine handed over, while this is a word somebody stood at the
    /// prompt and typed, and asking for it by name is the only thing they can have meant
    /// by it. The word has to <em>be</em> the name rather than contain one, and quoting it
    /// takes the escape: see <see cref="KlippyVariables.ValueOf"/>.
    /// </summary>
    /// <param name="template">
    /// The item's own text, which says what its placeholders want: a <c>%P:file%</c>
    /// picks the <c>file</c> flavour of whatever name fills it, so the person invoking it
    /// types <c>klippy</c> and not <c>klippy:file</c>. Null asks for no flavour at all.
    /// </param>
    /// <param name="variables">
    /// What a word may name, or null for a line read with none in force — a machine with
    /// no variables file, where every argument is simply the word typed.
    /// </param>
    public string[] ValuesFor(string? template, KlippyVariables? variables)
    {
        var values = new string[Arguments.Length];
        var qualifiers = Macros.PositionalQualifiers(template);

        for (int i = 0; i < Arguments.Length; i++)
        {
            var (word, asWritten) = Arguments[i];

            // The last %P% takes every argument still unused, so everything from it on is
            // asked for on the terms that one asked for.
            var qualifier = qualifiers.Length == 0
                ? null
                : qualifiers[Math.Min(i, qualifiers.Length - 1)];

            values[i] = asWritten || variables is null
                ? word
                : variables.ValueOf(word, qualifier) ?? word;
        }

        return values;
    }
}
