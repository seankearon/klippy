---
icon: lucide/database
description: "The store, the folder you choose, sharing it between machines, and how to back it up."
---

# Where data lives

Snippets are a folder of JSON files, one per snippet, in the platform app-data folder —
`%APPDATA%\Klippy\snippets\` on Windows, `~/.config/Klippy/snippets/` on macOS/Linux, and
`files/.config/Klippy/snippets/` inside the app sandbox on Android (app-private). Each file is
named for the snippet's id — `3f2b8c1e-….json` — and holds that snippet and nothing else, so it
is as easy to read, copy or diff by hand as the snippet is.

`variables.txt` sits beside the folder unless told otherwise, and the snippets can be pointed
elsewhere too; `settings.json`, `clipboard.history.json`, `command.history.json`,
`snippet.usage.json`, `clips/` and `window.json` (where the window was left, and how big) are
always in the app-data folder, wherever those two go.

Everything is held in memory. An edit rewrites only that snippet's file, atomically (temp file +
replace), so a crash can't corrupt it, and a delete deletes the file. **Copying or running a
snippet writes nothing to the snippets folder**: when each one was last used — the recency the
list is ranked by — is kept in `snippet.usage.json` beside `settings.json`, because it changes on
every copy and is a fact about this machine. Recency is therefore per machine: a snippet you use
all day at work ranks first there and nowhere else, and one never used on this machine ranks by
when it was made.

The snippets are kept in **creation order** — oldest first, and by id between two made at the
same moment. That is the order a clash is settled in when two snippets ask for the same
[shortcut or hotkey](snippet-shortcuts.md#in-the-file), or answer to the same quick-code: the
older one keeps it. It is an order every machine works out for itself, which a folder of files has
to have, since there is no file order any more.

**Up to 1.0.33 the snippets were one file, `snippets.json`**, rewritten whole on every change —
every copy too, to note when it was used. On the first start after the upgrade Klippy writes each
snippet in it to its own file, copies when each was last used into `snippet.usage.json`, and
renames the old file `snippets.migrated.json`. Nothing is deleted. A `snippets.json` that cannot be
read — locked, or cloud-only and not downloaded — is left exactly where it is and tried again on
the next start; until then nothing is written in its place and the examples are not put back.

**Three of those were renamed.** Up to 1.0.20 they were `history.json`,
`commands.json` and `klippy.vars`. A folder holding `history.json` beside `commands.json`
left you to work out which history, and whether commands were a log or a list of things
to run — so they now say what they are, and the defines file is a `.txt`, which every
desktop already knows how to open.

Klippy moves them on the first launch after the upgrade, so nothing is lost and there is
nothing to do. If you had set `DataDirectory`, the history, the MRU and the clip images
are fetched back out of it into the app-data folder at the same time — they used to
follow it and no longer do. Klippy never writes over a file already at the new name, and
one it cannot move is left where it is to be dealt with by hand. The one exception is a
`VariablesFile` setting that names `klippy.vars` outright: only the *default* name
changed, so a file you have named stays exactly where you put it.

## Choosing where the files go (desktop)

`DataDirectory` is the folder the snippets folder and the variables file live in, and each of
those two may name its way out of it as well:

```json
{
  "DataDirectory": "D:\\Klippy",
  "SnippetsFolder": "%OneDrive%\\Klippy\\snippets",
  "VariablesFile": ""
}
```

Those two and no others, because that pairing is the whole point: **the snippets are worth
sharing between a Mac and a Windows box, and `variables.txt` — the file that says where
`%ws%` is on *this* machine — is exactly what must not go with them.**

**The clipboard history, the command MRU, the snippet usage and the clip images ignore all
of it** and stay in the platform's own app-data folder — `%APPDATA%\Klippy` on Windows — beside
`settings.json`. They are the record of what happened on one machine, and `DataDirectory`
is a setting people point at a synced drive: a log of everything you have copied must not
follow the snippets there as a side effect of moving them. `settings.json` stays for its
own reason — it is the note saying where the snippets went, so it cannot live in the
folder it names.

For the two that are settable, empty means the usual name in the data folder — `snippets`
and `variables.txt`. A bare name is another folder or file in that folder, which is how you keep
a work set beside a personal one. An absolute path is taken as given.

**`SnippetsFile` is what the setting was called while the store was a file.** A settings.json
that still has it keeps working: the folder is the same path without its `.json` —
`%OneDrive%\Klippy\snippets.json` becomes `%OneDrive%\Klippy\snippets`, and
`work-snippets.json` becomes `work-snippets` beside `snippets`, so two sets that sat side by side
still do — and the file it names is the one carried forward. A name without `.json` gets
`.snippets` on the end instead, since the folder cannot share a name with the file still sitting
there. `SnippetsFolder` overrules it, and Settings swaps one for the other the first time you
change the box.

Environment variables and a leading `~` are expanded wherever a path is read, so
`"%OneDrive%\\Klippy"` and `"~/Klippy"` are both legitimate ways to write one. The *folder*
has to be absolute: relative to the exe, to the shell's working directory and to the
app-data folder are three different answers, so the setting insists on being told which one
you mean. A file name needs no such rule, since a bare one means the data folder.

`DataDirectory` and `SnippetsFolder` are read once at startup, so they take a restart, and
**nothing is moved for you**: copy the files across first. That cuts in your favour too —
the old ones are left exactly as they were, so setting it back gets you back. A folder
Klippy cannot use is reported on stderr and ignored, costing that one preference rather
than the snippets. `VariablesFile` is the exception: it is re-read whenever the file
changes, so it applies to the next copy.

Settings gives the data folder and those two a box each, with a line under it saying
what is there — `in use`, `no folder yet`, `no file yet`, or `takes effect on restart` — and
the resolved path in front of that when it is not simply what you typed. See
[Files](settings.md#files).
They are desktop settings for the same reason the **FILES** block is hidden on mobile: app
storage there is private and unreachable, so there is no other folder to point at and no
`settings.json` anyone could edit.

## Sharing snippets between machines

Point `DataDirectory` — or just `SnippetsFolder` — at a folder a sync client keeps in step
(OneDrive, Dropbox, Proton Drive, iCloud Drive, Syncthing…) on each machine, and both see one set
of snippets. It works because of one rule: **no two machines ever rewrite the same file as part of
normal use.**

- **Each snippet is its own file**, so snippets added on two machines are two different files, and
  an edit on one touches only that snippet. A delete deletes its file, and the sync client passes
  that on.
- **Copying or running writes nothing there.** Recency lives in `snippet.usage.json` on each
  machine, so the shared folder only changes when a snippet does.
- **Changes show up without a restart.** Klippy watches the folder and reloads only the files that
  changed, half a second after the sync client has finished with them — and looks again every time
  the window is summoned, for a sync client or a virtual drive that raises no events. The
  selection stays where it was.
- **A file it cannot read is never taken for an empty or a deleted one.** A cloud-only file that
  will not open yet, or one the sync client has locked, is skipped and read again on the next look;
  the snippet it held stays on the list meanwhile. Nothing is ever saved as a result of reading, so
  a failed read can cost nothing on disk.

**Edit conflicts.** Each save stamps the snippet with `ModifiedAt` (UTC) and `ModifiedBy` (the
machine's name). If the *same* snippet is edited on two machines before they sync, the sync client
keeps both, under a name of its own — `<id> (# Edit conflict … #).json`,
`<id> (Sam's conflicted copy …).json`, `<id>-MACHINE.json`, `<id>.sync-conflict-….json`. Klippy
recognises them by what they hold rather than what they are called: two files holding the same
snippet id are two versions of it. The later `ModifiedAt` becomes `<id>.json`, and the other is
moved to a `conflicts` folder beside `snippets` — `<DataDirectory>/conflicts/` in the usual
layout — and a notice says so, since the version put aside is somebody's edit. A copy that says
nothing the kept one does not is simply removed. A version is only ever moved over the snippet's
own file once that file has been read. The same goes for an editor left open here while the snippet
was edited elsewhere: saving keeps your version and puts the other in `conflicts`.

Conflicts need the same snippet edited on two machines inside the sync client's delay, so they
should be rare — and the machine clocks settle them, so keep those right.

**Upgrade every machine that shares the folder.** An older Klippy goes on rewriting
`snippets.json` from memory, and the new one only reads that file at startup: the next start
carries forward any snippet in it the folder does not have yet, overwrites nothing that is already
there, and renames the file `snippets.migrated.2.json`. A snippet deleted on the new version but
still held by the old one can come back that way, which is the price of never stranding one. If
two machines carry the same `snippets.json` forward at once, they write byte-identical files, and
any copy the sync client keeps of one is removed on the next look.

Conflict copies of the *old* single file — `snippets (# Edit conflict … #).json` and the like —
are not merged for you: each is a whole list from one moment, and merging them would bring back
snippets deleted since. Import one through [Export / import](export-import.md) if it holds
something you want back, bearing in mind that an import replaces a snippet with the same id.

**Android backup is on by default.** `allowBackup="true"` is now set explicitly rather
than relied on as an implicit default, and both rule files
([`backup_rules.xml`](https://github.com/seankearon/klippy/blob/main/Klippy.Android/Resources/xml/backup_rules.xml) for API ≤30,
[`data_extraction_rules.xml`](https://github.com/seankearon/klippy/blob/main/Klippy.Android/Resources/xml/data_extraction_rules.xml) for
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
the active location is inferred from where the folder actually is rather than from a
separate setting that could drift out of sync. A `snippets.migrated.json` left by the upgrade
goes with it, since it is a full copy of the snippets.

The toggle is hidden where the platform has no such concept (desktop), and also where
`SnippetsFolder` (or an older `SnippetsFile`) names a path — that setting has already said where the store lives, so
there would be nowhere to move it to that it would not name straight back. In practice
that second case is Android-only trivia: the **FILES** block is hidden there and
`settings.json` is unreachable, so there is no way to set it on the one platform the
toggle appears on. It is **not yet wired up on iOS**, which needs
`NSURLIsExcludedFromBackupKey` on the files rather than a dedicated directory.
