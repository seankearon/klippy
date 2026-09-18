using System;
using System.IO;
using Klippy.Models;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

public class VariablesTests
{
    private static KlippyVariables Vars(string text) => KlippyVariables.Parse(text);

    // ---- parsing ----

    [Fact]
    public void Defines_AreNameEqualsValue()
    {
        var vars = Vars("ws=C:\\tools\\webstorm64.exe\nsrc=D:\\src");

        Assert.Equal("C:\\tools\\webstorm64.exe", vars.Get("ws"));
        Assert.Equal("D:\\src", vars.Get("src"));
        Assert.Equal(2, vars.Count);
    }

    [Fact]
    public void Names_AreCaseInsensitive()
    {
        // %WS% and %ws% are the same key everywhere else a person meets this syntax.
        var vars = Vars("WS=x");
        Assert.Equal("x", vars.Get("ws"));
        Assert.Equal("x", vars.Get("Ws"));
    }

    [Fact]
    public void CommentsBlankLinesAndRubbish_AreSkipped_NotFatal()
    {
        // The file is hand-edited, so one bad line must not cost the rest of it.
        var vars = Vars("""
            # a comment
            ; another one

            this line is not a define
            =novalue
            ws=C:\tools\webstorm64.exe
            """);

        Assert.Equal("C:\\tools\\webstorm64.exe", vars.Get("ws"));
        Assert.Equal(1, vars.Count);
    }

    [Fact]
    public void Value_IsTakenLiterally_QuotesIncluded()
    {
        // A Windows path with spaces needs its quotes to survive into the shell you paste
        // it at, so stripping them would break the one case they are written for.
        var vars = Vars("ws=\"C:\\Program Files\\JetBrains\\bin\\webstorm64.exe\"");
        Assert.Equal("\"C:\\Program Files\\JetBrains\\bin\\webstorm64.exe\"", vars.Get("ws"));
    }

    [Fact]
    public void ValueMayContainEqualsSigns()
    {
        Assert.Equal("a=b=c", Vars("q=a=b=c").Get("q"));
    }

    [Fact]
    public void NamesThatCouldNeverBeReferenced_AreIgnored()
    {
        // %my var% never matches - the scan stops at whitespace - so accepting the define
        // would only produce a variable that silently never fires.
        var vars = Vars("my var=x\nwe%ird=y\nok=z");
        Assert.Equal(1, vars.Count);
        Assert.Equal("z", vars.Get("ok"));
    }

    [Fact]
    public void CrLfLineEndings_ParseTheSame()
    {
        var vars = Vars("ws=one\r\nsrc=two\r\n");
        Assert.Equal("one", vars.Get("ws"));
        Assert.Equal("two", vars.Get("src"));
    }

    // ---- expansion ----

    [Fact]
    public void Expand_ReplacesDefinedNames()
    {
        var vars = Vars("ws=C:\\tools\\webstorm64.exe");
        Assert.Equal("C:\\tools\\webstorm64.exe D:\\src\\shine", vars.Expand("%ws% D:\\src\\shine"));
    }

    [Fact]
    public void Expand_ReplacesEveryOccurrence()
    {
        var vars = Vars("a=1");
        Assert.Equal("1-1-1", vars.Expand("%a%-%a%-%a%"));
    }

    [Fact]
    public void Expand_LeavesUndefinedNamesExactlyAsWritten()
    {
        // The guarantee that makes this safe to ship: a snippet written before Klippy had
        // variables copies byte for byte as it always did.
        var vars = Vars("ws=x");
        Assert.Equal("%nope% and 100% and LIKE '%foo%'", vars.Expand("%nope% and 100% and LIKE '%foo%'"));
    }

    [Fact]
    public void Expand_IgnoresPercentsWithWhitespaceBetweenThem()
    {
        // "off" is defined, and still must not fire: the scan stops at whitespace, so
        // there is no %...% pair in that sentence at all.
        Assert.Equal("50% off, 100% focus", Vars("off=SHOULD NOT APPEAR").Expand("50% off, 100% focus"));
    }

