using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Klippy.Models;
using Klippy.Services;
using Klippy.ViewModels;
using Xunit;

namespace Klippy.Tests;

// Some of these point AppSettings.Current at a variables file of their own, which is
// process-wide while it is in force. Same collection as the other tests that move
// Klippy's files about, so the two cannot be mid-swap at the same moment.
[Collection("storage-locations")]
public class VariablesTests
{
    private static KlippyVariables Vars(string text) => KlippyVariables.Parse(text);

    /// <summary>
    /// Points <see cref="AppSettings.Current"/> at a throwaway variables file holding
    /// <paramref name="text"/>, and puts the suite's own back afterwards. The copy path
    /// reads <see cref="KlippyVariables.Current"/>, so a test that wants variables in force
    /// has to give the app a file rather than an object.
    /// </summary>
    private sealed class VariablesFileScope : IDisposable
    {
        private readonly string _previous = AppSettings.Current.VariablesFile;
        private readonly string _path =
            Path.Combine(Path.GetTempPath(), $"klippy-scope-{Guid.NewGuid():N}.vars");

        public VariablesFileScope(string text)
        {
            File.WriteAllText(_path, text);
            AppSettings.Current.VariablesFile = _path;
        }

        public void Dispose()
        {
            AppSettings.Current.VariablesFile = _previous;
            File.Delete(_path);
        }
    }

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
    public void ANameMayBeGivenMoreThanOnce()
    {
        // The reported file: one name, two meanings, nothing on the left to tell them
        // apart. Both lines are kept.
        var vars = Vars("klippy=D:\\main\\Klippy\\Klippy.slnx\nklippy=D:\\main\\Klippy");

        Assert.Equal(2, vars.Count);                            // lines, not names
        Assert.Equal("D:\\main\\Klippy", vars.Get("klippy"));   // the last answers on its own
        Assert.Equal("D:\\main\\Klippy\\Klippy.slnx", vars.ValueOf("klippy", "file"));
        Assert.Equal("D:\\main\\Klippy", vars.ValueOf("klippy", "folder"));
    }

    [Fact]
    public void WhatCountsAsAFile_IsADotInTheLastSegment()
    {
        var vars = Vars(
            "a=D:\\src\\shine\n" +            // a folder
            "a=D:\\src\\shine\\App.sln\n" +   // a file
            "b=\"D:\\my src\\bin\\\"\n" +     // quoted, trailing separator: still a folder
            "b=\"D:\\my src\\go.ps1\"");

        Assert.Equal("D:\\src\\shine\\App.sln", vars.ValueOf("a", "file"));
        Assert.Equal("D:\\src\\shine", vars.ValueOf("a", "folder"));
        Assert.Equal("\"D:\\my src\\go.ps1\"", vars.ValueOf("b", "file"));
        Assert.Equal("\"D:\\my src\\bin\\\"", vars.ValueOf("b", "folder"));
    }

    [Fact]
    public void WhereTheValueReadsWrong_TheFileCanSayWhichIsWhich()
    {
        // A folder with a dot in its name reads as a file, and Klippy is reading the value
        // rather than the disk, so it gets this one wrong...
        var byShape = Vars("n=D:\\src\\node_modules.bak\nn=D:\\src\\x.sln");
        Assert.Equal("D:\\src\\node_modules.bak", byShape.ValueOf("n", "file"));

        // ...and an explicit define is found first, which is how you settle it.
        var byName = Vars("n:folder=D:\\src\\node_modules.bak\nn:file=D:\\src\\x.sln");
        Assert.Equal("D:\\src\\x.sln", byName.ValueOf("n", "file"));
        Assert.Equal("D:\\src\\node_modules.bak", byName.ValueOf("n", "folder"));
    }

