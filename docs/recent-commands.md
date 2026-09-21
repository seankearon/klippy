---
icon: lucide/history
description: "The MRU: what lands in it, and what deliberately does not."
---

# Recent commands

The search box is a command line as much as a filter — a quick-code, then the arguments
that fill its `%P%` — and a command line you have to retype is half a command line. Klippy
remembers the lines that did something and offers them back:

- **`↓` on an empty search box** opens the list at the command you used last. `↑`/`↓`
  browse it, and each line lands in the search box as you reach it, so the row underneath
  already shows what `Enter` will do — the expanded URL, the script with its arguments.
  `Enter` then does what it always does: whatever the row is marked for, a copy or a run.
- **Typing** opens it on what the line could still become. Type `?` and the
  `? cats and dogs` you ran yesterday is there to be taken; matching is on the start of
  the line and ignores case.
- **`Esc`** leaves the list, putting back whatever you had typed before you started
  browsing — and `↑` past the top does the same, which is the way back to the snippet
  list for anyone who opened it by accident.
- **A click** takes the line without running it, so a command can be edited before
  `Enter` sends it.

A *command* is a line that did something: whatever was in the search box at the moment a
snippet was copied or run. Browsing to a row and pressing `Enter` with an empty box
records nothing, because there is no line to recall. A command used again moves back to
the top rather than being duplicated, which is what "most recently used" means, and
`? Cats` is kept apart from `? cats` — they search for different things.

One pair of arrow keys, two lists that could want them. The rule is that the MRU has them
only while it is open, and it is only open when it has something to say: a `↓` on an empty
box, or a typed line that starts a command run before. That is also why matching is a
prefix of the *whole line*, rather than the word-prefix search the snippet list uses —
anything looser would hold the list open, and the arrows with it, over ordinary searches
that were never commands. With it closed, `↑`/`↓` are the list's, exactly as before.

The MRU belongs to the snippet command line: a filter typed against the
[clipboard history](clipboard-history.md) is not a command, is not recorded, and
`↓` there steps through the clips as it always did. It is **desktop only**, for the reason
the history is — the gesture is a keyboard one, and a phone has neither the keys to browse
with nor the room for the list they open.

## Settings

In `settings.json`, beside the hotkeys:

```json
{
  "CommandHistoryLimit": 100
}
```

At the limit the oldest commands are dropped. **Zero turns the MRU off** and forgets what
it has already recorded, since the file is a record of what you have been typing and that
is a thing a person is entitled to decline.

Commands live in `commands.json` in the data folder — or wherever
[`CommandsFile`](settings.md#files) points — as a plain JSON array of strings so the file
can be read and edited by hand. Each is written as it is recorded rather than
on a timer: a command arrives when you press `Enter`, not on every copy made anywhere on
the system, so there is nothing to batch. A line is kept exactly as it was typed — `slf `
is an invocation of `slf` with nothing after it yet, and trimming that space would recall
something subtly different from what was run.
