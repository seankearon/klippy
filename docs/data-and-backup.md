---
icon: lucide/database
description: "The store, the folder you choose, and how to back it up."
---

# Where data lives

Snippets are one JSON file in the platform app-data folder — `%APPDATA%\Klippy\snippets.json`
on Windows, `~/.config/Klippy/` on macOS/Linux, and `files/.config/Klippy/` inside the app
sandbox on Android (mode `0600`, app-private). `clipboard.history.json`,
`command.history.json` and `clips/` sit in the same folder, and so do `variables.txt` and
the snippets themselves unless told otherwise; `settings.json` is always there, wherever
the rest go. The whole list is held in memory and each mutation rewrites the file
atomically (temp file + replace), so a crash can't corrupt it. A copy also writes, because
`LastUsedAt` drives recency ranking.

**Three of those were renamed.** Up to 1.0.20 they were `history.json`,
`commands.json` and `klippy.vars`. A folder holding `history.json` beside `commands.json`
left you to work out which history, and whether commands were a log or a list of things
to run — so they now say what they are, and the defines file is a `.txt`, which every
desktop already knows how to open.

Klippy renames them on the first launch after the upgrade, so nothing is lost and there
is nothing to do. It never writes over a file already at the new name, and one it cannot
move is left where it is to be renamed by hand. The one exception is a `VariablesFile`
setting that names `klippy.vars` outright: only the *default* name changed, so a file you
have named stays exactly where you put it.

## Choosing where the files go (desktop)

`DataDirectory` is the folder everything lives in, and two files may name their way out of
it — the snippets and the variables file:

```json
{
  "DataDirectory": "D:\\Klippy",
  "SnippetsFile": "%OneDrive%\\Klippy\\snippets.json",
  "VariablesFile": ""
}
```

Those two and no others, because that pairing is the whole point: **the snippets are worth
sharing between a Mac and a Windows box, and `variables.txt` — the file that says where
`%ws%` is on *this* machine — is exactly what must not go with them.** The clipboard
history, the command MRU and the clip images stay in the data folder: they are the record
of what happened on one machine, and a synced folder is the last place for them.
`settings.json` stays behind too, in the platform's own app-data folder, because it is the
note saying where the snippets went — it cannot live in the folder it names.

For the two that are settable, empty means the usual name in the data folder —
`snippets.json` and `variables.txt`. A bare name is another file in that folder, which is
how you keep a work set beside a personal one. An absolute path is taken as given.

Environment variables and a leading `~` are expanded wherever a path is read, so
`"%OneDrive%\\Klippy"` and `"~/Klippy"` are both legitimate ways to write one. The *folder*
has to be absolute: relative to the exe, to the shell's working directory and to the
app-data folder are three different answers, so the setting insists on being told which one
you mean. A file name needs no such rule, since a bare one means the data folder.

`DataDirectory` and `SnippetsFile` are read once at startup, so they take a restart, and
**nothing is moved for you**: copy the files across first. That cuts in your favour too —
the old ones are left exactly as they were, so setting it back gets you back. A folder
Klippy cannot use is reported on stderr and ignored, costing that one preference rather
than the snippets. `VariablesFile` is the exception: it is re-read whenever the file
changes, so it applies to the next copy.

Settings gives the folder and those two files a box each, with a line under it saying
what is there — `in use`, `no file yet`, or `takes effect on restart` — and the resolved
path in front of that when it is not simply what you typed. See
[Files](settings.md#files).
They are desktop settings for the same reason the **FILES** block is hidden on mobile: app
storage there is private and unreachable, so there is no other folder to point at and no
`settings.json` anyone could edit.

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
the active location is inferred from where the file actually is rather than from a
separate setting that could drift out of sync.

The toggle is hidden where the platform has no such concept (desktop), and also where
`SnippetsFile` names a path — that setting has already said where the store lives, so
there would be nowhere to move it to that it would not name straight back. In practice
that second case is Android-only trivia: the **FILES** block is hidden there and
`settings.json` is unreachable, so there is no way to set it on the one platform the
toggle appears on. It is **not yet wired up on iOS**, which needs
`NSURLIsExcludedFromBackupKey` on the file rather than a dedicated directory.
