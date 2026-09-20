---
icon: lucide/package
description: "Building an import file from markdown and a spreadsheet."
---

# Provenance and bulk import

Every snippet carries an optional `Source` and `ExternalId` (e.g. `BoldDesk Aug 2026`
and `13`). Together they give a snippet an identity in the system it came from, so a
later re-import of the same external export **updates snippets in place instead of
duplicating them** — the external system knows nothing about Klippy's own `Guid`.
Klippy's id is preserved on such a match, so it stays stable across re-imports.
Snippets created in Klippy leave both fields empty and continue to match on id alone.

`tools/` holds the BoldDesk pipeline:

```sh
# 1. markdown files + spreadsheet of titles -> a Klippy import file
python tools/bolddesk_to_klippy.py "<export folder>" out.json --source "BoldDesk Aug 2026" --tag support

# 2. merge it into the local store (same Merge path the in-app importer uses)
dotnet run --project tools/Importer -- out.json
```

Re-running both steps is safe: the second pass reports every snippet as *updated*
rather than adding duplicates. Pass the same `--tag` each time — a merge replaces the
whole snippet, so omitting it would blank the tag on snippets that already carry one.
