# Repository Layout

This project keeps source code, shippable resources, and local build artifacts separate.

## Source and project files

- `src/Common/`: application and UI code compiled for both CAD platforms. Key files:
  - `BatchPlotCommands.cs`: command entry points, window scan, shared utilities (partial class).
  - `CoordinateUtils.cs`: `BuildWcsToDcsMatrix`, `BuildUcsToDcsMatrix`, `TransformPlotWindow` (partial class).
  - `SinglePlotCommands.cs`: single-plot dialog, printer/style selection, custom paper size flow (partial class).
  - `AddTitleBlockCommands.cs`: add-to-library wizard, dynamic block detection (partial class).
  - `BatchPlotForm.cs`, `RectangleBatchPlotForm.cs`: main batch-print UIs.
  - `TitleBlockScanner.cs`, `RectangleFrameScanner.cs`: DWG-scanning engines.
  - `TemporarySequenceOverlay.cs`: red frame + number overlay on CAD canvas.
  - `SinglePlotForm.cs`: single-plot confirmation panel with preview, paper, and output-path controls.
  - `Models.cs`: `PlotJob`, `TitleBlockDefinition`, `LocalRectangle`, `PaperDetection`.
  - `AppSettingsStore.cs`, `PaperSizeDetector.cs` (includes `GuessScale`), `FileNameSanitizer.cs`, etc.
  - `PmpCustomPaper.cs`: PMP custom paper registration/removal (PIA 3.0 JSON / PIA 2.0 / ZWCAD INI).
  - `PmpPiaConverter.cs`: PIA version detection and PIA 3.0-to-PIA 2.0 conversion.
  - `CustomScaleForm.cs`: integer-scale selection dialog for non-standard paper sizes.
  - `PdfDocumentService.cs`: PDF merge and validation (PDFsharp).
- `src/ZWCAD/`: ZWCAD-specific API integration.
- `src/AutoCAD/`: AutoCAD-specific API integration and diagnostics.
- `src/PianNoCN/`: PIA 2.0 file format serialization (namespace `PiaNO`). Compiled only into AutoCAD projects.
  - `Pia/PiaFile.cs`, `PiaNode.cs`, `PiaHeader.cs`, `PiaSerializer.cs`, `PiaException.cs`, `EnumDecompressionType.cs`
  - `Plot/PlotterConfiguration.cs`, `Media.cs`
- `docs/platform-architecture.md`: boundary between shared and platform-specific code.
- `*.csproj`: build targets for the four projects (see table below).
- `scripts/`: reusable project scripts, such as DLL builds.
- `installer/` and `installer_acad/`: installer helper scripts and user-facing install notes.

## Projects

| .csproj | Platform | Target | Output |
|---------|----------|--------|--------|
| `BatchPlotter.csproj` | ZWCAD | net48 | `bin\BatchPlotter.dll` |
| `AcadBatchPlot.csproj` | AutoCAD 2019-2024 | net48 | `bin-acad\AcadBatchPlot.dll` |
| `AcadBatchPlot.AutoCAD2019.csproj` | AutoCAD 2019 | net47 | `bin-acad2019\AcadBatchPlot.dll` |
| `AcadBatchPlot.Core.csproj` | AutoCAD 2025+ Core | net8.0-windows | `bin-acad-core\AcadBatchPlot.Core.dll` |

AutoCAD 2016-2018 projects have been removed; minimum supported AutoCAD version is 2019.

## Shippable resources

- `resources/acad/Plotters/`: bundled AutoCAD plotter resources. Contains `PIA2/` and `PIA3/` subdirectories:
  - `PIA3/LA_pdf.pc3` and `PIA3/PMP Files/LA_pdf.pmp` — PIA 3.0 JSON format (AutoCAD 2024+).
  - `PIA2/LA_pdf.pc3` and `PIA2/PMP Files/LA_pdf.pmp` — PIA 2.0 compressed format (AutoCAD 2019-2023).
  - The installer (`AcadPlotterInstaller`) copies the appropriate version based on host AutoCAD's PIA compatibility.
- `resources/zwcad/Plotters/`: bundled ZWCAD plotter resources (INI format).
- `docs/`: project documentation.
- `ARCHITECTURE.md`: detailed architecture document with flow diagrams and Chinese annotations.
- `release-notes-*.md`: release notes that should stay in the repository.

## Generated or local-only artifacts

These are useful locally but should not be committed unless intentionally packaging a release:

- `bin/`, `bin-acad/`, `bin-acad2019/`, `bin-acad-core/`: build outputs per platform.
- `obj/`: MSBuild intermediate files.
- `release/`: generated release packages (zip).
- `bin-tmp/`, `bin-new/`: temporary build outputs when `bin/` is locked by a running CAD instance.
- `dist/`: ad hoc handoff/test bundles.
- `local-artifacts/`: local validation logs, temporary probe projects, and backups kept for safety instead of being deleted.

Before removing anything from the generated/local-only group, check whether it is the latest DLL or plotter package currently being tested in CAD.
