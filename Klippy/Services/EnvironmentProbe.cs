using System;
using System.Text.RegularExpressions;

namespace Klippy.Services;

/// <summary>
/// The environment the execution rules are allowed to see: one name looked up, and the
/// user's home folder. A record of functions like <see cref="PathProbe"/>, and for the
/// same reason — a rule that reads the machine directly can only be tested on a machine
/// arranged first, and arranging a process-wide variable is something every other test
/// in the run can see.
///
/// It expands the shorthands people write in a path, whether they typed it into the
/// search box or wrote it into an item: <c>%APPDATA%</c> as on Windows,
/// <c>$HOME</c>/<c>${HOME}</c> and a leading <c>~</c> as on macOS and Linux. Both
/// dialects are honoured everywhere, since the cost of the wrong one is a name that does
/// not resolve.
///
/// An unknown name is left exactly as written, so it stays a string that matches nothing
/// rather than quietly becoming a path with a hole in the middle of it.
///
/// Whether a name's spelling matters is the platform's answer, not one given here:
/// Windows looks a variable up without regard to case, so <c>%localappdata%</c> and
/// <c>%LOCALAPPDATA%</c> are the same name there, while macOS and Linux tell them apart.
/// A snippet written in one case and synced to the other resolves on Windows and may not
/// elsewhere, which is the same asymmetry those systems already have.
///
/// <c>%C%</c> and <c>%P%</c> are not touched: they are an item's placeholders, filled
/// from the clipboard and from typed arguments, and nobody writing one meant a variable
/// named C.
/// </summary>
public sealed partial record EnvironmentProbe(Func<string, string?> Value, Func<string> Home)
{
    public static readonly EnvironmentProbe Real = new(
        Environment.GetEnvironmentVariable,
        () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Klippy's own <see cref="KlippyVariables">defines</see> over a whole string, which
    /// is what an <em>argument</em> is resolved with where <see cref="Expand"/> is what
    /// the first word gets. The machine is deliberately not part of it: an argument's
    /// <c>%TEMP%</c> stays the user's own, because a child process inherits the
    /// environment and can read it for itself and the <c>.bat</c> refusal has to go on
    /// seeing what <c>cmd.exe</c> would see. Nothing downstream has ever heard of
    /// <c>klippy.vars</c>, so there is no such reason to leave one of those as written:
    /// it would reach the program as a path with percent signs in the middle of it,
    /// naming nothing.
    ///
    /// The identity on a probe that is only the machine — nothing is defined, so nothing
    /// is replaced. <see cref="KlippyVariables.Ahead"/> is what fills it in, and fills it
    /// in with the very expansion a copy of the same line gets, so one snippet cannot
    /// come to mean two things depending on which key was pressed.
    /// </summary>
    public Func<string, string> ExpandDefines { get; init; } = static text => text;

    public string Expand(string text)
    {
        if (text.Length == 0) return text;

        if (text[0] == '~' && (text.Length == 1 || text[1] == '/' || text[1] == '\\'))
        {
            var home = Home();
            if (home.Length > 0) text = home + text[1..];
        }

        return EnvVar().Replace(text, match =>
            // %C% would otherwise be read as an environment variable named C.
            Macros.IsPresent(match.Value)
                ? match.Value
                : Value(match.Groups["name"].Value) ?? match.Value);
    }

    // Source-generated rather than a runtime Regex, which NativeAOT cannot compile.
    [GeneratedRegex(@"%(?<name>[^%\s]+)%|\$\{(?<name>[^}\s]+)\}|\$(?<name>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex EnvVar();
}
