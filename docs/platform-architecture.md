# Platform architecture

The plugin uses one shared application layer with thin platform-specific CAD integrations.

## Shared code

Files under `src/Common/` are compiled into both the ZWCAD and AutoCAD assemblies. They contain:

- application settings, models, file naming, logging, CSV and PDF UI;
- paper-size detection and title-block management UI;
- batch-plot form and commands;
- title-block scanning, sequence overlays, directory tables and DWG splitting.

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
| `PlotterService.cs` | Plot engines, media validation, document opening and Core Console support differ substantially. |
| `TitleBlockLibraryStore.cs` | AutoCAD additionally imports/migrates the existing ZWCAD library. |

`src/AutoCAD/ScanDiagnostics.cs` is AutoCAD-only diagnostic tooling.

## Build constants

- `BatchPlotter.csproj` defines `ZWCAD`.
- AutoCAD projects define `AUTOCAD`.
- `AcadBatchPlot.Core.csproj` additionally defines `ACAD_CORE`.

Use `scripts/build-dll.ps1 -Target All -Configuration Release` after changing shared or platform code.
