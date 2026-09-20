---
icon: lucide/search
description: "Quick-codes, prefix search, ranking and the keyboard."
---

# Finding snippets

Two ways, both designed for speed:

- **Quick-codes** — a snippet can carry a short code (e.g. `slf` for "Send log files").
  Type the code in the search box and it jumps straight to the top; press Enter to copy.
- **Search** — every space-separated term must prefix-match a word anywhere in the
  label, content, or tag. `log fil` finds "…send us the **log** **fil**es…".
  Matching is case-insensitive; label hits rank above content hits, and recently used
  snippets rank first among ties.

The index is precomputed, lowercased words per snippet
([`SnippetSearch`](https://github.com/seankearon/klippy/blob/main/Klippy/Services/SnippetSearch.cs)), so a keystroke costs a linear
scan of ordinal `StartsWith` checks — microseconds for thousands of snippets.

**Keyboard (desktop):** type to filter, `↑`/`↓` to navigate, `Enter` to copy — or to
[run](running-things.md) a snippet marked for it, or
[what you typed](unmatched-search.md) when nothing matched at all —
`Ctrl/⌘+Enter` to copy one of those anyway, `F2` (or `Ctrl/⌘+I`) edit the selected snippet,
`Ctrl/⌘+N` new snippet, `Ctrl/⌘+D` duplicate the selected snippet, `Ctrl/⌘+F` focus
search, `Ctrl/⌘+P` toggle the preview pane, `Ctrl/⌘+E` export/import,
`Ctrl/⌘+,` settings, `↓` on an empty search box opens
[recent commands](recent-commands.md), `Esc`
clears/cancels, `Ctrl/⌘+Enter` saves in the editor. Clicking a row triggers it, the
same as `Enter`. Typing `quit` offers to [close Klippy](quitting.md).

> `F2` is the edit key everywhere else, and `Ctrl/⌘+I` is there for Mac keyboards, where
> `F2` is the brightness key unless the function-key setting says otherwise. Both open
> the editor on the selected snippet; in the clipboard history, where a clip has no
> editor of its own, they do nothing.

A **preview pane** at the bottom shows the full content of the selected snippet —
useful for long or multi-line entries that the one-line row preview truncates. It is
closed by default and toggles with `Ctrl/⌘+P`, the `preview` footer link, or a click on
its own header. Drag the splitter above it to trade height with the list; the pane
reopens at whatever height you left it. Row-level expansion (the chevron on multi-line
rows) still works independently.

The **edit dialog** is resizable: drag the grip in its bottom-right corner to give the
CONTENT field more room, and it reopens at that size. Clicking the dimmed area beside
it does nothing on purpose — leaving an edit takes `Esc` or Cancel, so a stray click
cannot discard it.

A snippet carries a single **tag**, and the editor shows the tags already in use under
the TAG field (EXISTING TAGS) so a snippet joins one of them rather than quietly coining
`wrk` beside `work` — a typo there costs a chip in the filter row and hides the snippet
from the tag it belonged to. Click a tag to fill the field; click the highlighted one
again to clear it, which is how a tagged snippet goes back to untagged without the
keyboard. Typing still creates a new tag: a part-typed entry narrows the chips to the
tags it could still become, and an entry that is already a tag shows the whole set again
so the next click can move the snippet elsewhere. A long list is capped at three rows and
scrolls, with the snippet's own tag scrolled into view. The chips sit out of the tab
order — the TAG box is the keyboard route, and tabbing through a dozen of them to reach
QUICK-CODE would cost more than they give.

Snippets can be **duplicated** — from a row's hover actions or `Ctrl/⌘+D` on desktop,
or via the Duplicate button in the edit overlay (the route on mobile: swipe → Edit →
Duplicate). A duplicate opens prefilled as a new snippet with " (copy)" appended to the
label; the quick-code is deliberately not copied so codes stay unique.
