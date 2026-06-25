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

`src/AutoCAD/ScanDiagnostics.cs` is AutoCAD-only diagnostic tooling.

## Build constants

- `BatchPlotter.csproj` defines `ZWCAD`.
- AutoCAD projects define `AUTOCAD`.
- `AcadBatchPlot.Core.csproj` additionally defines `ACAD_CORE`.

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
