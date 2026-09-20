---
icon: lucide/settings
description: "Every setting and what it changes."
---

# Settings

Three preferences change what a copy puts on the clipboard, all about Markdown, two more
say whether a copy dismisses the window, one brings a script's folder up to date before it
runs, three govern running an unmatched search, and one says where the window lands when
you summon it. Open with the **settings** footer link
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

One, desktop only, says whether running a script or an application
[pulls its folder first](running-things.md#pull-first) — where what you run is kept in a checkout, this is
what keeps it current.

| Toggle | On | Off (default) |
|---|---|---|
| **Pull first** | `git pull` runs in the script or application's own folder, and is waited for, before it starts | It starts as it is on disk |

Three more, desktop only, govern
[running an unmatched search](unmatched-search.md) — the one feature that executes
rather than copies, which is why all three default to the cautious answer.

| Toggle | On (default) | Off |
|---|---|---|
| **Run it** | A search that matched nothing and names something runnable is offered, and `Enter` runs it | `Enter` does nothing, as it always did on an empty list |
| **Verify paths** | Only a path that is really there is offered | Any rooted path is offered and the OS reports the failure — for a share that is slow to answer, or a path that does not exist yet |
| **Confirm OS actions** | Lock, sleep, hibernate and restart ask first; `Enter` again confirms | They run on the `Enter` that offered them |

In `settings.json`:

```json
{
  "ExecutePullFirst": false,
  "ExecuteUnmatched": true,
  "ExecuteVerifyPaths": true,
  "ExecuteConfirmSystemActions": true
}
```

One more, desktop only, says where the window lands when a key summons it. Klippy is
resident, so by default it comes back exactly where you left it — which is worth keeping
if it always lives in the same corner, since after a week your hand finds the search box
without looking. Less so across a multi-monitor desk, where "where you left it" is often
a screen you have since turned away from.

| Choice | Where the window appears |
|---|---|
| **Where it was** (default) | Wherever it last sat — what Klippy has always done |
| **Centre** | Centred on the screen the pointer is on |
| **Pointer** | Hung from the mouse pointer, dropped far enough that the cursor lands on the search box, and nudged to stay fully on screen |

A summon also brings the window to the **virtual desktop you are on**. A window is assigned
to a desktop when it becomes visible and stays there, so one left showing on another desktop
would otherwise be found rather than summoned: the activate would take you to it instead of
bringing it to you, which is the opposite of what a hotkey means. Klippy hides it first, and
the show that follows lands it where you are. Nothing to configure, and nothing to pay on
the usual path — a dismissed window is already hidden.

It applies whenever Klippy comes back to you — the hotkeys, the tray icon, and the first
appearance at launch — including when the window was left sitting behind whatever you were
working in. It never moves a window that is already in front of you and focused, so
switching between snippets and history with the other hotkey leaves it exactly where it is
rather than throwing it across the desk mid-use. In `settings.json` the key is `SummonPlacement`,
spelled `"Remembered"`, `"Centre"` or `"Pointer"`; anything else reads as `"Remembered"`
rather than costing you the rest of the file.

A **FILES** block at the bottom names the three paths that matter. `settings.json` cannot
move, since it is the file that says where the others went. The data folder is a
[hand-edit](data-and-backup.md#choosing-the-folder-desktop) that takes effect on the next launch, with an
**Open** beside it. The [variables file](variables.md) is a text box you can
point anywhere, with buttons to create or open it and to open its folder — see
[Getting at the file](variables.md#getting-at-the-file). The whole block is hidden on mobile, where app
storage is private and unreachable.

Each toggle saves as it is flipped, into the same `settings.json` as the hotkeys; there
is no OK button to forget. The panel scrolls rather than running off the bottom of a short
window. The Markdown three default to today's behaviour, so an upgrade changes nothing about
how existing snippets copy — and **Where it was** does the same for the window.
