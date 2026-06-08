# ZwcadBatchPlot / AcadBatchPlot v1.5.1

本版新增 AutoCAD 2019 ~ 2020 专用发布包。

## 新增

- 新增 `AcadBatchPlot-AutoCAD2019-2020-v1.5.1.zip`。
- 新增 `AcadBatchPlot.AutoCAD2019.csproj`，目标框架为 `.NET Framework 4.7`。
- AutoCAD 2019 ~ 2020 包引用 `AutoCAD.NET 23.0.0`，输出 DLL 为 `AcadBatchPlot.dll`。

## 当前发布包

- `ZwcadBatchPlot-v1.5.1.zip`：中望 CAD 版本，加载 `BatchPlotter.dll`。
- `AcadBatchPlot-AutoCAD2019-2020-v1.5.1.zip`：AutoCAD 2019 ~ 2020，加载 `AcadBatchPlot.dll`。
- `AcadBatchPlot-AutoCAD2021-2024-v1.5.1.zip`：AutoCAD 2021 ~ 2024，加载 `AcadBatchPlot.dll`。
- `AcadBatchPlot-AutoCAD2025Plus-v1.5.1.zip`：AutoCAD 2025 及以后，加载 `AcadBatchPlot.Core.dll`。

## 说明

- AutoCAD 2019 ~ 2024 都是 .NET Framework 插件，但分别按 2019/2020 的 `.NET 4.7 + AutoCAD.NET 23.0.0` 和 2021/2024 的 `.NET 4.8` 单独发布。
- AutoCAD 2025+ 包仍为 .NET 8；PDFsharp 在 .NET 8 下有兼容性警告，PDF 工具建议重点测试。
