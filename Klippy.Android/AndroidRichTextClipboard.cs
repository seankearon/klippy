using System.Threading.Tasks;
using Android.Content;
using Klippy.Services;

namespace Klippy.Android;

/// <summary>
/// Puts copies on the clipboard through Android's own API instead of Avalonia's.
///
/// Avalonia's Android backend turns each item into a single ClipData.Item holding one
/// value, and understands only text and string formats — so the HTML flavour Klippy
/// attaches is dropped on the floor. Android's actual mechanism for rich text is
/// ClipData.NewHtmlText, which builds one item carrying the plain text *and* the
/// markup: exactly the dual-flavour model the desktop heads get from CF_HTML and
/// text/html. Gmail, Outlook and Slack all read the HTML side on paste.
/// </summary>
internal sealed class AndroidRichTextClipboard(Context context)
{
    // Shown in the clipboard UI on Android 13+ ("Copied from Klippy").
    private const string Label = "Klippy";

    // Resolved per copy rather than cached, so construction doesn't depend on how far
    // through the activity lifecycle we are when the writer is wired up.
    private ClipboardManager? Manager =>
        context.GetSystemService(Context.ClipboardService) as ClipboardManager;

    public Task WriteAsync(CopyPayload payload)
    {
        if (Manager is not { } manager) return Task.CompletedTask;

        manager.PrimaryClip = payload.Html is { Length: > 0 } html
            ? ClipData.NewHtmlText(Label, payload.Plain, html)
            : ClipData.NewPlainText(Label, payload.Plain);

        return Task.CompletedTask;
    }
}
