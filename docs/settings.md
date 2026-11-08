---
icon: lucide/settings
description: "Every setting and what it changes."
---

# Settings

Three preferences change what a copy puts on the clipboard, all about Markdown, two more
say whether a copy dismisses the window, one brings a script's folder up to date before it
runs, a list adds kinds of file and of link to open, three govern running an unmatched search, one
starts Klippy when you log in, and one says where the window lands when you summon it. Open with the **settings** footer link
(`Ctrl/⌘+,`) on desktop, or the sliders button in the mobile header — Android shows no
window chrome, so there is no footer to reach.

| Toggle | On (default) | Off |
|---|---|---|
| **Rich text** | A Markdown snippet copies HTML alongside the plain text | It copies its raw Markdown everywhere — what you want when the target is another Markdown editor |
| **Double spacing** | A blank line between blocks, so composers that strip `<p>` margins (Zendesk) still show the separation | Blocks are joined directly, which suits Outlook and Word — they honour those margins and would otherwise space it twice over |
| **Bare links** (default off) | In the plain-text flavour, `[some words](https://www.qwe.com)` becomes just `https://www.qwe.com` — for apps that paste as plain text (the Zendesk mobile app), where the link syntax would otherwise land verbatim. The HTML flavour keeps its links intact | The plain text is the Markdown source as written |

Two more, desktop only, decide whether a copy dismisses Klippy — the same dismissal
`Esc` does, leaving it resident behind the tray icon. They are separate because the two
halves of the app are used differently: you summon the history to paste one clip and be
gone, while snippets get browsed and copied a couple at a time. Mobile has no launcher to
dismiss to, so the pair is hidden there.

| Toggle | On | Off |
|---|---|---|
| **Close on clip** (default on) | Copying from the clipboard history hides the window, so the app you are pasting into comes straight back to the front | The list stays up |
| **Close on snippet** (default off) | Copying a snippet hides the window too | The list stays up for the next copy |

Neither applies to a clip picked from the history brought up
[over the editor](global-hotkeys.md) — that clip is wanted in the dialog underneath, so the
window stays where it is.

