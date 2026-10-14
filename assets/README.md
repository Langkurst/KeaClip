# Original KeaClip kea

`kea.svg` is the original, editable vector artwork created for this project and the single
source of truth for the mascot. No third-party artwork or image model was used.

- **Generated icons:** `GenerateIcons.ps1` reads `kea.svg` and writes deterministic PNGs at
  16, 20, 24, 32, 48, 64, 128 and 256 px (`kea-*` on the dark tile, `kea-light-*`,
  white-on-transparent `kea-mono-*`), the multi-resolution `kea.ico` and a copy of `kea.svg`
  into `src/Klippi.Desktop/Assets`. Run it from the repository root with PowerShell 7 on
  Windows: `pwsh -File assets/GenerateIcons.ps1`. The same input always produces
  byte-identical files (offline System.Drawing, no fonts). For a scratch preview pass
  `-OutputDirectory artifacts/<folder>`; that run never touches the browser-helper icons.
- **In the app:** `src/Klippi.Desktop/Controls/KeaMascot.cs` reads the shipped copy of
  `kea.svg` and draws the same artwork as vector shapes at any size, in the dark or light
  palette, with an optional tile and a `Carrying` pose (the kea holds a small download card).
  There is no second copy of the geometry to keep in sync.
- **Keeping them in sync:** `tests/Klippi.UiTests/BrandTests.cs` fails if the shipped SVG copy
  is stale, if the control's rendering drifts from the generated icons, or if an output's
  name, size, ICO frame or key colours change. It also checks the small-size hinting and the
  carried card at the sizes where they matter.

## Editing the artwork

1. Edit `kea.svg`. The first element is the tile `<rect id="tile">`; after it come `path` and
   `circle` shapes in paint order, each with an `id` and a `fill`. Paths must use absolute
   `M L H V C Q Z` commands only, and groups, transforms and strokes are rejected, so a
   re-save from an SVG editor fails loudly instead of silently dropping parts.
   - `fill` is the dark-variant colour, `data-light-fill` the light one.
   - `data-mono` is `ink` or `hole` for the monochrome icon (`data-mono-gap` carves a thin
     separating outline, from `data-mono-gap-min-size`).
   - `data-min-size` / `data-below-size` limit a detail to pixel sizes `min <= px < below`,
     which keeps 16–32 px icons clean. A small-size variant (for example the pixel-snapped
     `eye-16`) also carries `display="none"`, so a plain SVG viewer shows exactly the
     full-size artwork.
2. Shapes inside the one `<g data-pose="carrying" data-origin="x y" display="none">` belong to
   the `KeaMascot` pose only and never appear in the icons. `data-origin` is the point the card
   grows from, and `data-token` names the app theme brush the control uses instead of `fill`.
3. Run the generator, then the UI tests. With `KEACLIP_UI_EVIDENCE_DIRECTORY` set, `BrandTests`
   also saves before/after sheets (`brand-compare-*.png`) and in-context renders
   (`brand-mascot-*.png`); point `KEACLIP_BRAND_BASELINE_DIRECTORY` at a folder of the previous
   PNGs to compare against them.

Native Windows tray/taskbar rendering at 100–200% scaling still needs a direct check on a
real desktop; the headless tests cannot show what the Windows shell does with the icon.
