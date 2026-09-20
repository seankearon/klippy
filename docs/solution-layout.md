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

Snippets persist as a single JSON file in the platform app-data folder
(`%APPDATA%\Klippy\snippets.json` on Windows), written atomically, serialized with
source-generated `System.Text.Json` (no reflection — AOT/trim safe). MVVM uses
CommunityToolkit.Mvvm source generators; all XAML bindings are compiled.
