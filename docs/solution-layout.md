---
icon: lucide/folder-tree
description: "The projects that make up Klippy."
---

# Solution layout

| Project | Purpose |
|---|---|
| `Klippy` | Shared app: models, JSON store, search, view models, views, theme |
| `Klippy.Desktop` | Windows/macOS/Linux head |
| `Klippy.Android` / `Klippy.iOS` | Mobile heads |
| `Klippy.Tests` | xunit: search/store units + Avalonia headless UI tests |
| `tools/fontfix.cs` | One-shot TTF name-table normalizer (see below) |

Snippets persist as one JSON file per snippet in a folder in the platform app-data folder
(`%APPDATA%\Klippy\snippets\` on Windows), each written atomically, serialized with
source-generated `System.Text.Json` (no reflection — AOT/trim safe); recency is kept apart, per
machine, so a synced folder only changes when a snippet does — see
[Where data lives](data-and-backup.md#sharing-snippets-between-machines). MVVM uses
CommunityToolkit.Mvvm source generators; all XAML bindings are compiled.
