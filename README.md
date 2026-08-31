# Klippy

A fast, cross-platform snippet manager. Store pieces of text, find them instantly, and
put them on the clipboard with one click, tap, or keystroke. Built with .NET 10 and
[Avalonia UI](https://avaloniaui.net/) for Windows, macOS, Android, and iOS.

![Desktop](docs/screenshot-desktop.png)

## Finding snippets

Two ways, both designed for speed:

- **Quick-codes** — a snippet can carry a short code (e.g. `slf` for "Send log files").
  Type the code in the search box and it jumps straight to the top; press Enter to copy.
- **Search** — every space-separated term must prefix-match a word anywhere in the
  label, content, or tag. `log fil` finds "…send us the **log** **fil**es…".
  Matching is case-insensitive; label hits rank above content hits, and recently used
  snippets rank first among ties.

The index is precomputed, lowercased words per snippet
([`SnippetSearch`](Klippy/Services/SnippetSearch.cs)), so a keystroke costs a linear
scan of ordinal `StartsWith` checks — microseconds for thousands of snippets.

**Keyboard (desktop):** type to filter, `↑`/`↓` to navigate, `Enter` to copy,
`Ctrl/⌘+N` new snippet, `Ctrl/⌘+D` duplicate the selected snippet, `Ctrl/⌘+F` focus
search, `Ctrl/⌘+P` toggle the preview pane, `Ctrl/⌘+E` export/import, `Esc`
clears/cancels, `Ctrl/⌘+Enter` saves in the editor. Clicking a row also copies it.

A **preview pane** at the bottom shows the full content of the selected snippet —
useful for long or multi-line entries that the one-line row preview truncates. It is
closed by default and toggles with `Ctrl/⌘+P`, the `preview` footer link, or a click on
its own header. Drag the splitter above it to trade height with the list; the pane
reopens at whatever height you left it. Row-level expansion (the chevron on multi-line
rows) still works independently.

The **edit dialog** is resizable: drag the grip in its bottom-right corner to give the
CONTENT field more room, and it reopens at that size. Clicking the dimmed area beside
it does nothing on purpose — leaving an edit takes `Esc` or Cancel, so a stray click
cannot discard it.

Snippets can be **duplicated** — from a row's hover actions or `Ctrl/⌘+D` on desktop,
or via the Duplicate button in the edit overlay (the route on mobile: swipe → Edit →
Duplicate). A duplicate opens prefilled as a new snippet with " (copy)" appended to the
label; the quick-code is deliberately not copied so codes stay unique.

## Markdown snippets (pasting into rich-text editors)

A snippet can be flagged **Markdown** in the editor (FORMAT → Markdown). Copying one
puts *two* flavours on the clipboard at once:

| Flavour | Goes to | Content |
|---|---|---|
| HTML | Rich-text editors — BoldDesk, Outlook, Gmail, Word | Rendered formatting |
| Plain text | Notepad, terminals, code editors | The raw Markdown |

The receiving app picks the flavour it prefers, so the same copy pastes *formatted*
into a helpdesk reply box and *as plain text* into a terminal. Converting Markdown
alone wouldn't achieve this — it's the dual-flavour clipboard write that makes
formatting survive the paste.

Snippets are plain by default, including every existing one, so keys, IBANs and shell
commands are never run through Markdown (`docker system prune -af --volumes` would
otherwise risk becoming an en-dash). Rows show a small `md` marker when a snippet is
Markdown.

Conversion uses [Markdig](https://github.com/xoofx/markdig) with a deliberately
conservative pipeline: bare URLs become links and single newlines become `<br>` (so a
canned reply keeps its shape instead of collapsing into one paragraph). SmartyPants is
*not* enabled, precisely because it rewrites `--` and quotes.

Per platform the HTML lands on the clipboard as `HTML Format` (Windows CF_HTML, with
correct UTF-8 byte offsets), `public.html` (macOS/iOS) or `text/html` (Linux/Android).
If a platform rejects the HTML flavour, the copy silently falls back to plain text.

> **Note:** on Windows the HTML flavour is served through OLE while Klippy is running.
> Copy, then paste with Klippy still open (its normal launcher lifecycle). If Klippy is
> closed before you paste, the plain-text flavour still works.

## Export / import

Open with the **export / import** footer link (`Ctrl/⌘+E`) on desktop, or the
transfer button in the mobile header.

- **Export** writes snippets to a JSON file — the whole set or a single tag
  (the scope defaults to whichever tag chip is active). The format is identical
  to the store file, so an export doubles as a backup.
- **Import** first previews the picked file (snippet count and the tags it
  contains), then merges everything or just one tag. Merging never wipes data:
  a snippet with a known id replaces the existing copy, everything else is added.

## Global hotkey (desktop)

Klippy runs as a resident launcher: it stays alive behind a tray / menu-bar icon, and a
system-wide hotkey summons it. Press it again — or `Esc`, once the filter and any overlay
are cleared — to dismiss it and return to what you were doing. Closing the window hides
it rather than quitting; use the tray menu's **Quit** to exit for real. Only one instance
runs at a time, so launching Klippy again just tells you it is already resident.

Default is `Ctrl+Alt+K` on Windows and `⌥⌘K` on macOS, stored in `settings.json` next to
your snippets:

```json
{ "Hotkey": "Ctrl+Alt+K", "HotkeyEnabled": true }
```

Modifiers may be written as Ctrl/Control, Alt/Option, Shift and Cmd/Command/Win/Meta, in
any order and any case; at least one modifier is required, since a bare key would swallow
that keystroke system-wide. If another application already owns the combination, Klippy
says so on stderr and starts without a hotkey rather than failing.

Keeping the app resident has a second benefit on Windows: the HTML clipboard flavour is
served through OLE for as long as Klippy runs, so the "paste before closing Klippy" caveat
above effectively disappears.

Implementation is per-platform, since Avalonia has no global-hotkey API:
[`WindowsHotkey`](Klippy.Desktop/WindowsHotkey.cs) uses `RegisterHotKey` with a
message-only window on its own thread, and [`MacHotkey`](Klippy.Desktop/MacHotkey.cs) uses
Carbon's `RegisterEventHotKey` — chosen over an event tap because it needs no Accessibility
permission. **The macOS path compiles but has not been run**, since it cannot be tested
from Windows.

## Clipboard history (Windows)

Klippy also keeps what you copy. The **History** chip, first in the chip row, switches
the list from saved snippets to captured clips — newest first, searchable with the same
prefix matching, and copied back with the same Enter or click. A clip carries the app it
came from and its age instead of a tag and a quick-code, and keeps whatever flavours it
was captured with, so pasting one back into a rich-text editor gives what the original
copy would have.

### Text, files and images

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

Image bytes live in `clips/` beside `history.json` rather than inside it, because the
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
(opens the editor prefilled — the clip stays put), **delete**, and **copy**. Deleting a
clip asks for no confirmation, unlike deleting a snippet: a clip is transient by nature
and the next copy makes another. The footer's **clear history** empties everything
except pinned clips.

**Windows only, and mobile never.** Capture is `AddClipboardFormatListener` on the same
message-only window the global hotkey uses ([`MessageOnlyWindow`](Klippy.Desktop/MessageOnlyWindow.cs)),
so it costs one shared thread. Android has forbidden background clipboard reads since
API 29 and shows a system toast on any foreground read since API 31; iOS forbids them
outright. There is no polite way around either, so mobile stays a snippet manager and
the History chip is simply absent there. macOS is not wired up yet — it needs a
`NSPasteboard.changeCount` poll, since it has no notification API at all.

### What is deliberately not recorded

Password managers mark their clipboard writes so managers like this one look away, and
[`CapturePolicy`](Klippy/Services/CapturePolicy.cs) honours them: the
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

### Settings

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

Clips live in `history.json` beside the snippets, written on a two-second timer and on
exit rather than per copy: history changes on *every* copy anywhere on the system, and
rewriting the file each time would be the wrong shape entirely. The cost is that a hard
crash loses the last couple of seconds — the right trade for data that is itself
transient, and a real difference from `snippets.json`, which is atomic per mutation.

**Not yet done:** paste-back. Selecting a clip copies it; it does not paste it into the
window you came from, which is the thing that makes Ditto feel fast. That needs
`SetForegroundWindow` plus synthesised Ctrl+V, and is the next piece of work.

## Provenance and bulk import

Every snippet carries an optional `Source` and `ExternalId` (e.g. `BoldDesk Aug 2026`
and `13`). Together they give a snippet an identity in the system it came from, so a
later re-import of the same external export **updates snippets in place instead of
duplicating them** — the external system knows nothing about Klippy's own `Guid`.
Klippy's id is preserved on such a match, so it stays stable across re-imports.
Snippets created in Klippy leave both fields empty and continue to match on id alone.

`tools/` holds the BoldDesk pipeline:

```sh
# 1. markdown files + spreadsheet of titles -> a Klippy import file
python tools/bolddesk_to_klippy.py "<export folder>" out.json --source "BoldDesk Aug 2026" --tag support

# 2. merge it into the local store (same Merge path the in-app importer uses)
dotnet run --project tools/Importer -- out.json
```

Re-running both steps is safe: the second pass reports every snippet as *updated*
rather than adding duplicates. Pass the same `--tag` each time — a merge replaces the
whole snippet, so omitting it would blank the tag on snippets that already carry one.

## Where data lives, and backup

Snippets are one JSON file in the platform app-data folder — `%APPDATA%\Klippy\snippets.json`
on Windows, `~/.config/Klippy/` on macOS/Linux, and `files/.config/Klippy/` inside the app
sandbox on Android (mode `0600`, app-private). The whole list is held in memory and each
mutation rewrites the file atomically (temp file + replace), so a crash can't corrupt it.
A copy also writes, because `LastUsedAt` drives recency ranking.

**Android backup is on by default.** `allowBackup="true"` is now set explicitly rather
than relied on as an implicit default, and both rule files
([`backup_rules.xml`](Klippy.Android/Resources/xml/backup_rules.xml) for API ≤30,
[`data_extraction_rules.xml`](Klippy.Android/Resources/xml/data_extraction_rules.xml) for
API 31+) restrict backup to `.config/Klippy/` alone. That include-list matters: debug
fast-deploy drops ~85 MB of Avalonia assemblies into `files/.__override__`, which would
otherwise be swept into the 25 MB Auto Backup quota. A verified backup run transfers
about 7 KB.

**Optional "wipe on uninstall".** Uninstalling always deletes app-private storage, so
what really decides whether snippets come back is the *backup*. The Export/Import panel
has a **DEVICE BACKUP** toggle; turning it off moves the store into Android's `no_backup`
directory, which is excluded from Auto Backup and device-to-device transfer — so an
uninstall genuinely wipes the data. Turning it back on moves it home again. The new copy
is always written before the old one is deleted, so the switch can't lose snippets, and
the active location is inferred from where the file actually is rather than from a
separate setting that could drift out of sync.

The toggle is hidden where the platform has no such concept (desktop). It is **not yet
wired up on iOS**, which needs `NSURLIsExcludedFromBackupKey` on the file rather than a
dedicated directory.

## Solution layout

| Project | Purpose |
|---|---|
| `Klippy` | Shared app: models, JSON store, search, view models, views, theme |
| `Klippy.Desktop` | Windows/macOS/Linux head |
| `Klippy.Android` / `Klippy.iOS` | Mobile heads |
| `Klippy.Tests` | xunit: search/store units + Avalonia headless UI tests |
| `tools/fontfix.cs` | One-shot TTF name-table normalizer (see below) |

Snippets persist as a single JSON file in the platform app-data folder
(`%APPDATA%\Klippy\snippets.json` on Windows), written atomically, serialized with
source-generated `System.Text.Json` (no reflection — AOT/trim safe). MVVM uses
CommunityToolkit.Mvvm source generators; all XAML bindings are compiled.

## Building

```sh
dotnet run --project Klippy.Desktop            # run the desktop app
dotnet test                                    # run tests (also captures artifacts/screenshot-*.png)
dotnet build Klippy.Android -c Debug           # Android (requires android workload)
```

### Release / publish

On Windows use [`build.ps1`](build.ps1), which publishes the desktop head in Release with
**NativeAOT** — no JIT, fast cold start, smallest output:

```powershell
.\build.ps1                                  # NativeAOT, win-x64
.\build.ps1 -Runtime win-arm64 -Clean -Test  # arm64, clean first, run tests
.\build.ps1 -NoAot -Output C:\dist\klippy    # fallback, custom output folder
```

> **Enabling NativeAOT.** It needs the MSVC toolset — the Windows SDK alone is not
> enough. If Visual Studio is already installed, add the single component rather than
> installing a second, standalone Build Tools copy (run elevated, then restart the shell):
>
> ```powershell
> & 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe' modify `
>     --installPath 'C:\Program Files\Microsoft Visual Studio\18\Community' `
>     --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --quiet --norestart
> ```
>
> Add `Microsoft.VisualStudio.Component.VC.Tools.ARM64` as well to publish `win-arm64`.
> The equivalent in the GUI is Visual Studio Installer → Modify → Individual components →
> search "MSVC". `build.ps1` prints the command tailored to your machine.

> **Execution policy.** Windows PowerShell 5.1 refuses unsigned scripts by default. Use
> PowerShell 7 (`pwsh`), or run it as
> `powershell -ExecutionPolicy Bypass -File .\build.ps1`.

NativeAOT links with MSVC, so the script checks for the Visual Studio
"Desktop development with C++" workload **before** building. Without that check the
failure only surfaces minutes in, as a bare "Platform linker not found". When the
component is missing it names it, prints the winget command to install it, and points at
`-NoAot`.

`-NoAot` publishes trimmed + ReadyToRun instead: no C++ toolchain needed and still quick
to start, but self-contained and ~54 MB rather than a single small native binary.

Other platforms publish directly:

```sh
dotnet publish Klippy.Desktop -c Release -r osx-arm64    # macOS (run on a Mac)
```

iOS is AOT by nature and trims `SdkOnly` (build on a Mac).

### Android

`build.ps1 -Android` publishes the Android head. It needs a JDK (17+) and the Android
SDK — `ANDROID_HOME`, or the Android Studio default — and checks for both up front:

```powershell
.\build.ps1 -Android                                   # Release, profiled AOT, arm64
.\build.ps1 -Android -Configuration Debug -Install      # quick build, push to device
.\build.ps1 -Android -NoAot                            # skip AOT, much faster to iterate
.\build.ps1 -Android -Abi android-x64                  # emulator
```

`-Install` runs `adb install -r` against the connected device.

Android's AOT is **not** the desktop's NativeAOT. Release turns on Mono *profiled* AOT
(`RunAOTCompilation`, `AndroidEnableProfiledAot`) plus full trimming and
`AndroidStripILAfterAOT`: hot startup paths are precompiled to native code while the Mono
runtime still ships inside the APK. So none of the MSVC toolchain above is involved, and
the size trade runs the *other* way — Release is larger than Debug (~14 MB arm64-only vs
~10 MB), because the precompiled native code outweighs what stripping the IL gives back.
It buys startup time, not size. Use `-NoAot` while iterating.

> **Stale APKs.** Android packaging is incremental and gets it wrong: when an APK is
> already present, MSBuild re-signs the previous package and reports success with zero
> errors even though the assemblies changed — so a "successful" build can silently ship
> stale code. `build.ps1 -Android` deletes the previous packages first to force a real
> repackage (much cheaper than `-Clean`, since the AOT output in `obj/` is reused), and
> warns if the APK it produced predates the build. Running `dotnet publish` on the
> project by hand does **not** protect you from this.

> **Signing.** No keystore is configured, so APKs are signed with the shared Android
> debug key. That is fine for sideloading, but they are not distributable, and a later
> release-signed build will not install over one without uninstalling first.
> `ApplicationId` is also still the template's `com.CompanyName.Klippy`.

## Design

`design_handoff_klippy/` holds the high-fidelity design reference (HTML mock + README
with the token sheet). The Avalonia theme mirrors it: tokens live in
[`App.axaml`](Klippy/App.axaml), control styles in
[`Styles/KlippyStyles.axaml`](Klippy/Styles/KlippyStyles.axaml), icons as
`StreamGeometry` in [`Styles/Icons.axaml`](Klippy/Styles/Icons.axaml).

### Fonts

Chivo and Chivo Mono (OFL) are bundled under `Klippy/Assets/Fonts`. The static TTFs
shipped by the foundry carry per-weight family names ("Chivo SemiBold"), which breaks
weight-based matching in Avalonia/Skia. `tools/fontfix.cs` rewrites the name tables so
all weights share one family; the committed fonts are already normalized. If you ever
replace them, re-run:

```sh
dotnet run tools/fontfix.cs -- Klippy/Assets/Fonts
```