    [Fact]
    public void Expand_StillFindsAVariableAfterALonePercent()
    {
        // Scanning must not stop at the first percent that leads nowhere.
        var vars = Vars("ws=WEBSTORM");
        Assert.Equal("50% off, run WEBSTORM", vars.Expand("50% off, run %ws%"));
    }

    [Fact]
    public void Expand_AClosingPercentCanOpenTheNextPair()
    {
        // Nothing was consumed for the undefined %a%, so its closing percent is still free
        // to open %b%.
        var vars = Vars("b=B");
        Assert.Equal("%aB", vars.Expand("%a%b%"));
    }

    [Fact]
    public void Expand_AConsumedPercentCannotOpenTheNextPair()
    {
        // The mirror of the case above, and the one that bites: %a% is consumed whole, so
        // the text after it is literal rather than a second pair starting behind what has
        // already been written out.
        var vars = Vars("a=A\nb=B");
        Assert.Equal("Ab%", vars.Expand("%a%b%"));
        Assert.Equal("AB", vars.Expand("%a%%b%"));
    }

    [Fact]
    public void Expand_AdjacentAndRepeatedPairsRoundTrip()
    {
        var vars = Vars("a=1\nb=2\nc=3");
        Assert.Equal("123", vars.Expand("%a%%b%%c%"));
        Assert.Equal("x1y2z3", vars.Expand("x%a%y%b%z%c%"));
    }

    [Fact]
    public void Expand_HandlesNullEmptyAndPercentFreeText()
    {
        var vars = Vars("a=1");
        Assert.Equal("", vars.Expand(null));
        Assert.Equal("", vars.Expand(""));
        Assert.Equal("nothing here", vars.Expand("nothing here"));
    }

    [Fact]
    public void Expand_DoubledPercentIsNotAnEscape_AndIsLeftAlone()
    {
        // There is deliberately no escape syntax: %% in an existing snippet (a batch file,
        // a printf) has to keep meaning what it meant.
        Assert.Equal("copy %%1 %%2", Vars("a=1").Expand("copy %%1 %%2"));
    }

    // ---- values referring to other things ----

    [Fact]
    public void AValueMayReferToAnotherVariable()
    {
        var vars = Vars("root=D:\\tools\nws=%root%\\webstorm64.exe");
        Assert.Equal("D:\\tools\\webstorm64.exe", vars.Get("ws"));
    }

