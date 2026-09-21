---
icon: lucide/database
description: "The store, the folder you choose, and how to back it up."
---

# Where data lives

Snippets are one JSON file in the platform app-data folder — `%APPDATA%\Klippy\snippets.json`
on Windows, `~/.config/Klippy/` on macOS/Linux, and `files/.config/Klippy/` inside the app
sandbox on Android (mode `0600`, app-private). By default `settings.json`, `history.json`,
`commands.json`, `clips/` and `klippy.vars` sit in the same folder. The whole list is held
in memory and each mutation rewrites the file atomically (temp file + replace), so a crash
can't corrupt it. A copy also writes, because `LastUsedAt` drives recency ranking.

## Choosing where the files go (desktop)

Every file is named on its own, and `DataDirectory` is the folder the ones without a path
of their own fall into:

```json
{
  "DataDirectory": "D:\\Klippy",
  "SnippetsFile": "%OneDrive%\\Klippy\\snippets.json",
  "HistoryFile": "",
  "CommandsFile": "",
  "VariablesFile": ""
}
```

Empty means the usual name in the data folder — `snippets.json`, `history.json`,
`commands.json`, `klippy.vars`. A bare name is another file in that folder, which is how
you keep a work set beside a personal one. An absolute path is taken as given, and is the
point of the whole arrangement: **the snippets are worth sharing between a Mac and a
Windows box, and `klippy.vars` — the file that says where `%ws%` is on *this* machine — is
exactly what must not go with them.** The clipboard history and the command MRU stay local
for the same reason: a record of what you copied and typed on one machine has no business
turning up on the other.

Environment variables and a leading `~` are expanded wherever a path is read, so
`"%OneDrive%\\Klippy"` and `"~/Klippy"` are both legitimate ways to write one. The *folder*
has to be absolute: relative to the exe, to the shell's working directory and to the
app-data folder are three different answers, so the setting insists on being told which one
you mean. A file name needs no such rule, since a bare one means the data folder.

Clip images follow the history: they live in a `clips/` folder beside whichever file
`HistoryFile` names, because a blob means nothing apart from the entry that names it.

`settings.json` is the one file that stays behind, because it is the note saying where
everything else went — it cannot live in the folder it names.

All of it is read once at startup, so it takes a restart, and **nothing is moved for
you**: copy the files across first. That cuts in your favour too — the old ones are left
exactly as they were, so setting it back gets you back. A folder Klippy cannot use is
reported on stderr and ignored, costing that one preference rather than the snippets.

Settings gives each of them a box, with the resolved path under it and `in use` or
`takes effect on restart` beside that — see [Files](settings.md#files). They are desktop
settings for the same reason the **FILES** block is hidden on mobile: app storage there is
private and unreachable, so there is no other folder to point at and no `settings.json`
anyone could edit.

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

The toggle is hidden where the platform has no such concept (desktop). It is **not yet
wired up on iOS**, which needs `NSURLIsExcludedFromBackupKey` on the file rather than a
dedicated directory.
