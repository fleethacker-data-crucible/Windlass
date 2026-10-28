# Windlass brand

| File | What it is |
|---|---|
| `emblem-light.png`, `emblem-dark.png` | Full emblem (Fleet frame and wordmark), 1024 px, transparent background; the wordmark is dark blue on light and ivory on dark. **Placeholder**, rendered from `emblem-source.html` until the detailed original is drawn |
| `emblem-source.html` | Source of the placeholder emblem. Delete it when the detailed original replaces the PNGs |
| `mark.svg` | Icon-only mark, simplified to read at 32 px: the windlass drum between its cheeks, with the chain led off it |
| `mark-256.png` | The mark as PNG |
| `favicon.ico` | Icon (16, 32, 48, 256). Windlass has no tray icon, so there is no `tray.ico` |

The emblem follows `UNIFIED-UI.md` section 8: the Scrimshaw frame (dark-blue field, thin brass ring, compass-point
rays at 3 and 9 o'clock) around the app's own subject, a ship's windlass hauling its anchor chain, which breaks out
of the circle at the lower right.

Regenerate everything with `powershell -File build\New-WindlassBrand.ps1` (needs Edge or Chrome on the build machine).
