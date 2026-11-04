# Windlass brand

| File | What it is |
|---|---|
| `emblem-light.png`, `emblem-dark.png` | The full emblem (Fleet frame, the windlass hauling its anchor chain, wordmark), 1024 px, **transparent background** (the engraving's cream and night-blue grounds were removed; the circle's inner sky stays). Light is for light backgrounds (dark-blue wordmark), dark is for dark ones (ivory wordmark) |
| `mark.svg` | Icon-only mark, simplified to read at 32 px: the windlass drum between its cheeks, with the chain led off it |
| `mark-256.png` | The mark as PNG |
| `favicon.ico` | Icon (16, 32, 48, 256). `tray.ico`: the same mark at 16, 20, 24 and 32 for the notification-area icon (`src/Tray.ps1`) |

The emblem follows `UNIFIED-UI.md` section 8: the Scrimshaw frame (dark-blue field, thin brass ring, compass-point
rays at 3 and 9 o'clock) around the app's own subject, a ship's windlass hauling its anchor chain, which breaks out
of the circle at the lower right.

Regenerate the mark and the favicon with `powershell -File build\New-WindlassBrand.ps1` (needs Edge or Chrome on the build machine).
