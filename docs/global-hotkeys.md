---
icon: lucide/keyboard
description: "Summoning Klippy from anywhere on the desktop."
---

# Global hotkeys

Klippy runs as a resident launcher: it stays alive behind a tray / menu-bar icon, and a
system-wide hotkey summons it. Closing the window hides it rather than quitting; see
[Quitting](quitting.md) for the ways out. Only one instance runs at a time, so
launching Klippy again just tells you it is already resident.

There are two keys, one per half of the app:

| Key (Windows / macOS) | Summons |
|---|---|
| `Ctrl+Alt+K` / `⌥⌘K` | Saved snippets |
| `Ctrl+Alt+J` / `⌥⌘J` | Clipboard history |

Each means *show me this view*. Pressing a key while its view is already in front
dismisses the window, as the single key always did; pressing the **other** key switches
views rather than hiding, which is the point of having two. `Esc` still dismisses once
the filter and any overlay are cleared. Switching views keeps what is in the search box:
you are looking for the same thing either way, and the other half of the answer should be
one keystroke away rather than one keystroke and a retype.

They register independently, so one losing the race for its combination leaves the other
working, and Klippy says on stderr which one it could not claim. The history key is only
registered where there is a history to summon — not on mobile, and not with history
switched off.

Stored in `settings.json`, in Klippy's app-data folder (Settings shows the path):

```json
{
  "Hotkey": "Ctrl+Alt+K",
  "HotkeyEnabled": true,
  "HistoryHotkey": "Ctrl+Alt+J"
}
```

`HotkeyEnabled` is the master switch for both. Setting `HistoryHotkey` to `""` turns off
just that one — it is not filled back in with the default, since that would re-register a
key you had just removed.

Modifiers may be written as Ctrl/Control, Alt/Option, Shift and Cmd/Command/Win/Meta, in
any order and any case; at least one modifier is required, since a bare key would swallow
that keystroke system-wide. If another application already owns the combination, Klippy
says so on stderr and starts without a hotkey rather than failing.

Keeping the app resident has a second benefit on Windows: the HTML clipboard flavour is
served through OLE for as long as Klippy runs, so the "paste before closing Klippy" caveat
above effectively disappears.

Implementation is per-platform, since Avalonia has no global-hotkey API. Each key is a
separate registration on its own [`MessageOnlyWindow`](https://github.com/seankearon/klippy/blob/main/Klippy.Desktop/MessageOnlyWindow.cs),
which costs an idle thread apiece and buys independent failure:
[`WindowsHotkey`](https://github.com/seankearon/klippy/blob/main/Klippy.Desktop/WindowsHotkey.cs) uses `RegisterHotKey`, and
[`MacHotkey`](https://github.com/seankearon/klippy/blob/main/Klippy.Desktop/MacHotkey.cs) uses
Carbon's `RegisterEventHotKey` — chosen over an event tap because it needs no Accessibility
permission. **The macOS path compiles but has not been run**, since it cannot be tested
from Windows.
