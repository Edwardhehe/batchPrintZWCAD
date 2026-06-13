# Repository Layout

This project keeps source code, shippable resources, and local build artifacts separate.

## Source and project files

- `src/`: ZWCAD plugin source.
- `src_acad/`: AutoCAD plugin source.
- `*.csproj`: build targets for ZWCAD and AutoCAD variants.
- `scripts/`: reusable project scripts, such as DLL builds.
- `installer/` and `installer_acad/`: installer helper scripts and user-facing install notes.

## Shippable resources

- `resources/acad/Plotters/`: bundled AutoCAD plotter resources copied beside the AutoCAD DLL output and installed by the plugin at load time.
- `docs/`: project documentation.
- `release-notes-*.md`: release notes that should stay in the repository.

## Generated or local-only artifacts

These are useful locally but should not be committed unless intentionally packaging a release:

- `bin/`, `bin-acad/`, `bin-acad2019/`, `bin-acad-core/`: build outputs.
- `obj/`: MSBuild intermediate files.
- `release/`: generated release packages.
- `dist/`: ad hoc handoff/test bundles.
- `local-artifacts/`: local validation logs, temporary probe projects, and backups kept for safety instead of being deleted.

Before removing anything from the generated/local-only group, check whether it is the latest DLL or plotter package currently being tested in CAD.
