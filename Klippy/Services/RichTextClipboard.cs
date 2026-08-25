using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Klippy.Models;
using Markdig;

namespace Klippy.Services;

/// <summary>What a copy puts on the clipboard: always plain text, plus HTML for Markdown snippets.</summary>
public sealed record CopyPayload(string Plain, string? Html);

/// <summary>
/// Converts Markdown snippets to HTML and writes them to the clipboard with two
/// flavours at once — <c>text/plain</c> and the platform's HTML format.
///
/// Rich-text editors (BoldDesk, Outlook, Gmail, Word…) prefer the HTML flavour on
/// paste and render real formatting; plain-text targets fall back to the raw
/// Markdown. That dual-flavour write is what makes formatting survive a paste;
/// converting alone would not.
/// </summary>
public static class RichTextClipboard
{
    // Deliberately conservative. Autolinks turn bare URLs into links (helpdesk replies
    // are full of them) and soft line breaks become <br> so a canned reply keeps the
    // shape it has in the editor instead of collapsing into one paragraph. Notably NOT
    // enabled: SmartyPants — it rewrites -- and quotes, which would corrupt commands.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UseSoftlineBreakAsHardlineBreak()
        .UsePipeTables()
        .Build();

    public static string ToHtml(string markdown) => Markdown.ToHtml(markdown ?? "", Pipeline).Trim();

    public static CopyPayload BuildPayload(Snippet snippet) =>
        snippet.IsMarkdown
            ? new CopyPayload(snippet.Content, ToHtml(snippet.Content))
            : new CopyPayload(snippet.Content, null);

    /// <summary>The OS clipboard format name for HTML, or null where we don't know one.</summary>
    public static string? HtmlFormatName =>
        OperatingSystem.IsWindows() ? "HTML Format"
        : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? "public.html"
        : "text/html"; // X11/Wayland and Android

    /// <summary>
    /// Encodes HTML for the platform clipboard. Windows needs CF_HTML: a header whose
    /// StartHTML/EndHTML/StartFragment/EndFragment are *byte* offsets into the UTF-8
    /// payload, which is why this returns bytes rather than a string.
    /// </summary>
    public static byte[] EncodeHtml(string html) =>
        Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? WrapCfHtml(html) : html);

    public static string WrapCfHtml(string fragment)
    {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string pre = "<html><body>\r\n<!--StartFragment-->";
        const string post = "<!--EndFragment-->\r\n</body></html>";

        int startHtml = string.Format(header, 0, 0, 0, 0).Length; // header is pure ASCII
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(pre);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(post);

        return string.Format(header, startHtml, endHtml, startFragment, endFragment) + pre + fragment + post;
    }

    /// <summary>
    /// Writes the payload to the clipboard. Falls back to plain text if the platform
    /// rejects the HTML flavour, so a copy never silently fails.
    /// </summary>
    public static async Task WriteAsync(IClipboard? clipboard, CopyPayload payload)
    {
        if (clipboard is null) return;

        if (payload.Html is { Length: > 0 } html && HtmlFormatName is { } format)
        {
            try
            {
                var item = new DataTransferItem();
                item.SetText(payload.Plain);
                item.Set(DataFormat.CreateBytesPlatformFormat(format), EncodeHtml(html));

                var transfer = new DataTransfer();
                transfer.Add(item);
                await clipboard.SetDataAsync(transfer);
                return;
            }
            catch (Exception)
            {
                // Platform doesn't accept custom formats — fall through to plain text.
            }
        }

        await clipboard.SetTextAsync(payload.Plain);
    }
}
