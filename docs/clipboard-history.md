---
icon: lucide/clipboard-list
description: "Text, files and images on Windows — and what is deliberately not recorded."
---

# Clipboard history

Klippy also keeps what you copy. `Ctrl+Alt+J` summons it directly (see
[Global hotkeys](global-hotkeys.md)), or the **History** chip, first in the chip
row, switches the list from saved snippets to captured clips — newest first, searchable with the same
prefix matching, and copied back with the same Enter or click. Whatever is in the search
box comes with you, in both directions, so "that connection string" can be asked of the
snippets and of the clips without being typed twice. A *tag* chip is a narrowing of the
view you are already in rather than a change of view, and still clears the box: a search
drops the tag filter the moment you type, so the two never stand together. A clip carries the app it
came from and its age instead of a tag and a quick-code, and keeps whatever flavours it
was captured with, so pasting one back into a rich-text editor gives what the original
copy would have.

## Text, files and images

A clip is one of three kinds, and each pastes back as what it was:

| Kind | Captured from | Pastes as |
|---|---|---|
| Text | `CF_UNICODETEXT`, plus `HTML Format` when offered | text, with formatting where the target takes it |
| Files | `CF_HDROP` | real files in Explorer; the paths in a text editor |
| Image | `PNG` where offered, otherwise `CF_DIB` | `CF_DIB` for a bitmap, `PNG` otherwise |

File clips store paths, not contents, so copying a 4 GB folder costs a few hundred
bytes — and pasting one later fails the same way Explorer would if the files have since
moved. Image clips store the bytes the source app offered, unconverted: PNG when it is
on the clipboard, otherwise the raw DIB with a BMP file header on the front. That is
why Klippy needs no image codec at all.

Image bytes live in a `clips/` folder beside `clipboard.history.json` rather than inside it,
because the
JSON is rewritten whole on every flush and megabytes of base64 would make each copy cost
the entire history. A blob is deleted with its clip, whether that is a delete, a clear
or an eviction. Images above `HistoryImageLimitMb` (16 by default) are not recorded.

Copying a file or image clip back is done through Win32 rather than Avalonia, whose
clipboard cannot express `CF_HDROP` or `CF_DIB` at all. Text and HTML still go through
Avalonia exactly as before — that path is what the Markdown flavours depend on and it
is left alone. One consequence worth knowing: a PNG-flavoured clip goes back as PNG,
which browsers, Office and chat clients accept, but a few paint-style apps that only
speak DIB will not see it.

Per-clip actions, on row hover: **pin** (exempt from eviction), **save as snippet**
(opens the editor prefilled — the clip stays put), **delete**, and **copy**.
A clip carries no
[Execute marker](running-things.md), so the history
always copies; save a clip as a snippet and mark that if you want to run it. Deleting a
clip asks for no confirmation, unlike deleting a snippet: a clip is transient by nature
and the next copy makes another. The footer's **clear history** empties everything
except pinned clips.

**Windows only, and mobile never.** Capture is `AddClipboardFormatListener` on the same
message-only window the global hotkey uses ([`MessageOnlyWindow`](https://github.com/seankearon/klippy/blob/main/Klippy.Desktop/MessageOnlyWindow.cs)),
so it costs one shared thread. Android has forbidden background clipboard reads since
API 29 and shows a system toast on any foreground read since API 31; iOS forbids them
outright. There is no polite way around either, so mobile stays a snippet manager and
the History chip is simply absent there. macOS is not wired up yet — it needs a
`NSPasteboard.changeCount` poll, since it has no notification API at all.

## What is deliberately not recorded

Password managers mark their clipboard writes so managers like this one look away, and
[`CapturePolicy`](https://github.com/seankearon/klippy/blob/main/Klippy/Services/CapturePolicy.cs) honours them: the
`ExcludeClipboardContentFromMonitorProcessing` format that KeePass, 1Password and
Bitwarden set, the older `Clipboard Viewer Ignore` convention, and
`CanIncludeInClipboardHistory` — which is read *by value*, since an app setting it to 1
is opting in rather than out. Klippy's own copies are skipped too, identified by
comparing the clipboard's owning process to its own; without that, every snippet copied
would be echoed straight back into the history.

The policy is a pure function with no Win32 in it, so the security-critical half of
capture is something the tests pin down rather than something you have to trust.

That said, **the history is a plaintext record of what you copied**, protected by the
same `%APPDATA%` permissions as `snippets.json` and nothing more. If that is not a trade
you want, `"HistorySessionOnly": true` keeps it in memory and never writes it to disk.

## Settings

In `settings.json`, next to the hotkey:

```json
{
  "HistoryEnabled": true,
  "HistoryLimit": 500,
  "HistorySessionOnly": false,
  "HistoryCaptureImages": true,
  "HistoryCaptureFiles": true,
  "HistoryImageLimitMb": 16,
  "HistoryExcludedApps": ["keepass", "some-other-app"]
}
```

`HistoryExcludedApps` matches on process name, so `keepass` catches
`C:\Program Files\KeePass\KeePass.exe`. At the limit the oldest unpinned clips are
dropped — but never a pinned one, and never the clip that just arrived, since a history
full of pinned clips would otherwise swallow every new copy in silence.

Clips live in `clipboard.history.json` in the platform's own app-data folder, beside
`settings.json` — never in the [data folder](settings.md#files) or wherever the snippets
have been pointed, since a record of what this machine copied has no business syncing to
another — written on a two-second timer and on exit rather than per copy: history changes on *every* copy anywhere on the system, and
rewriting the file each time would be the wrong shape entirely. The cost is that a hard
crash loses the last couple of seconds — the right trade for data that is itself
transient, and a real difference from `snippets.json`, which is atomic per mutation.

**Not yet done:** paste-back. Selecting a clip copies it; it does not paste it into the
window you came from, which is the thing that makes Ditto feel fast. That needs
`SetForegroundWindow` plus synthesised Ctrl+V, and is the next piece of work.
