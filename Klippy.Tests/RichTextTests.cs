using System;
using System.Text;
using System.Threading.Tasks;
using Klippy.Models;
using Klippy.Services;
using Xunit;

namespace Klippy.Tests;

public class RichTextTests
{
    // ---- markdown -> html ----

    [Fact]
    public void ToHtml_ConvertsCommonFormatting()
    {
        var html = RichTextClipboard.ToHtml("Please send the **log files** as per [these docs](https://example.com/x).");
        Assert.Contains("<strong>log files</strong>", html);
        Assert.Contains("<a href=\"https://example.com/x\">these docs</a>", html);
    }

    [Fact]
    public void ToHtml_AutolinksBareUrls()
    {
        var html = RichTextClipboard.ToHtml("Instructions here:\n\nhttps://www.shineforms.co.uk/docs/XXX");
        Assert.Contains("<a href=\"https://www.shineforms.co.uk/docs/XXX\">", html);
    }

    [Fact]
    public void ToHtml_KeepsSingleLineBreaks()
    {
        // A canned reply must not collapse into one paragraph the way stock Markdown would.
        var html = RichTextClipboard.ToHtml("Hi,\nI'm out of office until Monday.\nContact ops@klippy.app.");
        Assert.Contains("<br", html);
    }

    [Fact]
    public void ToHtml_DoesNotSmartenPunctuation()
    {
        // SmartyPants would turn "--" into an en-dash and wreck shell commands.
        var html = RichTextClipboard.ToHtml("docker system prune -af --volumes");
        Assert.Contains("--volumes", html);
        Assert.DoesNotContain("–", html);
    }

    // ---- blank lines between blocks ----

    [Fact]
    public void ToHtml_SeparatesParagraphsWithASpacerParagraph()
    {
        // Zendesk's composer gives <p> no margin, so consecutive paragraphs would
        // otherwise arrive welded together. An empty paragraph is content, not styling,
        // so it survives sanitising.
        var html = RichTextClipboard.ToHtml("Renew your licence here:\n\nwww.example.com/buy");

        Assert.Equal(
            "<p>Renew your licence here:</p>\n<p>&nbsp;</p>\n<p><a href=\"http://www.example.com/buy\">www.example.com/buy</a></p>",
            html);
    }

    [Fact]
    public void ToHtml_SingleParagraph_HasNoTrailingSpacer()
    {
        var html = RichTextClipboard.ToHtml("Just the one paragraph.");
        Assert.Equal("<p>Just the one paragraph.</p>", html);
    }

    [Fact]
    public void ToHtml_SoftBreaksInsideAParagraphGetNoSpacer()
    {
        // Single newlines are <br> within one block — only blank lines earn a spacer.
        var html = RichTextClipboard.ToHtml("Hi,\nI'm out of office until Monday.");
        Assert.DoesNotContain(Spacer, html);
        Assert.Contains("<br", html);
    }

    [Fact]
    public void ToHtml_SpacesBetweenBlocksOfDifferentKinds()
    {
        var html = RichTextClipboard.ToHtml("## Steps\n\nDo this:\n\n- first\n- second");

        Assert.Contains("<h2>Steps</h2>\n" + Spacer, html);
        Assert.Contains(Spacer + "\n<ul>", html);
        Assert.Equal(2, CountOccurrences(html, Spacer)); // not one per list item
    }

    [Fact]
    public void ToHtml_LinkReferenceDefinition_LeavesNoStraySpacer()
    {
        // The definition renders to nothing, so it must not contribute a spacer.
        var html = RichTextClipboard.ToHtml("[docs]: https://example.com\n\nSee [docs].");

        Assert.StartsWith("<p>", html);
        Assert.DoesNotContain(Spacer, html);
    }

    private const string Spacer = "<p>&nbsp;</p>";

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ---- payload selection ----

    [Fact]
    public void BuildPayload_PlainSnippet_HasNoHtml()
    {
        var snippet = new Snippet { Content = "ssh-ed25519 AAAA_key_data", IsMarkdown = false };
        var payload = RichTextClipboard.BuildPayload(snippet);
        Assert.Equal(snippet.Content, payload.Plain);
        Assert.Null(payload.Html); // never risk mangling keys/commands
    }

