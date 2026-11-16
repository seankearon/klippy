---
icon: lucide/arrow-left-right
description: "Moving snippets between machines."
---

# Export / import

Open with the **export / import** footer link (`Ctrl/⌘+E`) on desktop, or the
transfer button in the mobile header.

- **Export** writes snippets to a JSON file — the whole set or a single tag
  (the scope defaults to whichever tag chip is active). It is one JSON list — the
  format the store itself was up to 1.0.33, before it became a folder of one file per snippet —
  so an export doubles as a backup, and an older Klippy can still import it. A snippet's
  [shortcut and hotkey](snippet-shortcuts.md) go with it; if an import brings keys another snippet
  already has, the older of the two keeps them.
- **Import** first previews the picked file (snippet count and the tags it
  contains), then merges everything or just one tag. Merging never wipes data:
  a snippet with a known id replaces the existing copy, everything else is added.
