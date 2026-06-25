# Repository Layout

This project keeps source code, shippable resources, and local build artifacts separate.

## Source and project files

- `src/Common/`: application and UI code compiled for both CAD platforms. Key files:
  - `BatchPlotCommands.cs`: command entry points, window scan, shared utilities (partial class).
  - `CoordinateUtils.cs`: `BuildWcsToDcsMatrix`, `BuildUcsToDcsMatrix`, `TransformPlotWindow` (partial class).
  - `SinglePlotCommands.cs`: single-plot dialog, printer/style selection (partial class).
  - `AddTitleBlockCommands.cs`: add-to-library wizard, dynamic block detection (partial class).
  - `BatchPlotForm.cs`, `RectangleBatchPlotForm.cs`: main batch-print UIs.
  - `TitleBlockScanner.cs`, `RectangleFrameScanner.cs`: DWG-scanning engines.
  - `TemporarySequenceOverlay.cs`: red frame + number overlay on CAD canvas.
  - `SinglePlotForm.cs`: single-plot confirmation panel with preview, paper, and output-path controls.
  - `Models.cs`: `PlotJob`, `TitleBlockDefinition`, `LocalRectangle`, `PaperDetection`.
  - `AppSettingsStore.cs`, `PaperSizeDetector.cs`, `FileNameSanitizer.cs`, etc.
- `src/ZWCAD/`: ZWCAD-specific API integration.
- `src/AutoCAD/`: AutoCAD-specific API integration and diagnostics.
- `docs/platform-architecture.md`: boundary between shared and platform-specific code.
- `*.csproj`: build targets for ZWCAD and AutoCAD variants (2016–2025 Core).
- `scripts/`: reusable project scripts, such as DLL builds.
- `installer/` and `installer_acad/`: installer helper scripts and user-facing install notes.

## Shippable resources

- `resources/acad/Plotters/`: bundled AutoCAD plotter resources copied beside the AutoCAD DLL output and installed by the plugin at load time.
- `resources/zwcad/Plotters/`: bundled ZWCAD plotter resources.
- `docs/`: project documentation.
- `ARCHITECTURE.md`: detailed architecture document with flow diagrams and Chinese annotations.
- `release-notes-*.md`: release notes that should stay in the repository.

## Generated or local-only artifacts

These are useful locally but should not be committed unless intentionally packaging a release:

- `bin/`, `bin-acad/`, `bin-acad2016/`–`bin-acad2019/`, `bin-acad-core/`: build outputs per platform.
- `obj/`: MSBuild intermediate files.
- `release/`: generated release packages (zip).
- `bin-tmp/`, `bin-new/`: temporary build outputs when `bin/` is locked by a running CAD instance.
- `dist/`: ad hoc handoff/test bundles.
- `local-artifacts/`: local validation logs, temporary probe projects, and backups kept for safety instead of being deleted.

Before removing anything from the generated/local-only group, check whether it is the latest DLL or plotter package currently being tested in CAD.