One, desktop only, says whether running a script or an application
[pulls its folder first](running-things.md#pull-first) — where what you run is kept in a checkout, this is
what keeps it current.

| Toggle | On | Off (default) |
|---|---|---|
| **Pull first** | `git pull` runs in the script or application's own folder, and is waited for, before it starts | It starts as it is on disk |

Under it, **ALSO OPEN** is a list rather than a toggle: the kinds of file opened
[as documents are](running-things.md#opening-other-kinds-of-file) beyond the built-in ones —
`.slnx` for solutions that should open in their IDE — and the
[kinds of link](running-things.md#other-kinds-of-link) handed to their application, written
with the colon: `vscode:`. Type one and press `Enter` or **Add**; click its chip to take it
off. A script or an application is refused, since those run by rules of their own, and so are
`file:`, since a `file:` link is a path, `javascript:` and its like, and a kind that opens
already.
Empty by default; in `settings.json` the kinds of file are `ExecuteOpenExtensions` and the
kinds of link `ExecuteOpenSchemes`.

Three more, desktop only, govern
[running an unmatched search](unmatched-search.md) — the one feature that executes
rather than copies, which is why all three default to the cautious answer.

| Toggle | On (default) | Off |
|---|---|---|
| **Run it** | A search that matched nothing and names something runnable is offered, and `Enter` runs it — as is each value of a [define](variables.md#typing-a-name) typed by name, and a [console](variables.md#a-console-there) in each place one names | `Enter` does nothing, as it always did on an empty list |
| **Verify paths** | Only a path that is really there is offered | Any rooted path is offered and the OS reports the failure — for a share that is slow to answer, or a path that does not exist yet |
| **Confirm OS actions** | Lock, sleep, hibernate and restart ask first; `Enter` again confirms | They run on the `Enter` that offered them |

In `settings.json`:

```json
{
  "ExecutePullFirst": false,
  "ExecuteUnmatched": true,
  "ExecuteVerifyPaths": true,
  "ExecuteConfirmSystemActions": true,
  "ExecuteOpenExtensions": [],
  "ExecuteOpenSchemes": []
}
```

One more, on Windows and macOS, says whether Klippy **starts when you log in**.

| Toggle | On | Off (default) |
|---|---|---|
| **Start at login** | Klippy starts as you log in, put away behind its tray or menu-bar icon, so the hotkeys work from the first minute | It starts when you start it |

It comes up the way a dismissed Klippy is — no window, just the icon and the hotkeys —
since logging in is not asking for a window. The first summons brings the window up, where
**ON SUMMON** below says. Starting Klippy by hand with `--background` does the same.

There is no key for it in `settings.json`. The setting *is* the platform's own list of what
starts at login: read when Settings opens and written as the toggle flips, so it never says
something the next login will not do.

- **Windows** keeps it as a `Klippy` value under
  `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, holding
  `"…\Klippy.Desktop.exe" --background`. That is the list Task Manager's **Startup apps** and
  Settings' **Apps › Startup** show, so Klippy appears there by name. Switched off there, it
  reads as off here, and turning it on here switches it back on there too. It is per user, so
  nothing asks for an administrator.
- **macOS** keeps it as a LaunchAgent,
  `~/Library/LaunchAgents/com.seankearon.klippy.login.plist`, which runs
  `open -g -a /Applications/Klippy.app --args --background` at login — through `open`, which
  is what starts an app as an app, and `-g`, so it does not take the foreground. macOS
  notes a new one with a notification and lists it under **System Settings › General ›
  Login Items**. Switching it off *there* leaves the file in place, and macOS gives an app no
  way to see that switch, so Klippy's toggle goes on reading on; switch it off here instead,
  which removes the file. A LaunchAgent rather than the newer login-item API because that API
  needs macOS 13, Klippy runs on 12, and it cannot pass `--background`. **This has not been
  run on a Mac**, for the same reason as the macOS hotkey: it cannot be tested from Windows.

It names the copy of Klippy that was running when it was turned on. Run a different copy —
a portable one, or a build of your own — and the line beside the toggle says which copy
starts at login instead of claiming it is this one; turn it off and on again from the copy
you want. Uninstalling does not take it away, so turn it off first, or Windows goes on
listing an entry that starts nothing. Linux desktops each keep their own list, so the toggle
is not offered there, nor on mobile, which has no login to start at.

One more, desktop only, says where the window lands when a key summons it. Klippy is
resident, so by default it comes back exactly where you left it — which is worth keeping
if it always lives in the same corner, since after a week your hand finds the search box
without looking. Less so across a multi-monitor desk, where "where you left it" is often
a screen you have since turned away from.

| Choice | Where the window appears |
|---|---|
| **Where it was** (default) | Wherever it last sat — including across a restart, and centred on the main screen instead if that place is no longer on any screen |
| **Centre** | Centred on the screen the pointer is on |
| **Pointer** | Hung from the mouse pointer, dropped far enough that the cursor lands on the search box, and nudged to stay fully on screen |

A summon also brings the window to the **virtual desktop you are on**. A window is assigned
to a desktop when it becomes visible and stays there, so one left showing on another desktop
would otherwise be found rather than summoned: the activate would take you to it instead of
bringing it to you, which is the opposite of what a hotkey means. Klippy hides it first, and
the show that follows lands it where you are. Nothing to configure, and nothing to pay on
the usual path — a dismissed window is already hidden.

Its **size** is remembered whichever you choose: resize the window and it opens that size next
time, held to the screen it lands on so a window left filling a 4K monitor still fits a
laptop's. Both are noted each time the window is put away or Klippy quits, in `window.json`
beside `settings.json` — a file of its own, so the one you edit by hand is never rewritten
behind your back, and never in the data folder, since where a window sat means nothing on
another machine's monitors. A window put away minimised or maximised leaves the last normal
size and place as they were. Delete `window.json` to go back to the defaults.

It applies whenever Klippy comes back to you — the hotkeys, the tray icon, and the first
appearance at launch — including when the window was left sitting behind whatever you were
working in. It never moves a window that is already in front of you and focused, so
switching between snippets and history with the other hotkey leaves it exactly where it is
rather than throwing it across the desk mid-use. In `settings.json` the key is `SummonPlacement`,
spelled `"Remembered"`, `"Centre"` or `"Pointer"`; anything else reads as `"Remembered"`
rather than costing you the rest of the file.

Each toggle saves as it is flipped, into the same `settings.json` as the hotkeys; there
is no OK button to forget. The panel scrolls rather than running off the bottom of a short
window. The Markdown three default to today's behaviour, so an upgrade changes nothing about
how existing snippets copy — and **Where it was** does the same for the window.

## Files

A **FILES** block at the bottom is where
[the paths live](data-and-backup.md#choosing-where-the-files-go-desktop), so they are a
thing you set here rather than a hand-edit. `settings.json` cannot move, since it is the
file that says where the others went, so it is shown and nothing more. Under it come three
boxes: the data folder, the snippets, and the [variables file](variables.md).

| Box | Empty means | Point it elsewhere to |
|---|---|---|
| **Data folder** | the platform's app-data folder | keep the snippets and defines on another drive |
| **Snippets** | `snippets.json` in the data folder | share one set between two machines through a synced folder |
| **Variables file** | `variables.txt` in the data folder | keep this machine's `%ws%` local while the snippets are shared |

Those last two are the reason the boxes exist at all: a snippet is the same text on every
machine and a path is not, so the store can be shared exactly because the file that
translates it is not.

The **clipboard history**, the **recent commands** and the clip images have no box, and
do not follow the data folder either. They stay in the platform's own app-data folder
beside `settings.json`, wherever you point the other three. They are the record of what
happened on this machine, and the data folder is a thing people aim at a synced drive: a
log of everything you have copied should not end up there as a side effect of moving your
snippets.

Last comes **SAVED IMAGES**: the folder a picture from the clipboard history is written
to when you [show it in its folder or copy its path](clipboard-history.md#text-files-and-images).
Empty means `Pictures/Klippy`; **Open** opens it, creating it first if nothing has been
saved there yet. It is read at each save, so a change needs no restart.

For the two files that are settable, a bare name is another file in the data folder — a
work set beside a personal one. An absolute path is taken as given, and environment
variables and a leading `~` are expanded in both.

Under each box is what is there — `in use`, `no file yet`, `3 variables`, or
`takes effect on restart` for a path that will not be picked up until the next launch —
with the resolved path in front of it whenever that is not simply what you typed. A bare
name is worth resolving on screen; an absolute one is already the answer, so repeating it
would read like a second setting.

The data folder and the snippets are read once at startup and held from then on, and
**nothing is moved for you** — copy the file across first, and the old one is left
exactly as it was. The variables file is the exception, re-read whenever it changes.

**Open** opens a file the way double-clicking it would. Only the variables file also
offers **Create**, because it is the only one Klippy knows what to put in — a commented
example — so it has a button whether or not the file is there. The snippets box shows
**Open** once `snippets.json` exists, which it normally does, and no button at all while
it does not: only the store knows what belongs in an empty one. **Folder** is always
there. The whole block is hidden on mobile, where app storage is private and unreachable
— and where the buttons would have no launcher to open anything with.
