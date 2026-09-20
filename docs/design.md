---
icon: lucide/palette
description: "The visual language, and the fonts it is built on."
---

# Design

`design_handoff_klippy/` holds the high-fidelity design reference (HTML mock + README
with the token sheet). The Avalonia theme mirrors it: tokens live in
[`App.axaml`](https://github.com/seankearon/klippy/blob/main/Klippy/App.axaml), control styles in
[`Styles/KlippyStyles.axaml`](https://github.com/seankearon/klippy/blob/main/Klippy/Styles/KlippyStyles.axaml), icons as
`StreamGeometry` in [`Styles/Icons.axaml`](https://github.com/seankearon/klippy/blob/main/Klippy/Styles/Icons.axaml).

## Fonts

Chivo and Chivo Mono (OFL) are bundled under `Klippy/Assets/Fonts`. The static TTFs
shipped by the foundry carry per-weight family names ("Chivo SemiBold"), which breaks
weight-based matching in Avalonia/Skia. `tools/fontfix.cs` rewrites the name tables so
all weights share one family; the committed fonts are already normalized. If you ever
replace them, re-run:

```sh
dotnet run tools/fontfix.cs -- Klippy/Assets/Fonts
```