    [Fact]
    public void AValueFallsBackToTheEnvironment()
    {
        // The line straight out of the issue: ws=%localappdata%\Programs\...
        var name = $"KLIPPY_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "C:\\Users\\sam\\AppData\\Local");
        try
        {
            var vars = Vars($"ws=%{name}%\\Programs\\WebStorm\\bin\\webstorm64.exe");
            Assert.Equal("C:\\Users\\sam\\AppData\\Local\\Programs\\WebStorm\\bin\\webstorm64.exe", vars.Get("ws"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void SnippetContent_DoesNotFallBackToTheEnvironment()
    {
        // A snippet may have carried %TEMP% since long before Klippy had variables, and it
        // has to keep meaning what it says. Only the variables file opts in.
        var name = $"KLIPPY_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "expanded");
        try
        {
            Assert.Equal($"%{name}%", Vars("ws=x").Expand($"%{name}%"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ASelfReference_IsTheWayToPublishAnEnvironmentVariable()
    {
        // localappdata=%localappdata% is a cycle against the file, so it falls through to
        // the OS - which is the documented way to make %localappdata% work in a snippet.
        var name = $"KLIPPY_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "C:\\Local");
        try
        {
            var vars = Vars($"{name}=%{name}%");
            Assert.Equal("C:\\Local", vars.Get(name));
            Assert.Equal("C:\\Local\\Programs", vars.Expand($"%{name}%\\Programs"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ACycleBetweenTwoVariables_Terminates()
    {
        // Distinctive names: a bare "a" could collide with a real environment variable.
        var vars = Vars("klippycyclea=%klippycycleb%\nklippycycleb=%klippycyclea%");
        Assert.Equal("%klippycyclea%", vars.Get("klippycyclea"));
        Assert.Equal("%klippycycleb%", vars.Get("klippycycleb"));
    }

    // ---- what a copy actually gets ----

    [Fact]
    public void Copy_ExpandsAPlainSnippet()
    {
        var vars = Vars("ws=C:\\tools\\webstorm64.exe");
        var snippet = new Snippet { Label = "Open shine", Content = "%ws% D:\\src\\shine" };

        var payload = RichTextClipboard.BuildPayload(snippet, new AppSettings(), vars);

        Assert.Equal("C:\\tools\\webstorm64.exe D:\\src\\shine", payload.Plain);
        Assert.Null(payload.Html);
    }

    [Fact]
    public void Copy_ExpandsBothFlavoursOfAMarkdownSnippet()
    {
        var vars = Vars("docs=https://docs.example.com");
        var snippet = new Snippet { Label = "Docs", Content = "See [the docs](%docs%/logs).", IsMarkdown = true };

        var payload = RichTextClipboard.BuildPayload(snippet, new AppSettings(), vars);

        Assert.Contains("https://docs.example.com/logs", payload.Plain);
        Assert.Contains("https://docs.example.com/logs", payload.Html);
        Assert.DoesNotContain("%docs%", payload.Html);
    }

    [Fact]
    public void Copy_ExpandsBeforeLinksAreSanitised()
    {
        var vars = Vars("docs=https://docs.example.com");
        var settings = new AppSettings { MarkdownSanitiseLinks = true, MarkdownToHtml = false };
        var snippet = new Snippet { Content = "[docs](%docs%/logs)", IsMarkdown = true };

        Assert.Equal("https://docs.example.com/logs",
            RichTextClipboard.BuildPayload(snippet, settings, vars).Plain);
    }

    [Fact]
    public void Copy_LeavesTheStoredSnippetAlone()
    {
        // The store keeps %ws%, or the snippet would only ever be right on the machine that
        // last saved it - and an export would carry one machine's paths to another.
        var vars = Vars("ws=C:\\tools\\webstorm64.exe");
        var snippet = new Snippet { Content = "%ws% D:\\src" };

        RichTextClipboard.BuildPayload(snippet, new AppSettings(), vars);

        Assert.Equal("%ws% D:\\src", snippet.Content);
    }

    [Fact]
    public void WithNoVariablesAtAll_ContentIsUntouched()
    {
        var snippet = new Snippet { Content = "docker system prune -af --volumes  # 100% sure" };
        var payload = RichTextClipboard.BuildPayload(snippet, new AppSettings(), KlippyVariables.Parse(""));
        Assert.Equal(snippet.Content, payload.Plain);
    }

    // ---- the file on disk ----

    [Fact]
    public void AMissingFile_MeansNoVariables_NotACrash()
    {
        var vars = KlippyVariables.Load(Path.Combine(Path.GetTempPath(), $"klippy-missing-{Guid.NewGuid():N}.vars"));
        Assert.False(vars.Exists);
        Assert.Equal(0, vars.Count);
        Assert.Equal("%ws% x", vars.Expand("%ws% x"));
    }

    [Fact]
    public void LoadReadsTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"klippy-vars-{Guid.NewGuid():N}.vars");
        try
        {
            File.WriteAllText(path, "# local defines\nws=C:\\tools\\webstorm64.exe\n");

            var vars = KlippyVariables.Load(path);
            Assert.True(vars.Exists);
            Assert.Equal(path, vars.FilePath);
            Assert.Equal("C:\\tools\\webstorm64.exe", vars.Get("ws"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
