---
icon: lucide/database
description: "The store, the folder you choose, and how to back it up."
---

# Where data lives

Snippets are one JSON file in the platform app-data folder — `%APPDATA%\Klippy\snippets.json`
on Windows, `~/.config/Klippy/` on macOS/Linux, and `files/.config/Klippy/` inside the app
sandbox on Android (mode `0600`, app-private). `settings.json`, `history.json`, `clips/`
and `klippy.vars` sit in the same folder. The whole list is held in memory and each
mutation rewrites the file atomically (temp file + replace), so a crash can't corrupt it.
A copy also writes, because `LastUsedAt` drives recency ranking.

## Choosing the folder (desktop)

`DataDirectory` moves the lot — snippets, history, clip images and the variables file:

```json
{
  "DataDirectory": "D:\\Klippy"
}
```

Environment variables and a leading `~` are expanded, so `"%OneDrive%\\Klippy"` and
`"~/Klippy"` are both legitimate ways to write it. The path has to be absolute: relative to
the exe, to the shell's working directory and to the app-data folder are three different
answers, so the setting insists on being told which folder you mean.

`settings.json` is the one file that stays behind, because it is the note saying where
everything else went — it cannot live in the folder it names. Settings shows both paths,
and the variables file, under **FILES**, with an **Open** button on the folder.

It is read once at startup, so it takes a restart, and **nothing is moved for you**: copy
the files across first. That cuts in your favour too — the old folder is left exactly as it
was, so setting it back gets you back. A path Klippy cannot use is reported on stderr and
ignored, costing that one preference rather than the snippets.

It is a desktop setting for the same reason the **FILES** block is hidden on mobile: app
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

The toggle is hidden where the platform has no such concept (desktop). It is **not yet
wired up on iOS**, which needs `NSURLIsExcludedFromBackupKey` on the file rather than a
dedicated directory.
