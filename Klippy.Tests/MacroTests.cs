using System;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

/// <summary>
/// The placeholders an item's text can carry, and the "code argument…" line that fills
/// them. Pure string work, so it is pinned down here rather than through the UI.
/// </summary>
public class MacroTests
{
    private const string Search = "https://www.google.com/search?q=%P%";

    // ---- %P%: positional arguments ----

    [Fact]
    public void PositionalMacro_TakesTheArgumentTypedAfterTheQuickCode()
    {
        // The example from the feature request: "? stuff" against the snippet behind "?".
        Assert.Equal("https://www.google.com/search?q=stuff",
            Macros.Expand(Search, new[] { "stuff" }));
    }

    [Fact]
    public void TheLastPositional_TakesEveryArgumentStillUnused()
    {
        // One placeholder, three words typed: a search for "cats and dogs" is obviously
        // what was meant, not a search for "cats" with two words thrown away.
        Assert.Equal("https://www.google.com/search?q=cats and dogs",
            Macros.Expand(Search, new[] { "cats", "and", "dogs" }));
    }

    [Fact]
    public void SeveralPositionals_FillLeftToRight()
    {
        Assert.Equal("deploy.ps1 staging fast",
            Macros.Expand("deploy.ps1 %P% %P%", new[] { "staging", "fast" }));

        // …and the last one still sweeps up what is left over.
        Assert.Equal("deploy.ps1 staging fast and loud",
            Macros.Expand("deploy.ps1 %P% %P%", new[] { "staging", "fast", "and", "loud" }));
    }

    [Fact]
    public void PositionalWithoutAnArgument_ExpandsToNothing()
    {
        // Half-typed invocations copy as far as they have got; "%P%" itself must never
        // reach the clipboard.
        Assert.Equal("https://www.google.com/search?q=", Macros.Expand(Search, Array.Empty<string>()));
        Assert.Equal("deploy.ps1 staging ", Macros.Expand("deploy.ps1 %P% %P%", new[] { "staging" }));
    }

    // ---- %C%: the clipboard ----

    [Fact]
    public void ClipboardMacro_TakesTheClipboardText()
    {
        Assert.Equal("Hi Sam, thanks!",
            Macros.Expand("Hi %C%, thanks!", clipboardText: "Sam"));
    }

    [Fact]
    public void ClipboardMacro_IsLeftAsWrittenWhenNoClipboardTextIsSupplied()
    {
        // This is what lets a row show its own expansion without Klippy reading the
        // clipboard on every keystroke.
        Assert.Equal("Hi %C%, thanks!", Macros.Expand("Hi %C%, thanks!"));
    }

    [Fact]
    public void MacrosAreCaseInsensitive()
    {
        Assert.Equal("a-stuff-b", Macros.Expand("a-%p%-b", new[] { "stuff" }));
        Assert.Equal("x", Macros.Expand("%c%", clipboardText: "x"));
    }

    [Fact]
    public void TextWithoutMacros_IsReturnedUntouched()
    {
        const string plain = "docker system prune -af --volumes";
        Assert.Same(plain, Macros.Expand(plain, new[] { "ignored" }));
        Assert.False(Macros.IsPresent(plain));
        Assert.False(Macros.UsesClipboard(Search)); // %P% is not a reason to read the clipboard
        Assert.True(Macros.UsesClipboard("Hi %C%"));
    }

    // ---- encoding ----

    [Fact]
    public void EncodingTouchesTheValues_NotTheTextAroundThem()
    {
        // The URL's own slashes and ? must survive; only what was typed is escaped.
        Assert.Equal("https://www.google.com/search?q=cats%20and%20dogs",
            Macros.Expand(Search, new[] { "cats", "and", "dogs" }, encode: Uri.EscapeDataString));
    }

    // ---- token-by-token expansion ----

    [Fact]
    public void ExpandingTokens_KeepsAMultiWordValueAsOneArgument()
    {
        // Expanding the joined line instead would hand the script three arguments, and
        // "two words" would arrive as two of them.
        var expanded = Macros.ExpandAll(
            new[] { "deploy.ps1", "%P%", "--verbose" }, new[] { "two words" });

        Assert.Equal(new[] { "deploy.ps1", "two words", "--verbose" }, expanded);
    }

    // ---- splitting a typed line ----

    [Fact]
    public void SplitArguments_SplitsOnWhitespace_AndQuotesGroupWords()
    {
        Assert.Equal(new[] { "one", "two" }, Macros.SplitArguments("one   two"));
        Assert.Equal(new[] { "deploy.ps1", "two words", "three" },
            Macros.SplitArguments("deploy.ps1 \"two words\" three"));
        Assert.Equal(new[] { @"C:\Program Files\tools\go.ps1" },
            Macros.SplitArguments("\"C:\\Program Files\\tools\\go.ps1\""));
        Assert.Empty(Macros.SplitArguments("   "));
    }

    [Fact]
    public void FirstArgument_AgreesWithSplitting_WithoutSplittingTheRest()
    {
        // Rows ask "is there anything to run here?" on every keystroke, so this reads the
        // first word alone — and has to answer exactly what a full split would have.
        foreach (var line in new[]
                 {
                     "https://www.google.com/search?q=%P%",
                     "deploy.ps1 --env staging",
                     "\"C:\\Program Files\\tools\\go.ps1\" now",
                     "   leading space",
                     "Please send us the log files\nas per the instructions",
                     "one",
                 })
        {
            Assert.Equal(Macros.SplitArguments(line)[0], Macros.FirstArgument(line));
        }

        Assert.Equal("", Macros.FirstArgument("   "));
        Assert.Equal("", Macros.FirstArgument(null));
    }

    // ---- "code argument…" ----

    [Fact]
    public void Invocation_IsAQuickCodeFollowedByArguments()
    {
        Assert.True(QuickInvocation.TryParse("? stuff", out var invocation));
        Assert.Equal("?", invocation.Code);
        Assert.Equal(new[] { "stuff" }, invocation.Arguments);
    }

    [Fact]
    public void Invocation_NeedsWhitespaceAfterTheCode()
    {
        // Still typing the code: this has to stay an ordinary search, or a quick-code
        // would hijack the list before its arguments exist.
        Assert.False(QuickInvocation.TryParse("slf", out _));
        Assert.False(QuickInvocation.TryParse("", out _));
        Assert.False(QuickInvocation.TryParse(null, out _));

        // A leading space is a typo, not an invocation of an empty code.
        Assert.False(QuickInvocation.TryParse(" slf x", out _));
    }

    [Fact]
    public void Invocation_MayHaveNoArgumentsYet()
    {
        // "slf " is a code with the arguments still to come; its snippet stays on screen.
        Assert.True(QuickInvocation.TryParse("slf ", out var invocation));
        Assert.Equal("slf", invocation.Code);
        Assert.Empty(invocation.Arguments);
    }
}