    [Fact]
    public void BuildPayload_MarkdownSnippet_CarriesBothFlavours()
    {
        var snippet = new Snippet { Content = "Send the **logs**", IsMarkdown = true };
        var payload = RichTextClipboard.BuildPayload(snippet);
        Assert.Equal("Send the **logs**", payload.Plain); // plain fallback stays raw markdown
        Assert.Contains("<strong>logs</strong>", payload.Html);
    }

    // ---- CF_HTML wire format ----

    [Fact]
    public void WrapCfHtml_OffsetsAreCorrectUtf8ByteOffsets()
    {
        const string fragment = "<p>Grüße — naïve</p>"; // multi-byte, so byte != char offsets
        var wrapped = RichTextClipboard.WrapCfHtml(fragment);
        var bytes = Encoding.UTF8.GetBytes(wrapped);

        int Read(string key)
        {
            int i = wrapped.IndexOf(key + ":", StringComparison.Ordinal) + key.Length + 1;
            return int.Parse(wrapped.Substring(i, 10));
        }

        int startHtml = Read("StartHTML"), endHtml = Read("EndHTML");
        int startFragment = Read("StartFragment"), endFragment = Read("EndFragment");

        Assert.Equal("<html>", Encoding.UTF8.GetString(bytes, startHtml, 6));
        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment));
        Assert.Equal(bytes.Length, endHtml);
        Assert.EndsWith("</html>", Encoding.UTF8.GetString(bytes, 0, endHtml));
    }

    [Fact]
    public void EncodeHtml_IsUtf8_AndWrappedOnlyOnWindows()
    {
        var encoded = RichTextClipboard.EncodeHtml("<p>hi</p>");
        var text = Encoding.UTF8.GetString(encoded);
        if (OperatingSystem.IsWindows())
            Assert.StartsWith("Version:0.9", text);
        else
            Assert.Equal("<p>hi</p>", text);
    }

    [Fact]
    public void HtmlFormatName_MatchesPlatformConvention()
    {
        var name = RichTextClipboard.HtmlFormatName;
        if (OperatingSystem.IsAndroid()) Assert.Null(name);
        else if (OperatingSystem.IsWindows()) Assert.Equal("HTML Format", name);
        else if (OperatingSystem.IsMacOS()) Assert.Equal("public.html", name);
        else Assert.Equal("text/html", name);
    }

    // ---- platform writer hook ----
    //
    // Android's backend silently drops byte[] formats, so the head installs its own
    // writer. These cover the seam; the ClipData call itself needs a device.

    [Fact]
    public async Task WriteAsync_PrefersPlatformWriter_OverAvaloniaClipboard()
    {
        CopyPayload? seen = null;
        using var _ = PlatformWriter(payload => { seen = payload; return Task.CompletedTask; });

        // A null IClipboard is what a head without clipboard support hands us. The
        // platform writer must still run — that's the whole point of the hook.
        await RichTextClipboard.WriteAsync(null, new CopyPayload("Send the **logs**", "<p>Send the <strong>logs</strong></p>"));

        Assert.Equal("Send the **logs**", seen?.Plain);
        Assert.Equal("<p>Send the <strong>logs</strong></p>", seen?.Html);
    }

    [Fact]
    public async Task WriteAsync_PlatformWriter_StillSeesPlainSnippetsWithoutHtml()
    {
        // The Android writer branches on Html being empty, so it has to arrive null.
        CopyPayload? seen = null;
        using var _ = PlatformWriter(payload => { seen = payload; return Task.CompletedTask; });

        await RichTextClipboard.WriteAsync(null, RichTextClipboard.BuildPayload(
            new Snippet { Content = "ssh-ed25519 AAAA_key_data", IsMarkdown = false }));

        Assert.Equal("ssh-ed25519 AAAA_key_data", seen?.Plain);
        Assert.Null(seen?.Html);
    }

    [Fact]
    public async Task WriteAsync_WithoutPlatformWriter_AndNoClipboard_DoesNothing()
    {
        Assert.Null(RichTextClipboard.PlatformWriter); // default on desktop heads
        await RichTextClipboard.WriteAsync(null, new CopyPayload("hi", null));
    }

    /// <summary>Installs a platform writer and removes it when the test ends.</summary>
    private static IDisposable PlatformWriter(Func<CopyPayload, Task> writer)
    {
        RichTextClipboard.PlatformWriter = writer;
        return new Restore(() => RichTextClipboard.PlatformWriter = null);
    }

    private sealed class Restore(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
