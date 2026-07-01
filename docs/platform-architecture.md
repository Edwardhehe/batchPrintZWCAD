# Platform architecture

The plugin uses one shared application layer with thin platform-specific CAD integrations.

## Shared code

Files under `src/Common/` are compiled into both the ZWCAD and AutoCAD assemblies. They contain:

- application settings, models, file naming, logging, CSV and PDF UI;
- paper-size detection and title-block management UI;
- batch-plot form and commands (split into partial class files: `BatchPlotCommands.cs`, `CoordinateUtils.cs`, `SinglePlotCommands.cs`, `AddTitleBlockCommands.cs`);
- title-block scanning, rectangle-frame scanning, sequence overlays, directory tables and DWG splitting;
- UCS-to-DCS coordinate transform utilities shared by single plot, rectangle batch, and title block batch.

Shared files that reference CAD types use only small `ZWCAD` / `AUTOCAD` conditional import blocks. Their behavior and call paths remain identical across platforms.

## Platform-specific code

The following files remain under both `src/ZWCAD/` and `src/AutoCAD/` because their runtime behavior or available APIs differ materially:

| File | Reason to keep platform-specific |
| --- | --- |
| `AcadPlotterInstaller.cs` | Plotter discovery and bundled PC3/PC5 installation paths differ. |
| `AutoloadManager.cs` | ZWCAD and AutoCAD use different registry trees and application registration rules. |
| `CadMenuInstaller.cs` | Menu APIs and AutoCAD Core behavior differ. |
| `CadTextExtractor.cs` | Dynamic/nested block behavior and compatibility workarounds differ between host APIs. |
| `CadTextUpdater.cs` | Document activation and entity update behavior differ, especially in AutoCAD Core. |
| `PlotterService.cs` | Plot engines, media validation, document opening and Core Console support differ substantially. Includes `IsDcsWindow` and `PrepareEditorViewForPlot` guards for UCS-aware printing. |
| `TitleBlockLibraryStore.cs` | AutoCAD additionally imports/migrates the existing ZWCAD library. |

## Build constants

- `BatchPlotter.csproj` defines `ZWCAD`.
- AutoCAD projects define `AUTOCAD`.
- `AcadBatchPlot.Core.csproj` additionally defines `ACAD_CORE`.

## PianNoCN integration

Files under `src/PianNoCN/` provide PIA 2.0 compressed-format serialization for AutoCAD PMP/PC3 files. They are compiled into AutoCAD assemblies (AcadBatchPlot, AcadBatchPlot.AutoCAD2019, AcadBatchPlot.Core) but not into the ZWCAD assembly, which uses INI-format PMP instead.

The PianNoCN code (namespace `PiaNO`) handles:
- `PiaFile` / `PiaNode` / `PiaHeader`: tree-based node model for PIA 2.0 binary files.
- `PiaSerializer`: deflate decompression of the PIA 2.0 payload into text nodes.
- `PlotterConfiguration`: typed access to PC3 plotter configuration metadata.

The original upstream source lives in `lib/PianNoCN/` for reference; the compiled copy is at `src/PianNoCN/`.

## New features (v1.10.1+)

### Single-plot custom paper size

When the user's selected region does not match any standard paper size, `PaperSizeDetector.GuessScale()` infers the most likely integer scale. A `CustomScaleForm` dialog lets the user adjust the scale, and `PmpCustomPaper.RegisterCustomPaper()` writes a custom paper entry into the LA_pdf.pmp file before plotting. `PmpCustomPaper.RemoveCustomPaper()` cleans up the entry in a `finally` block after the plot completes.

PMP write/cleanup supports three formats:
- **PIA 3.0 JSON** (AutoCAD 2024+): JSON-with-header format, parsed/modified via Newtonsoft.Json.
- **PIA 2.0 compressed** (AutoCAD 2019-2023): PianNoCN-based tree manipulation, then re-serialized.
- **ZWCAD INI**: `[Meta]/[user]` section-based text format, handled with regex.

`PmpPiaConverter.IsCadPia3Compatible()` detects the target AutoCAD's PMP format at runtime. `PmpPiaConverter.ConvertToPia2()` converts PIA 3.0 resources to PIA 2.0 for older AutoCAD versions.

### XCLIP filtering

Both `CadTextExtractor.BuildOwnerTextCache()` (both platforms) and `RectangleFrameScanner.CollectEntityRectangles()` skip block references that have an XCLIP boundary. Detection is via `IsBlockClipped()`, which checks the block reference's extension dictionary for an `"ACAD_FILTER"` entry. XCLIPped blocks display only a portion of their content, so internal rectangles or text extracted from them would be incomplete and are excluded from scanning.

### Empty-frame filtering

`RectangleFrameScanner.FilterEmptyRectangles()` checks each candidate rectangle for actual drawing content. `HasDrawingContent()` recursively traverses all entities (including nested blocks) within the rectangle's bounding box; if an entity's `GeometricExtents` intersects the target rectangle and is not the original Polyline frame itself, the rectangle is considered non-empty. Frames without content (blank title blocks, empty layouts) are excluded from results.

### Rectangle-frame multi-layout with TabOrder

`RectangleFrameScanner.ScanScope()` collects rectangles from all matching layouts while recording each layout's `TabOrder`. After collection, spaces are sorted by TabOrder (model space first, then paper layouts in tab order). Within `RectangleBatchPlotForm`, results are grouped by layout (preserving TabOrder), and each group is further sorted spatially by the user's chosen direction (top-to-bottom/left-to-right or left-to-right/top-to-bottom). The overlay numbering follows the same sort order.

### Rectangle batch print form-first UX

Unlike the title-block batch flow (scan then auto-open results), `ZBP_RECTANGLE_BATCH_PLOT` opens the `RectangleBatchPlotForm` immediately. The user then explicitly triggers scanning via "Scan current drawing" (with a scope dialog for layout selection) or "Box select scan" (for window-based area selection). A "Re-identify" button repeats the last scan. This gives the user control over scanning scope and timing.

## Key design decisions (v1.10.0)

### UCS coordinate transform chain

All three print modes (single plot, rectangle batch, title block batch) follow the same pattern:

1. **Collection**: scanner/editor returns WCS coordinates.
2. **Transform**: `BuildWcsToDcsMatrix` (or `BuildUcsToDcsMatrix` for single plot) builds the view-aware DCS matrix.
3. **4-corner → DCS**: actual corner points (not bounding-box corners) are transformed to DCS in one pass, avoiding double expansion.
4. **IsDcsWindow flag**: tells `GetPlotWindow` to skip its own WCS→DCS, and `PrepareEditorViewForPlot` to preserve the user's rotated view.

### Dynamic block visibility

`entity.Visible` (CAD engine native) is used exclusively to determine which nested blocks are active. No name guessing, no layer-based inference.

### Rectangle scanner filter pipeline

1. Window intersection → 2. Per-definition largest → 3. Deduplication → 4. Paper size match. Geometric rectangle detection (diagonal equality + midpoint coincidence) replaces axis-alignment check for UCS-rotated rectangles.

Use `scripts/build-dll.ps1 -Target All -Configuration Release` after changing shared or platform code.