    [Fact]
    public void OnlyFileAndFolderAreReadOffAValue()
    {
        // Every other flavour is a name you write out in full. Until you do, it picks
        // nothing: asked for by an item it falls back to the bare name like any flavour
        // the file cannot answer, and named at the prompt it is taken at its word.
        var vars = Vars("n=D:\\src\\readme.md\nn=D:\\src");

        Assert.Equal("D:\\src", vars.ValueOf("n", "docs"));
        Assert.Null(vars.ValueOf("n:docs"));

        // Written out, it is an ordinary name and needs no reading of anything.
        Assert.Equal("D:\\src\\readme.md", Vars("n:docs=D:\\src\\readme.md").ValueOf("n", "docs"));
        Assert.Equal("D:\\src\\readme.md", Vars("n:docs=D:\\src\\readme.md").ValueOf("n:docs"));
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
        // Not "c": that one belongs to the %C% macro - see below.
        var vars = Vars("a=1\nb=2\nz=3");
        Assert.Equal("123", vars.Expand("%a%%b%%z%"));
        Assert.Equal("x1y2z3", vars.Expand("x%a%y%b%z%z%"));
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
    //
    // Expansion lives in MainViewModel.Copy, beside the macro resolution it has to run
    // before, so these drive the real copy path rather than a helper's own idea of it.

    /// <summary>
    /// A view model over one snippet, with the variables file pointed at
    /// <paramref name="varsText"/> and the clipboard captured instead of written.
    /// </summary>
    private static (MainViewModel Vm, Func<string?> Copied, IDisposable Scope) CopyVm(
        Snippet snippet, string varsText)
    {
        var scope = new VariablesFileScope(varsText);
        var store = new SnippetStore(
            Path.Combine(Path.GetTempPath(), $"klippy-vars-{Guid.NewGuid():N}.json"), seedIfEmpty: false);
        store.Add(snippet);

        string? copied = null;
        var vm = new MainViewModel(store)
        {
            ClipboardWriter = payload => { copied = payload.Plain; return Task.CompletedTask; },
        };
        return (vm, () => copied, scope);
    }

    private static string? CopyFirst(Snippet snippet, string varsText, string? filter = null)
    {
        var (vm, copied, scope) = CopyVm(snippet, varsText);
        using (scope)
        {
            if (filter is not null) vm.FilterText = filter;
            vm.CopyCommand.Execute(vm.Filtered[0]);
            Dispatcher.UIThread.RunJobs();
            return copied();
        }
    }

    [AvaloniaFact]
    public void Copy_ExpandsAPlainSnippet()
    {
        var snippet = new Snippet { Label = "Open shine", Content = "%ws% D:\\src\\shine" };

        Assert.Equal("C:\\tools\\webstorm64.exe D:\\src\\shine",
            CopyFirst(snippet, "ws=C:\\tools\\webstorm64.exe"));
    }

    [AvaloniaFact]
    public void Copy_LeavesTheStoredSnippetAlone()
    {
        // The store keeps %ws%, or the snippet would only ever be right on the machine that
        // last saved it - and an export would carry one machine's paths to another.
        var snippet = new Snippet { Content = "%ws% D:\\src" };

        CopyFirst(snippet, "ws=C:\\tools\\webstorm64.exe");

        Assert.Equal("%ws% D:\\src", snippet.Content);
    }

    [AvaloniaFact]
    public void Copy_ExpandsVariablesBeforeMacrosAreFilled()
    {
        // The order that matters: %ws% is a name the item asked to have resolved, and what
        // arrives through a %P% is already a value. A value is never re-read for names, so
        // this argument keeps its percent signs whether or not it happens to name one.
        var snippet = new Snippet
        {
            Label = "Open in WebStorm",
            Content = "%ws% %P%",
            QuickCode = "ws",
        };

        Assert.Equal("C:\\tools\\webstorm64.exe %notavariable%",
            CopyFirst(snippet, "ws=C:\\tools\\webstorm64.exe", filter: "ws %notavariable%"));
    }

    // ---- a typed argument may name a define ----
    //
    // The one place a piece of data is read for a name, and the asymmetry is the reason:
    // a %C% is what the machine handed over, while this is a word somebody stood at the
    // prompt and typed. The word has to *be* the name, and quoting it takes the escape.

    private const string RiderAndSolution =
        "r=C:\\tools\\rider64.exe\npir=D:\\src\\shine\\Shine.sln";

    private static Snippet OpenInRider() => new()
    {
        Label = "Open in Rider",
        Content = "%r% %P%",
        QuickCode = "r",
        IsExecutable = true,
    };

    [AvaloniaFact]
    public void Copy_AnArgumentThatNamesADefine_CarriesItsValue()
    {
        Assert.Equal("C:\\tools\\rider64.exe D:\\src\\shine\\Shine.sln",
            CopyFirst(OpenInRider(), RiderAndSolution, filter: "r pir"));
    }

    [AvaloniaFact]
    public void Copy_AQuotedArgument_IsTheWordItself()
    {
        Assert.Equal("C:\\tools\\rider64.exe pir",
            CopyFirst(OpenInRider(), RiderAndSolution, filter: "r \"pir\""));
    }

    [AvaloniaFact]
    public void Copy_AnArgumentThatNamesNothing_IsPassedAsTyped()
    {
        Assert.Equal("C:\\tools\\rider64.exe D:\\src\\other\\Other.sln",
            CopyFirst(OpenInRider(), RiderAndSolution, filter: "r D:\\src\\other\\Other.sln"));
    }

    [AvaloniaFact]
    public void TheRow_ShowsWhatTheArgumentResolvedTo()
    {
        // Which is how you can tell the name was found before pressing Enter. The item's
        // own %r% still shows as written - that value is the same every time, and the row
        // is what you would edit.
        var (vm, _, scope) = CopyVm(OpenInRider(), RiderAndSolution);
        using (scope)
        {
            vm.FilterText = "r pir";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("%r% D:\\src\\shine\\Shine.sln", vm.Filtered[0].Content);
        }
    }

    /// <summary>
    /// What running <paramref name="template"/> means when <paramref name="line"/> is
    /// typed at it: the real path from the search box to the process, since the item is
    /// half of what an argument means and a helper that skipped it would prove nothing.
    /// </summary>
    private static ExecutionPlan PlanFor(string template, string line, KlippyVariables vars)
    {
        Assert.True(QuickInvocation.TryParse(line, out var invocation));
        return ExecutionPolicy.Plan(
            template,
            invocation.ValuesFor(template, vars),
            platform: ExecutionPlatform.Windows,
            environment: vars.Ahead(new EnvironmentProbe(_ => null, () => "C:\\Users\\sam")));
    }

    [Fact]
    public void Execute_AnArgumentThatNamesADefine_ReachesTheProcessAsItsValue()
    {
        // End to end over the ask: "%r% %P%" behind the code r, invoked as "r pir".
        var plan = PlanFor("%r% %P%", "r pir", Vars(RiderAndSolution));

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal("C:\\tools\\rider64.exe", plan.Target);
        Assert.Equal(new[] { "D:\\src\\shine\\Shine.sln" }, plan.Arguments);
    }

    [Fact]
    public void Execute_ADefineWorthAWholePathWithSpaces_StaysOneArgument()
    {
        // It resolves after the line has been split, so a value with a space in it needs
        // no quotes - and could not have had them, since quoting is the escape.
        var plan = PlanFor(
            "%r% %P%", "r big", Vars("r=C:\\tools\\rider64.exe\nbig=D:\\my src\\Big.sln"));

        Assert.Equal(new[] { "D:\\my src\\Big.sln" }, plan.Arguments);
    }

    [Fact]
    public void Execute_ABatFileStillRefusesWhatCmdWouldReRead_WhateverNameItArrivedBy()
    {
        // The refusal judges the value that is about to be passed, so a define worth an
        // ampersand is refused on the ampersand rather than waved through on the strength
        // of the short name it was reached by.
        var plan = PlanFor("deploy.bat %P%", "go bad", Vars("bad=a&b"));

        Assert.Equal(ExecutionKind.None, plan.Kind);
        Assert.Contains("cannot be passed", plan.Problem);
    }

    // ---- one name, two flavours ----
    //
    // A name is defined once per flavour, and the item says which of them it wants:
    // opening the folder and opening the solution are two items, and "klippy" is what you
    // type at both of them.

    private const string TwoFlavours =
        "r=C:\\tools\\rider64.exe\n" +
        "code=C:\\tools\\code.exe\n" +
        "klippy:folder=D:\\dev\\klippy\n" +
        "klippy:file=D:\\dev\\klippy\\klippy.slnx";

    [Fact]
    public void Execute_TheItemPicksTheFlavour()
    {
        Assert.Equal(new[] { "D:\\dev\\klippy" },
            PlanFor("%code% %P:folder%", "c klippy", Vars(TwoFlavours)).Arguments);

        Assert.Equal(new[] { "D:\\dev\\klippy\\klippy.slnx" },
            PlanFor("%r% %P:file%", "r klippy", Vars(TwoFlavours)).Arguments);
    }

    [Fact]
    public void Execute_ThePromptMayPickTheFlavourInstead()
    {
        // Typed wins: the item's qualifier is a default, not a veto.
        Assert.Equal(new[] { "D:\\dev\\klippy" },
            PlanFor("%r% %P:file%", "r klippy:folder", Vars(TwoFlavours)).Arguments);

        // And it works where the item asked for nothing at all.
        Assert.Equal(new[] { "D:\\dev\\klippy\\klippy.slnx" },
            PlanFor("%r% %P%", "r klippy:file", Vars(TwoFlavours)).Arguments);
    }

    [Fact]
    public void Execute_TheReportedFileWorksEndToEnd()
    {
        // klippy.vars exactly as it was written - one name twice, values quoted.
        var vars = Vars(
            "r=C:\\tools\\rider64.exe\n" +
            "klippy=\"D:\\main\\Klippy\\Klippy.slnx\"\n" +
            "klippy=\"D:\\main\\Klippy\"");

        Assert.Equal(new[] { "D:\\main\\Klippy\\Klippy.slnx" },
            PlanFor("%r% %P:file%", "r klippy", vars).Arguments);
        Assert.Equal(new[] { "D:\\main\\Klippy" },
            PlanFor("%r% %P:folder%", "r klippy", vars).Arguments);
    }

    // ---- quotes, on the way to a process and on the way to the clipboard ----

    [Fact]
    public void Execute_AQuotedValueLosesItsQuotesOnTheWayToTheProcess()
    {
        // The arguments are passed as arguments, and .NET puts back whatever quoting the
        // OS needs - so a pair that survived from the file would reach the program as part
        // of the name it is looking for.
        var plan = PlanFor("%r% %P%", "r big", Vars(
            "r=\"C:\\Program Files\\JetBrains\\rider64.exe\"\nbig=\"D:\\my src\\Big.sln\""));

        Assert.Equal(ExecutionKind.Application, plan.Kind);
        Assert.Equal("C:\\Program Files\\JetBrains\\rider64.exe", plan.Target);
        Assert.Equal(new[] { "D:\\my src\\Big.sln" }, plan.Arguments);
    }

    [Fact]
    public void Execute_AQuoteAnywhereElseIsStillRefused()
    {
        // Only a pair wrapping the whole word comes off. One in the middle would decide
        // what starts behind the allow-list's back, and is refused as it always was.
        var plan = PlanFor("%bad% x", "r x", Vars("bad=C:\\x\\payload.scr\"y.exe"));

        Assert.Equal(ExecutionKind.None, plan.Kind);
        Assert.Contains("paths carry no quotes", plan.Problem);
    }

    [AvaloniaFact]
    public void Copy_AQuotedValueKeepsItsQuotes()
    {
        // The other half of the same rule: what you paste at a prompt still needs them.
        var snippet = new Snippet { Content = "%r% %P%", QuickCode = "r" };

        Assert.Equal("\"C:\\Program Files\\x.exe\" \"D:\\my src\\Big.sln\"",
            CopyFirst(snippet, "r=\"C:\\Program Files\\x.exe\"\nbig=\"D:\\my src\\Big.sln\"",
                filter: "r big"));
    }

    [Fact]
    public void AFlavouredNameReadsLikeAnyOtherEverywhereElse()
    {
        // Nothing new in the file format: a colon is an ordinary character in a name, so
        // a snippet and a value may both name a flavour directly.
        var vars = Vars(TwoFlavours);

        Assert.Equal("D:\\dev\\klippy\\klippy.slnx", vars.Expand("%klippy:file%"));
        Assert.Equal("C:\\tools\\rider64.exe D:\\dev\\klippy\\klippy.slnx",
            vars.Expand("%r% %klippy:file%"));

        // An undefined flavour is left as written, like any undefined name.
        Assert.Equal("%klippy:docs%", vars.Expand("%klippy:docs%"));
    }

    [AvaloniaFact]
    public void Copy_TheItemsFlavourReachesTheClipboard()
    {
        var snippet = new Snippet { Content = "%r% %P:file%", QuickCode = "r" };

        Assert.Equal("C:\\tools\\rider64.exe D:\\dev\\klippy\\klippy.slnx",
            CopyFirst(snippet, TwoFlavours, filter: "r klippy"));
    }

    [AvaloniaFact]
    public void WithNoVariablesAtAll_ContentIsUntouched()
    {
        var snippet = new Snippet { Content = "docker system prune -af --volumes  # 100% sure" };
        Assert.Equal(snippet.Content, CopyFirst(snippet, ""));
    }

    // ---- both flavours, through the helper the copy path hands its content to ----

    [Fact]
    public void BothFlavoursOfAMarkdownSnippet_CarryTheExpansion()
    {
        var vars = Vars("docs=https://docs.example.com");
        var content = vars.Expand("See [the docs](%docs%/logs).");

        var payload = RichTextClipboard.BuildPayload(content, isMarkdown: true, new AppSettings());

        Assert.Contains("https://docs.example.com/logs", payload.Plain);
        Assert.Contains("https://docs.example.com/logs", payload.Html);
        Assert.DoesNotContain("%docs%", payload.Html);
    }

    [Fact]
    public void ExpansionHappensBeforeLinksAreSanitised()
    {
        var vars = Vars("docs=https://docs.example.com");
        var settings = new AppSettings { MarkdownSanitiseLinks = true, MarkdownToHtml = false };
        var content = vars.Expand("[docs](%docs%/logs)");

        Assert.Equal("https://docs.example.com/logs",
            RichTextClipboard.BuildPayload(content, isMarkdown: true, settings).Plain);
    }

    // ---- macros are not variables ----

    [Fact]
    public void MacrosAreNeverSwallowed_EvenByAFileThatDefinesThem()
    {
        // %C% and %P% belong to the item, and nobody writing one meant a variable named C.
        var vars = Vars("c=CLIPBOARD\np=POSITIONAL\nws=WEBSTORM");
        Assert.Equal("%C% %P% WEBSTORM", vars.Expand("%C% %P% %ws%"));
        Assert.Equal("%c% %p%", vars.Expand("%c% %p%"));
    }

    // ---- the run path ----

    [Fact]
    public void Ahead_PutsTheFileInFrontOfTheMachine()
    {
        var vars = Vars("ws=C:\\tools\\webstorm64.exe");
        var machine = new EnvironmentProbe(name => name == "HOMEDRIVE" ? "C:" : null, () => "/home/sam");

        var probe = vars.Ahead(machine);

        Assert.Equal("C:\\tools\\webstorm64.exe", probe.Expand("%ws%"));  // the file answers
        Assert.Equal("C:", probe.Expand("%HOMEDRIVE%"));                  // the machine still does
        Assert.Equal("%nope%", probe.Expand("%nope%"));                   // neither: left as written
        Assert.Equal("/home/sam/src", probe.Expand("~/src"));             // Home comes through untouched
    }

    [Fact]
    public void Ahead_WinsOverTheMachineOnTheSameName()
    {
        // The file is the local answer, and being local is the point of it.
        var vars = Vars("ws=FROM THE FILE");
        var machine = new EnvironmentProbe(_ => "FROM THE MACHINE", () => "");

        Assert.Equal("FROM THE FILE", vars.Ahead(machine).Expand("%ws%"));
    }

    [Fact]
    public void TheTypedRoute_ReadsTheSameVariables()
    {
        // Two routes to the same launcher must not disagree about what %ws% means.
        var vars = Vars("ws=C:\\tools\\webstorm64.exe");
        var plan = UnmatchedSearch.Plan(
            "%ws%",
            verifyPaths: false,
            platform: ExecutionPlatform.Windows,
            environment: vars.Ahead(new EnvironmentProbe(_ => null, () => "C:\\Users\\sam")));

        Assert.Equal("C:\\tools\\webstorm64.exe", plan.Target);
    }

    [Fact]
    public void Execute_ResolvesAVariableInTheFirstWord()
    {
        // The issue's own example: "%ws% <some folder>" opens that folder in WebStorm.
        var vars = Vars("ws=C:\\tools\\webstorm64.exe");
        var plan = ExecutionPolicy.Plan(
            "%ws% D:\\src\\shine",
            platform: ExecutionPlatform.Windows,
            environment: vars.Ahead(new EnvironmentProbe(_ => null, () => "C:\\Users\\sam")));

        Assert.Equal("C:\\tools\\webstorm64.exe", plan.Target);
        Assert.Equal(new[] { "D:\\src\\shine" }, plan.Arguments);
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
