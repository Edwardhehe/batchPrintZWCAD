# ZwcadBatchPlot / AcadBatchPlot v1.5.0

本版新增 AutoCAD 2021 及以后版本的分平台发布包，并保留中望 CAD 发布包。

## 新增

- 新增 `AcadBatchPlot.dll`，用于 AutoCAD 2021 ~ 2024 x64，目标框架为 .NET Framework 4.8。
- 新增 `AcadBatchPlot.Core.dll`，用于 AutoCAD 2025 及以后 x64，目标框架为 .NET 8。
- AutoCAD 安装脚本可自动识别 `AcadBatchPlot.dll` 或 `AcadBatchPlot.Core.dll`。
- README 增加 CAD 平台和 DLL 对应关系。

## 适用包

- `ZwcadBatchPlot-v1.5.0.zip`：中望 CAD 版本，加载 `BatchPlotter.dll`。
- `AcadBatchPlot-AutoCAD2021-2024-v1.5.0.zip`：AutoCAD 2021 ~ 2024，加载 `AcadBatchPlot.dll`。
- `AcadBatchPlot-AutoCAD2025Plus-v1.5.0.zip`：AutoCAD 2025 及以后，加载 `AcadBatchPlot.Core.dll`。

## 说明

- AutoCAD 2025 及以后如果菜单栏未显示，可执行 `ZBP_SHOW_PANEL` 打开主界面。
- AutoCAD 2025+ 包已包含 WebView2、Newtonsoft.Json 和 PDFsharp 依赖；PDFsharp 在 .NET 8 下有兼容性警告，PDF 工具建议重点测试。
