Put the AutoCAD PDF plotter package here before building a release:

- LA_pdf.pc3
- PMP Files/LA_pdf.pmp

Files in this folder are copied to the build output under `Plotters/`.
When the plugin loads, it copies `Plotters/LA_pdf.pc3` and
`Plotters/PMP Files/LA_pdf.pmp` into the user's AutoCAD Plotters directory,
then the batch print panel prefers `LA_pdf.pc3` in the device list.
