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
        Assert.Equal(new[] { new TypedArgument("stuff", false) }, invocation.Arguments);
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

    // ---- splitting keeps what the quotes said ----

    [Fact]
    public void SplitTypedArguments_RemembersWhichWordsWereQuoted()
    {
        Assert.Equal(
            new[] { new TypedArgument("pir", false), new TypedArgument("pir", true) },
            Macros.SplitTypedArguments("pir \"pir\""));

        // A quote anywhere settles it: half a quoted word is still somebody reaching for
        // the escape.
        Assert.Equal(new[] { new TypedArgument("pir", true) }, Macros.SplitTypedArguments("pi\"r\""));

        // And the plain split still agrees about where the words are.
        Assert.Equal(new[] { "deploy.ps1", "two words", "three" },
            Macros.SplitArguments("deploy.ps1 \"two words\" three"));
    }

    // ---- an argument that names something ----

    private const string Defines =
        "pir=D:\\src\\shine\\Shine.sln\n" +
        "klippy=D:\\dev\\klippy\n" +
        "klippy:folder=D:\\dev\\klippy\n" +
        "klippy:file=D:\\dev\\klippy\\klippy.slnx";

    /// <summary>What the item behind the code receives, for a line typed against it.</summary>
    private static string[] Values(string line, string? vars = Defines, string? template = null)
    {
        Assert.True(QuickInvocation.TryParse(line, out var invocation));
        return invocation.ValuesFor(template, vars is null ? null : KlippyVariables.Parse(vars));
    }

    [Fact]
    public void AnArgumentThatNamesADefine_StandsForItsValue()
    {
        // The ask: "%r% %P%" behind the code r, invoked as "r pir".
        Assert.Equal(new[] { @"D:\src\shine\Shine.sln" }, Values("r pir"));

        // %pir% is the same request written the other way.
        Assert.Equal(new[] { @"D:\src\shine\Shine.sln" }, Values("r %pir%"));

        // A word that names nothing is the word, and with no file in force nothing is
        // looked up at all.
        Assert.Equal(new[] { "elsewhere" }, Values("r elsewhere"));
        Assert.Equal(new[] { "pir" }, Values("r pir", vars: null));
    }

    [Fact]
    public void QuotingIsHowYouSayYouMeantTheWord()
    {
        Assert.Equal(new[] { "pir" }, Values("r \"pir\""));

        // Per argument, not per line.
        Assert.Equal(new[] { "pir", @"D:\src\shine\Shine.sln" }, Values("r \"pir\" pir"));
    }

    [Fact]
    public void TheQuickCodeItselfIsNeverResolved()
    {
        // The item behind the code r is reached by typing r, and a file that happens to
        // define r must not put it out of reach of its own invocation.
        Assert.True(QuickInvocation.TryParse("pir x", out var invocation));
        Assert.Equal("pir", invocation.Code);
    }

    [Fact]
    public void AnArgumentIsNotReadForNamesInsideIt()
    {
        // Only a word that *is* a name. One with a name buried in it keeps its percent
        // signs, exactly as it always has - which is what a script reading %TEMP% needs.
        Assert.Equal(new[] { @"%klippy%\src" }, Values(@"r %klippy%\src"));
        Assert.Equal(new[] { "100%klippy%off" }, Values("r 100%klippy%off"));
    }

    [Fact]
    public void MacrosAreNotSwallowed_EvenByAFileThatDefinesThem()
    {
        // %C% and %P% belong to the item; nobody typing one meant a define called C.
        Assert.Equal(new[] { "%C%", "%P%" }, Values("r %C% %P%", vars: "c=CLIPBOARD\np=POSITIONAL"));
    }

    // ---- one name, two flavours ----

    [Fact]
    public void PositionalQualifiers_AreReadOffTheItemInOrder()
    {
        Assert.Equal(new string?[] { null }, Macros.PositionalQualifiers("%r% %P%"));
        Assert.Equal(new string?[] { "file" }, Macros.PositionalQualifiers("%r% %P:file%"));
        Assert.Equal(new string?[] { "folder", null }, Macros.PositionalQualifiers("%x% %P:folder% %P%"));

        // A %C% asks nothing, and neither does text with no placeholders in it.
        Assert.Equal(new string?[] { "file" }, Macros.PositionalQualifiers("%C% %P:file%"));
        Assert.Empty(Macros.PositionalQualifiers("deploy.ps1 --now"));
        Assert.Empty(Macros.PositionalQualifiers(null));
    }

    [Fact]
    public void TheItemSaysWhichFlavourItWants()
    {
        // One name defined once per flavour, and the item picks - so the person invoking
        // it types "klippy" either way.
        Assert.Equal(new[] { @"D:\dev\klippy" }, Values("x klippy", template: "%code% %P:folder%"));
        Assert.Equal(new[] { @"D:\dev\klippy\klippy.slnx" }, Values("x klippy", template: "%r% %P:file%"));

        // With no flavour asked for, the bare name answers.
        Assert.Equal(new[] { @"D:\dev\klippy" }, Values("x klippy", template: "%r% %P%"));
    }

    [Fact]
    public void ThePromptMaySayWhichFlavourItWants_AndOverrulesTheItem()
    {
        Assert.Equal(new[] { @"D:\dev\klippy\klippy.slnx" }, Values("r klippy:file"));

        // What was typed wins: the item's idea of what it wanted is a default, not a veto.
        Assert.Equal(new[] { @"D:\dev\klippy" },
            Values("r klippy:folder", template: "%r% %P:file%"));
    }

    [Fact]
    public void AFlavourFallsBackToTheBareName_ButOnlyWhenTheItemAskedForIt()
    {
        // pir has no flavours of its own, and reads as a file, so both sides find it.
        Assert.Equal(new[] { @"D:\src\shine\Shine.sln" }, Values("r pir", template: "%r% %P:file%"));
        Assert.Equal(new[] { @"D:\src\shine\Shine.sln" }, Values("r pir:file"));

        // A flavour the file cannot answer parts the two sides. Asked for by the item, the
        // bare name still answers - putting a qualifier on an item must not stop it
        // working with the defines that have none.
        Assert.Equal(new[] { @"D:\src\shine\Shine.sln" }, Values("r pir", template: "%r% %P:folder%"));

        // Typed, it is taken at its word, and passed on as typed like any name that
        // answers to nothing.
        Assert.Equal(new[] { "pir:folder" }, Values("r pir:folder"));
        Assert.Equal(new[] { "pir:docs" }, Values("r pir:docs"));
    }

    [Fact]
    public void ANameGivenTwice_IsToldApartByFlavour()
    {
        // The file as it was written: one name, twice, and nothing on the left to say
        // which is which.
        const string twice =
            "klippy=\"D:\\main\\Klippy\\Klippy.slnx\"\n" +
            "klippy=\"D:\\main\\Klippy\"";

        Assert.Equal(new[] { "\"D:\\main\\Klippy\\Klippy.slnx\"" },
            Values("r klippy", twice, "%r% %P:file%"));
        Assert.Equal(new[] { "\"D:\\main\\Klippy\"" },
            Values("r klippy", twice, "%r% %P:folder%"));

        // The prompt may say it instead.
        Assert.Equal(new[] { "\"D:\\main\\Klippy\\Klippy.slnx\"" }, Values("r klippy:file", twice));

        // On its own the name is the last line, as a file read top to bottom ends on it.
        // (The quotes come off at the last stop before the process, not here: a copy still
        // wants them. See ExecutionPolicy.Unquote.)
        Assert.Equal(new[] { "\"D:\\main\\Klippy\"" }, Values("r klippy", twice));
    }

    [Fact]
    public void AWindowsPathIsNotAFlavour()
    {
        // "C:\temp" carries a colon without naming anything, under a %P:file% or not.
        Assert.Equal(new[] { @"C:\temp" }, Values(@"r C:\temp"));
        Assert.Equal(new[] { @"C:\temp" }, Values(@"r C:\temp", template: "%r% %P:file%"));
        Assert.Equal(new[] { "https://example.com" },
            Values("r https://example.com", template: "%r% %P:file%"));
    }

    [Fact]
    public void TheLastPlaceholdersFlavour_CoversTheArgumentsItSwallows()
    {
        // The last %P% takes everything still unused, so everything from it on is asked
        // for on its terms.
        Assert.Equal(new[] { @"D:\dev\klippy", @"D:\dev\klippy\klippy.slnx" },
            Values("x klippy klippy", template: "%code% %P:folder% %P:file%"));
        Assert.Equal(new[] { @"D:\dev\klippy", @"D:\dev\klippy\klippy.slnx", @"D:\dev\klippy\klippy.slnx" },
            Values("x klippy klippy klippy", template: "%code% %P:folder% %P:file%"));
    }

    [Fact]
    public void AQualifiedPlaceholder_FillsLikeAnyOther()
    {
        Assert.Equal("rider64.exe D:\\x.sln", Macros.Expand("rider64.exe %P:file%", new[] { "D:\\x.sln" }));

        // And it is a macro everywhere that asks, so no variables file swallows it.
        Assert.True(Macros.IsPresent("%P:file%"));
        Assert.Equal("%P:file%", KlippyVariables.Parse("p=NOPE").Expand("%P:file%"));

        // A clipboard value has nothing to choose between, so %C:file% is not a macro at
        // all - it is the text it looks like.
        Assert.False(Macros.IsPresent("%C:file%"));
    }
}
