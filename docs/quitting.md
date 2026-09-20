---
icon: lucide/power
description: "Closing Klippy on the desktop, and what happens to the tray."
---

# Quitting

Being resident is the point, so nothing about the window ends Klippy: closing it hides
it, and `Esc` dismisses it. Three things do end it — two of them from the window you are
already looking at:

- **Type `quit`.** It appears as an offer in the band where the first row would have
  been, reading `Quit Klippy · stop listening and leave the tray`, and `↵` takes it —
  the same gesture, in the same place, as [`lock` or `restart`](unmatched-search.md).
- **The `quit` footer link**, for when the pointer is already down there.
- **The tray / menu-bar icon's Quit**, which is where it has always been.

Both of the in-window routes land on the same confirmation, because the search box and a
footer full of links are places a stray keystroke or click can reach: `↵` or **Quit Klippy**
closes, `Esc` or **Cancel** goes back. The tray menu asks nothing and never did — picking
**Quit** off a two-item menu is already a deliberate act.

Nothing is at stake in the data — snippets are written per edit and the clipboard history
flushes on exit — but the hotkeys and the tray icon go with it, and getting them back means
launching Klippy again. That, rather than lost work, is what the dialog says.

**An item that matches beats it**, exactly as one beats `lock`: `quit` is an ordinary word,
and a snippet answering to it was plausibly what was being looked for. A snippet called
"Quit the trial" keeps the word a filter for as long as it exists — the footer link and the
tray are then the ways out, which is why the link is there and not only the word.

It is **not** part of [running an unmatched search](unmatched-search.md), although
it stands in the same place and answers to the same key. Klippy closing is not Klippy
starting something: no plan reaches
[`ProcessLauncher`](https://github.com/seankearon/klippy/blob/main/Klippy/Services/ProcessLauncher.cs), and it is outside the **Run it**
setting — being unwilling to hand typed text to the machine is no reason to be unable to
close the app. For the same reason its confirmation is not the one **Confirm OS actions**
can waive: that governs the machine's controls, and this one closes a program.

The word is deliberately not offered on mobile: Android leaves closing to the system's own
gesture, and iOS forbids an app quitting itself outright, so there `quit` stays an ordinary
search term.
