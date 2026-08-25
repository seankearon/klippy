using System;
using System.Text;
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
        if (OperatingSystem.IsWindows()) Assert.Equal("HTML Format", name);
        else if (OperatingSystem.IsMacOS()) Assert.Equal("public.html", name);
        else Assert.Equal("text/html", name);
    }
}
