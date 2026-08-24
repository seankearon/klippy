# Handoff: Klippy — Clipboard Snippet Manager

## Overview
Klippy is a cross-platform snippet manager (.NET Avalonia UI, targeting Windows, macOS, iOS, Android). The user keeps a list of saved text snippets and copies any of them to the system clipboard with one click/tap. The design covers two form factors: a compact desktop launcher-style window and a mobile screen.

## About the Design Files
The files in this bundle are **design references created in HTML** — static mockups showing intended look and behavior, not production code. The task is to **recreate these designs in Avalonia UI** (XAML + styles) using its control set (TextBox, ListBox/ItemsRepeater, Button, etc.). The HTML is only the visual spec; nothing in it ships.

- `Klippy.dc.html` — the design source. Desktop and mobile mocks side by side. Device chrome (macOS titlebar, iPhone bezel/status bar) comes from `macos-window.jsx` / `ios-frame.jsx` and is NOT part of the app — implement only the content inside.

## Fidelity
**High-fidelity.** Colors, typography, spacing, and states are final. Recreate pixel-perfectly with Avalonia equivalents.

## Design Tokens

Colors (dark theme only):
- App background: `#16181D`
- Raised surface / input background: `#1D2027`
- Hover row background: `#1A1C22`
- Selected row background: `#22242C` (also toast + icon-button background)
- Icon button hover surface: `#262A33`
- Border, strong: `#2B2F39` (inputs, chips)
- Border, hairline: `#23262E` (section dividers), `#1D2027` (row dividers)
- Code block background: `#12141A`
- Text, primary: `#E8EAEF`
- Text, secondary (previews): `#7A8090`; selected-row preview: `#B8BDC9`
- Text, muted (tags, hints, placeholder): `#5A5F6B`, `#6A7080`, `#9AA0AD`
- Accent (amber): `#F5A524`; accent hover: `#FFC35C`; text on accent: `#16181D`
- Destructive (delete): `#E0665C`

Typography:
- UI font: **Chivo** (400/500/600/700). Snippet content, tags, keyboard hints, chips: **Chivo Mono** (400/500). Both on Google Fonts; bundle for offline.
- Desktop: row label 12px/600; snippet preview 11px mono; tag 10px mono; chips 11px mono; footer hints 10px mono; search text 12px mono.
- Mobile: screen title 20px/700 (letter-spacing -0.02em); row label 14px/600; preview 12px mono; chips 12px mono; search placeholder 13px.

Spacing & shape:
- Desktop row padding: 9px 14px; mobile row: 12px 16px, min-height 44px (touch target).
- Radii: inputs/buttons 6px (desktop), 8px (mobile); code block 6px; chips fully rounded (999px); icon buttons 5px (24px square desktop, 38px square mobile).
- Gaps: 12px between row text and trailing items; 6px between chips; 10px search-bar internals 8px.

## Screens

### 1. Desktop launcher (680×560 window)
Single-column, top-to-bottom:

1. **Filter bar** (padding 12px 14px, bottom hairline #23262E)
   - Search input (flex:1): #1D2027 bg, 1px #2B2F39 border, r6, padding 7px 10px. Magnifier icon (13px, #5A5F6B stroke), typed text in mono 12px, amber caret (1×14px), right-aligned "⌘F" key hint (10px mono, 1px #2B2F39 border, r4, padding 1px 5px).
   - **New button**: amber #F5A524 bg, #16181D text, r6, padding 7px 12px, "+ New" 12px/600. Hover: #FFC35C.
2. **Tag chips row** (padding 10px 14px, bottom hairline): "All" selected (amber bg, dark text), then work / dev / personal / banking (1px #2B2F39 border, #9AA0AD text). 11px mono, padding 3px 9px, pill.
3. **Snippet list** (fills remaining height). Each row: label + one-line ellipsized preview (mono) left; tag name (10px mono #6A7080) and a copy icon (13px, #5A5F6B) right; 1px #1D2027 divider.
   - **Selected row (keyboard focus)**: #22242C bg, 2px amber left border, label in amber, preview brightened to #B8BDC9, trailing "↵ copy" badge (10px mono amber text, 1px amber border, r4, padding 1px 6px).
   - **Hover row**: #1A1C22 bg; trailing icons replaced by three 24px icon buttons — edit (pencil, #9AA0AD on #262A33), delete (trash, #E0665C on #262A33), copy (amber bg, dark icon).
   - **Expanded multi-line row**: header line (label + tag + chevron-up) then a code block: mono 11px, line-height 1.6, #9AA0AD text on #12141A, 1px #23262E border, r6, padding 9px 11px, preserves line breaks.
4. **Toast** (transient, centered above footer): "Copied to clipboard" with amber check icon; #22242C bg, 1px amber border, r6, padding 6px 12px, 11px text. Shown ~1.5s after any copy.
5. **Footer** (padding 8px 14px, top hairline): left "↑↓ navigate  ↵ copy  ⌘N new  ⌘F filter", right snippet count. 10px mono #5A5F6B.

### 2. Mobile (iOS / Android)
1. **Header** (below system status bar): "Klippy" 20px/700 left; 32px amber square (r8) "+" button right.
2. **Search field**: full-width, #1D2027 bg, 1px #2B2F39 border, r8, padding 10px 12px, magnifier + "Filter snippets" placeholder.
3. **Tag chips**: same style as desktop at 12px mono, padding 5px 12px, horizontally scrollable.
4. **List** (top hairline #23262E): rows ≥44px tall; label 14px/600 + mono preview; trailing 38px copy button (r8, #22242C bg, amber copy icon).
   - **Swipe-left row**: content shifts left revealing "Edit" (64px, #2B2F39) and "Delete" (64px, #E0665C, white text) actions.
5. **Toast**: same as desktop, bottom-centered above the home indicator.

## Interactions & Behavior
- **Copy on click/tap**: clicking a desktop row (or the amber copy button) or tapping a mobile row's copy button puts the full snippet text on the clipboard and shows the toast (~1.5s, fade).
- **Filter**: typing filters rows live (match label + content, case-insensitive). ⌘F/Ctrl+F focuses the field. Active tag chip further scopes the list.
- **Keyboard navigation (desktop)**: ↑/↓ moves selection, Enter copies the selected snippet, ⌘N/Ctrl+N opens new-snippet entry, Esc clears the filter. Selection wraps optional.
- **Add / edit / delete**: "+ New" and pencil open a snippet editor (label, multi-line content, tag) — not mocked; keep it in the same visual language. Delete asks for confirmation.
- **Multi-line snippets**: chevron expands/collapses the content block in place; copy always copies full content.
- **Tags**: single-select chips; "All" resets.

## State Management
- `snippets: [{ id, label, content, tag }]` persisted locally.
- `filterText`, `activeTag`, `selectedIndex` (desktop keyboard nav), `expandedId`, transient `toastVisible`.

## Assets
No image assets. Icons are simple 2px-stroke line glyphs (magnifier, copy = two overlapping rounded rects, pencil, trash, check, chevron) — use an Avalonia icon library equivalent (e.g. Lucide/Feather set).

## Files
- `Klippy.dc.html` — full design source (both form factors, all states).
- `macos-window.jsx`, `ios-frame.jsx` — device chrome only, ignore for implementation.
