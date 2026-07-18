# 批量打印插件 v1.13 发布说明

发布日期：2026-07-18

## 主要更新

- 新增并完善 PDF、PNG、JPG、DWF、DWG 多格式批量输出。
- PNG/JPG 改为严格使用插件自有 `LA_png` / `LA_jpg` 绘图仪，不使用 CAD 自带设备兜底。
- 预览与正式输出统一使用当前选择的格式设备，避免预览固定走 PDF。
- 修复 AutoCAD PNG/JPG 批量打印及预览在 A0 等纸张下出现 `eInvalidInput` 的问题。
- AutoCAD 栅格绘图仪按设备 DPI 在毫米图幅和像素介质之间换算，自动生成 PIA2/PIA3 兼容纸张表。
- ZWCAD 栅格绘图仪使用插件自有 PC5/PMP，并在安装后刷新设备和纸张列表。
- 重构文件命名设置，支持字段任意排列以及可配置的起始序号、补零位数和自动位数。
- 更新架构文档和用户使用说明，明确 AutoCAD 两个 DLL 兼容组。

## 兼容范围

| 平台 | DLL | 兼容范围 |
|------|-----|----------|
| ZWCAD | `BatchPlotter.dll` | ZWCAD Enterprise x64 |
| AutoCAD | `AcadBatchPlot.dll` | AutoCAD 2015–2024 |
| AutoCAD Core | `AcadBatchPlot.Core.dll` | AutoCAD 2025–2027 |

AutoCAD 2015–2024 全系列共用同一个 DLL；AutoCAD 2025–2027 全系列共用同一个 Core DLL。

## 本地发布包

- `ZwcadBatchPlot-v1.13.zip`
- `AcadBatchPlot-AutoCAD2015-2024-v1.13.zip`
- `AcadBatchPlot-AutoCAD2025-2027-v1.13.zip`

安装前请关闭 CAD，并完整解压对应压缩包后运行 `安装.cmd`。不要只复制主 DLL，以免缺少依赖文件和绘图仪资源。
