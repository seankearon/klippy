---
icon: lucide/command
description: "Keys of your own for a snippet: in Klippy, chords included, and from anywhere."
---

# Snippet shortcuts

A snippet can carry keys of its own, and pressing them triggers it. That does exactly what `Enter` on its
row does: the snippet is copied, or — when it is marked [Execute](running-things.md) — run. The one
difference is a snippet with [placeholders](#snippets-with-placeholders) the keys can't fill on their own:
one that takes an argument waits for you to type it, and one that uses the clipboard only goes ahead when
there is text on it. There are two kinds of keys, and a snippet can have either or both:

| | Works | Keys |
|---|---|---|
| **Shortcut in Klippy** | While Klippy's window is up | One press such as `Ctrl+Shift+L`, or a **chord** of two such as `Ctrl+K, Ctrl+L` |
| **Hotkey anywhere** | In any application, without summoning Klippy | One combination such as `Ctrl+Alt+1` or `Ctrl+Alt+/` — Windows and macOS |

Both are set in the snippet's editor, under **SHORTCUT IN KLIPPY** and **HOTKEY ANYWHERE**, and a row shows
the keys it has beside its quick-code — `Ctrl+K, Ctrl+L · Ctrl+Alt+1 anywhere`.

## Shortcuts in Klippy

Summon Klippy, press the snippet's keys, and it is copied — whatever the list is showing, the clipboard
history included, and whatever is in the search box. Nothing typed is involved, so nothing goes into
[recent commands](recent-commands.md) — except for a snippet that
[takes an argument](#an-argument-p), which waits for you to type one.

A **chord** is two presses one after the other, as Visual Studio and VS Code have them: `Ctrl+K`, then
`Ctrl+L`. After the first, a pill near the foot of the window says Klippy is waiting for the second. It waits as long
as you take; `Esc`, a click, or the window going away calls it off. A second press that finishes no chord
says so — `Ctrl+K, X isn't a shortcut` — rather than doing nothing, and whatever it was, the second press
**never types into the search box**: the `L` of `Ctrl+K, L` doesn't end up in your search.

Shortcuts stand aside while an overlay — the editor, a confirmation, export / import, settings — is open.

## Hotkeys anywhere

A hotkey triggers its snippet from any application: press `Ctrl+Alt+1` in your mail client and the snippet
is on the clipboard, ready to paste, without Klippy coming up. A snippet marked Execute runs instead. Klippy
comes up only when there is something for you to do or read: a snippet that
[takes an argument](#an-argument-p) brings it up with the quick-code waiting, and one that can't do its job —
a run that can't start, a [clipboard](#the-clipboard-c) with no text on it — brings it up to say why.

Hotkeys are registered the way [the summon keys](global-hotkeys.md) are, one registration each, so one that
another application already holds leaves the rest working. The editor says when a snippet's hotkey could
not be claimed, and so does a toast when you save one. They live under the same master switch: with
`HotkeyEnabled` off there are no hotkeys at all, and the editor offers none. On Windows each costs an idle
thread, as the summon keys do.

A hotkey is a letter, a digit, `Space`, `F1`–`F20`, or one of the punctuation keys `` / ; ' , . - = [ ] \ ` ``
— held with at least one of `Ctrl`, `Alt` or `Win` / `⌘`. `Shift` alone would take capitals from every
application. In the file a punctuation key is written as its character, or by name — `Ctrl+Alt+Slash` reads
as `Ctrl+Alt+/`.

!!! warning "Punctuation keys and your keyboard layout"
    A punctuation key is the key in that place on a **US** keyboard. On a US or UK layout `Ctrl+Alt+/` is the
    key marked `/`; on others the same physical key may be marked differently — on a German keyboard it is
    `#`, and `/` is `Shift+7`. Recording and registering always agree, since both go through your layout:
    the key you press is the key that fires. Only the name Klippy shows for it can differ from the keycap.

    On many European layouts `Ctrl+Alt` is also **AltGr**, which types characters such as `{` and `@`. A
    system-wide `Ctrl+Alt` hotkey takes that character from every application, letters and digits included —
    so where `Ctrl+Alt` types, choose a combination that doesn't.

!!! note
    Pressed while Klippy's editor or another overlay is open, a hotkey still copies its snippet — so one
    snippet can be pasted into another as you write it — but leaves the window as it is. One that takes an
    argument leaves the overlay alone and says why, rather than throw away what you were doing.

## Snippets with placeholders

A snippet's [macros](running-things.md#macros) are filled in as it is copied or run. Two of them need
something the keys alone can't supply, and the editor says what its keys will do about it as you set them.
The [variables file](variables.md) is applied first, as it is for a copy or a run, so a define can be what
brings a placeholder in.

### An argument: `%P%`

A snippet with a `%P%` — or `%P:file%`, `%P:exact%` — can't be done on the spot: its keys don't know what to
fill it with. So they do what you would have done by hand — put its [quick-code](finding-snippets.md) in the
search box with a space after it, the caret at the end, and wait. Type the argument and press `Enter`.

With the seeded **Google search** snippet — `https://www.google.com/search?q=%P%` behind the quick-code `?` —
pressing its keys leaves `? ` in the box, ready; `cats` and `Enter` then search for cats, and `? cats` goes
into [recent commands](recent-commands.md) as if you had typed it all. The same goes for a hotkey: it brings
Klippy up on the snippets, wherever you were, with the line waiting for you.

Arguments are typed after a quick-code, so a snippet that takes one needs a quick-code for its keys to work —
without one they only say so, and the editor warns as you set them.

### The clipboard: `%C%`

A snippet with a `%C%` and no `%P%` goes ahead at once, with whatever text is on the clipboard — which is
the point of giving it keys: copy a word anywhere, press the hotkey of a snippet holding
`https://www.google.com/search?q=%C%`, and the search is open. Nothing to type, so Klippy stays where it is.

When the clipboard holds no text — it is empty, or holds a picture or files, or only spaces — the keys do
nothing and say so, bringing Klippy up for a hotkey, rather than search for nothing or copy a snippet with a
hole in it. That is only the keys: `Enter` on the row still fills an empty clipboard in as nothing, as it
always has.

A snippet with both waits for its argument, as above; the clipboard is read when you press `Enter`.

## Setting keys

Click the field, then press the keys:

- **Shortcut in Klippy** — the first press is held: a second makes it a chord and is taken there and then;
  `Enter` keeps the single press as the whole shortcut. These fields are on the desktop only; a phone has no
  keys to press them with.
- **Hotkey anywhere** — one press, taken at once.

`Esc`, or clicking away, leaves the field as it was; **×** removes the keys. Nothing is kept until you
**Save**. A hotkey Klippy already holds — a summon key, another snippet's — can be recorded too: pressed
while the field is listening, it goes to the field instead of doing what it usually does.

Some keys are refused, with a line saying why:

- **Typing.** A shortcut has to start with `Ctrl`, `Alt` or `Win` / `⌘` held down — or with a function key.
  A shortcut on plain `K`, or `Shift+K`, would take the letter from the search box. The second press of a
  chord can be any key.
- **Klippy's own keys.** The first press can't be one Klippy already answers to: `Ctrl/⌘` with `N`, `F`,
  `E`, `D`, `I`, `P`, `R`, `,` or `Enter` — whatever else is held with it — or `F2`, the arrows, `Enter` and
  `Esc`. Nor the search box's own editing keys: select all, copy, cut, paste, undo and redo, and moving
  through the line by word or to either end. The second press of a chord is free of all of these:
  `Ctrl+K, Ctrl+N` is a chord of its own.
- **System-wide keys.** A shortcut can't start on a summon key or a snippet's hotkey, since the OS hands
  that press to the hotkey before the window ever sees it — and for the same reason a hotkey can't be where
  a shortcut starts. `Alt+Space` and `Alt+F4` belong to the system.

**No two snippets share keys.** Give a snippet keys another one has — the same keys, or a single press where
the other's chord starts — and the editor names it: `Ctrl+K, L is “Send log files”’s — saving moves it
here`. Saving takes them; the other snippet is left without. Duplicating a snippet leaves its keys behind,
as it does its quick-code.

## In the file

Both are stored on the snippet, in the snippets file and in an [export](export-import.md), so they travel
with it:

```json
{
  "Label": "Send log files",
  "QuickCode": "slf",
  "Shortcut": "Ctrl+K, Ctrl+L",
  "Hotkey": "Ctrl+Alt+1"
}
```

Empty, or absent in an older file, means none. A shortcut is written as the editor shows it, a chord with a
comma between its presses; VS Code's space-separated `ctrl+k ctrl+l` reads too. A hotkey is written as the
summon keys are. If an edit or an import leaves two snippets with the same keys, the first in the file keeps
them, and a shortcut that could never fire — one starting on typing, or on one of Klippy's own keys — is
left unbound.
