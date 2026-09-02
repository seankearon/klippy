using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Klippy.Models;
using Markdig;
using Markdig.Renderers;

namespace Klippy.Services;

/// <summary>What a copy puts on the clipboard: always plain text, plus HTML for Markdown snippets.</summary>
public sealed record CopyPayload(string Plain, string? Html)
{
    /// <summary>Paths, when the clip being copied back is a file selection.</summary>
    public string[]? Files { get; init; }

    /// <summary>Encoded image bytes, when the clip being copied back is a picture.</summary>
    public byte[]? Image { get; init; }

    /// <summary>"PNG" or "BMP" — how <see cref="Image"/> is encoded.</summary>
    public string ImageFormat { get; init; } = "";

    /// <summary>True when this payload needs formats Avalonia's clipboard cannot carry.</summary>
    public bool NeedsNativeWrite => Files is { Length: > 0 } || Image is { Length: > 0 };
}

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

    /// <summary>
    /// A blank line between blocks, expressed as content rather than as a margin.
    ///
    /// Zendesk's composer strips presentational markup and renders &lt;p&gt; with no
    /// margin, so paragraphs arrive welded together — the separation is there in the
    /// markup but invisible. An empty paragraph is just text, so it survives sanitising
    /// and still takes up a line. The cost is that Word and Outlook, which *do* honour
    /// &lt;p&gt; margins, space these more widely than the Markdown source suggests.
    /// </summary>
    private const string Spacer = "<p>&nbsp;</p>";

    /// <param name="doubleSpaced">
    /// False joins blocks with a plain newline instead of the <see cref="Spacer"/>, for
    /// targets that honour &lt;p&gt; margins and would otherwise space them twice over.
    /// </param>
    public static string ToHtml(string markdown, bool doubleSpaced = true)
    {
        var document = Markdown.Parse(markdown ?? "", Pipeline);

        // Rendered block by block rather than by splitting the finished HTML: the
        // top-level blocks are exactly what the source separated with blank lines, so
        // joining them with a spacer puts those blank lines back. Blocks that render to
        // nothing (link reference definitions, say) must not leave a stray spacer.
        var blocks = new List<string>();

        foreach (var block in document)
        {
            using var writer = new StringWriter();
            var renderer = new HtmlRenderer(writer);
            Pipeline.Setup(renderer);
            renderer.Render(block);

            if (writer.ToString().Trim() is { Length: > 0 } html)
                blocks.Add(html);
        }

        return string.Join(doubleSpaced ? "\n" + Spacer + "\n" : "\n", blocks);
    }

    /// <param name="settings">Defaults to <see cref="AppSettings.Current"/>; passed in by tests.</param>
    public static CopyPayload BuildPayload(Snippet snippet, AppSettings? settings = null)
    {
        settings ??= AppSettings.Current;

        // With the HTML flavour switched off a Markdown snippet is just its source, which
        // is exactly what a plain snippet already is — so both take the same path.
        return snippet.IsMarkdown && settings.MarkdownToHtml
            ? new CopyPayload(snippet.Content, ToHtml(snippet.Content, settings.MarkdownDoubleSpaced))
            : new CopyPayload(snippet.Content, null);
    }

    /// <summary>
    /// The OS clipboard format name for HTML, or null where Avalonia cannot carry one.
    ///
    /// Null on Android: its backend only understands text and string formats, so the
    /// byte[] flavour is dropped — but the format name still reaches the ClipData mime
    /// list, leaving a clip that advertises text/html while its item has no HtmlText.
    /// Better to claim nothing and let <see cref="PlatformWriter"/> do the real work.
    /// </summary>
    public static string? HtmlFormatName =>
        OperatingSystem.IsAndroid() ? null
        : OperatingSystem.IsWindows() ? "HTML Format"
        : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() ? "public.html"
        : "text/html"; // X11/Wayland

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
    /// Set by a platform head whose native clipboard API can do more than Avalonia's
    /// backend exposes. Android sets this in MainActivity: only ClipData.NewHtmlText
    /// puts text and HTML on the clipboard as one item, and Avalonia never calls it.
    /// Null everywhere else, where <see cref="WriteAsync"/> handles it directly.
    /// </summary>
    public static Func<CopyPayload, Task>? PlatformWriter { get; set; }

    /// <summary>
    /// Set by a platform head that can put file and image flavours on the clipboard.
    /// Returns false to fall back to the ordinary path — a file clip still pastes as its
    /// paths, which is better than nothing. Null where the platform cannot do it.
    /// </summary>
    public static Func<CopyPayload, Task<bool>>? NativeWriter { get; set; }

    /// <summary>
    /// Writes the payload to the clipboard. Falls back to plain text if the platform
    /// rejects the HTML flavour, so a copy never silently fails.
    /// </summary>
    public static async Task WriteAsync(IClipboard? clipboard, CopyPayload payload)
    {
        // Files and images need CF_HDROP and CF_DIB, which Avalonia's clipboard has no way
        // to express. Only those two kinds take this path — text and HTML keep going
        // through Avalonia exactly as before, since that is what already works.
        if (payload.NeedsNativeWrite && NativeWriter is { } native && await native(payload))
            return;

        if (PlatformWriter is { } platform)
        {
            await platform(payload);
            return;
        }

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
