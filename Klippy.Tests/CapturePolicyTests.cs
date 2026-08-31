using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

public class CapturePolicyTests
{
    private static readonly CaptureRules Defaults = new();

    private static CaptureContext Copy(
        string? app = null,
        int length = 10,
        string[]? formats = null,
        bool? canIncludeInHistory = null,
        bool isSelfWrite = false) =>
        new()
        {
            SourceApp = app,
            TextLength = length,
            Formats = formats ?? [],
            CanIncludeInClipboardHistory = canIncludeInHistory,
            IsSelfWrite = isSelfWrite,
        };

    [Fact]
    public void OrdinaryTextIsCaptured()
    {
        Assert.Equal(CaptureVerdict.Capture, CapturePolicy.Evaluate(Copy(app: "chrome.exe"), Defaults));
        Assert.True(CapturePolicy.ShouldCapture(Copy(), Defaults));
    }

    // ---- password managers ----

    [Theory]
    [InlineData("ExcludeClipboardContentFromMonitorProcessing")]
    [InlineData("Clipboard Viewer Ignore")]
    public void FormatsThatAskManagersToLookAwayAreHonoured(string format)
    {
        var verdict = CapturePolicy.Evaluate(Copy(formats: [format]), Defaults);

        Assert.Equal(CaptureVerdict.ExcludedFormat, verdict);
    }

    [Fact]
    public void BlockingFormatsMatchRegardlessOfCase()
    {
        var verdict = CapturePolicy.Evaluate(
            Copy(formats: ["excludeclipboardcontentfrommonitorprocessing"]), Defaults);

        Assert.Equal(CaptureVerdict.ExcludedFormat, verdict);
    }

    [Fact]
    public void BlockingFormatIsFoundAlongsideOrdinaryOnes()
    {
        var verdict = CapturePolicy.Evaluate(
            Copy(formats: ["CF_UNICODETEXT", "HTML Format", "Clipboard Viewer Ignore"]), Defaults);

        Assert.Equal(CaptureVerdict.ExcludedFormat, verdict);
    }

    [Fact]
    public void CloudClipboardOptOutIsAboutTheValueNotThePresence()
    {
        // The format carries a DWORD: 0 opts out, 1 opts in. Treating presence alone as a
        // refusal would drop clips from apps that explicitly said yes.
        Assert.Equal(CaptureVerdict.ExcludedFormat,
            CapturePolicy.Evaluate(Copy(canIncludeInHistory: false), Defaults));

        Assert.Equal(CaptureVerdict.Capture,
            CapturePolicy.Evaluate(Copy(canIncludeInHistory: true), Defaults));

        Assert.Equal(CaptureVerdict.Capture,
            CapturePolicy.Evaluate(Copy(canIncludeInHistory: null), Defaults));
    }

    // ---- self-writes ----

    [Fact]
    public void KlippysOwnCopyIsNotRecorded()
    {
        // Otherwise every snippet copied is echoed back into the history by the very
        // notification that copying it raised.
        Assert.Equal(CaptureVerdict.SelfWrite,
            CapturePolicy.Evaluate(Copy(isSelfWrite: true), Defaults));
    }

    [Fact]
    public void SelfWriteIsCheckedBeforeAnythingElse()
    {
        var context = Copy(isSelfWrite: true, length: 0, formats: ["Clipboard Viewer Ignore"]);

        Assert.Equal(CaptureVerdict.SelfWrite, CapturePolicy.Evaluate(context, Defaults));
    }

    // ---- app exclusions ----

    [Fact]
    public void ExcludedAppMatchesOnProcessNameAlone()
    {
        var rules = new CaptureRules { ExcludedApps = ["keepass"] };

        Assert.Equal(CaptureVerdict.ExcludedApp,
            CapturePolicy.Evaluate(Copy(app: @"C:\Program Files\KeePass\KeePass.exe"), rules));
        Assert.Equal(CaptureVerdict.ExcludedApp,
            CapturePolicy.Evaluate(Copy(app: "KeePass.exe"), rules));
        Assert.Equal(CaptureVerdict.ExcludedApp,
            CapturePolicy.Evaluate(Copy(app: "keepass"), rules));
    }

    [Fact]
    public void ExclusionListMayItselfCarryPathsOrExtensions()
    {
        var rules = new CaptureRules { ExcludedApps = [@"C:\apps\1Password.exe", "bitwarden.EXE"] };

        Assert.Equal(CaptureVerdict.ExcludedApp, CapturePolicy.Evaluate(Copy(app: "1password"), rules));
        Assert.Equal(CaptureVerdict.ExcludedApp, CapturePolicy.Evaluate(Copy(app: "Bitwarden.exe"), rules));
    }

    [Fact]
    public void UnlistedAppsAreCaptured()
    {
        var rules = new CaptureRules { ExcludedApps = ["keepass"] };

        Assert.Equal(CaptureVerdict.Capture, CapturePolicy.Evaluate(Copy(app: "keepassxc"), rules));
        Assert.Equal(CaptureVerdict.Capture, CapturePolicy.Evaluate(Copy(app: "chrome.exe"), rules));
    }

    [Fact]
    public void UnknownSourceAppIsNotTreatedAsExcluded()
    {
        var rules = new CaptureRules { ExcludedApps = ["keepass"] };

        Assert.Equal(CaptureVerdict.Capture, CapturePolicy.Evaluate(Copy(app: null), rules));
        Assert.Equal(CaptureVerdict.Capture, CapturePolicy.Evaluate(Copy(app: "   "), rules));
    }

    // ---- size and emptiness ----

    [Fact]
    public void EmptyClipsAreSkipped()
    {
        Assert.Equal(CaptureVerdict.Empty, CapturePolicy.Evaluate(Copy(length: 0), Defaults));
    }

    [Fact]
    public void OversizedClipsAreSkippedAtTheBoundary()
    {
        var rules = new CaptureRules { MaxTextLength = 100 };

        Assert.Equal(CaptureVerdict.Capture, CapturePolicy.Evaluate(Copy(length: 100), rules));
        Assert.Equal(CaptureVerdict.TooLong, CapturePolicy.Evaluate(Copy(length: 101), rules));
    }

    [Fact]
    public void ARefusalReportsTheReasonThatMattersMost()
    {
        // A huge password-manager clip is refused as excluded, not as oversized: the
        // verdict is what gets logged, and "too long" would be a misleading record.
        var rules = new CaptureRules { MaxTextLength = 10 };
        var context = Copy(length: 5_000, formats: ["ExcludeClipboardContentFromMonitorProcessing"]);

        Assert.Equal(CaptureVerdict.ExcludedFormat, CapturePolicy.Evaluate(context, rules));
    }
}
