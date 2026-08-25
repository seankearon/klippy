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
search, `Ctrl/⌘+E` export/import, `Esc` clears/cancels, `Ctrl/⌘+Enter` saves in the
editor. Clicking a row also copies it.

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

The desktop head publishes with **Native AOT** by default — fast cold start, small
self-contained binary, no JIT:

```sh
dotnet publish Klippy.Desktop -c Release -r win-x64      # Windows (needs VS "Desktop development with C++")
dotnet publish Klippy.Desktop -c Release -r osx-arm64    # macOS (run on a Mac)
```

If the native linker isn't available, fall back to trimmed + ReadyToRun
(~53 MB self-contained, still quick to start):

```sh
dotnet publish Klippy.Desktop -c Release -r win-x64 -p:PublishAot=false -p:PublishTrimmed=true -p:PublishReadyToRun=true
```

Android Release builds use profiled AOT + full trimming (`RunAOTCompilation`,
`AndroidStripILAfterAOT`); iOS is AOT by nature and trims `SdkOnly` (build on a Mac).

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
