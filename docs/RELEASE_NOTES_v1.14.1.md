# 批量打印插件 v1.14.1 发布说明

发布日期：2026-07-29

## 主要更新

- 修复不同 CAD 主菜单组名称造成的菜单加载兼容问题：中望优先使用 `ZWCAD`、AutoCAD 优先使用 `ACAD`，名称不可用时再回退默认菜单组；刷新时清理单个残留菜单，并在插入后验证菜单确实可见，避免把 `NETLOAD` 成功误判为菜单加载成功。
- 修复 AutoCAD 2027 中自定义纸张注册后仍可能无法用于当前打印会话的问题，并补充针对 PIA2 介质目录缓存的刷新处理。
- AutoCAD 2015–2027 统一使用发布包内经过验证的 PIA2 模板生成插件自有的 `LA_pdf`、`LA_png`、`LA_jpg`、`LA_dwf` 绘图仪及 PMP。
- 不再读取、转换或合并用户已有的 LA PIA 内容；CAD 自带 PC3 仅以只读方式提供驱动路径，避免改动用户绘图仪设置。
- 新增 PNG、JPG、DWF 的随包 PIA2 模板资源，并在生成后执行完整的 PIA2 解析和关联校验。
- 安装和卸载命令窗口无论成功或失败都会保留，便于用户查看错误信息和处理建议。
- 保持三个发布兼容组：ZWCAD、AutoCAD 2015–2024、AutoCAD 2025–2027。

## 兼容范围

| 平台 | DLL | 兼容范围 |
|------|-----|----------|
| ZWCAD | `BatchPlotter.dll` | ZWCAD Enterprise x64 |
| AutoCAD | `AcadBatchPlot.dll` | AutoCAD 2015–2024 |
| AutoCAD Core | `AcadBatchPlot.Core.dll` | AutoCAD 2025–2027 |

AutoCAD 2015–2024 全系列共用同一个 DLL；AutoCAD 2025–2027 全系列共用同一个 Core DLL。

## 本地发布包

- `ZwcadBatchPlot-v1.14.1.zip`
- `AcadBatchPlot-AutoCAD2015-2024-v1.14.1.zip`
- `AcadBatchPlot-AutoCAD2025-2027-v1.14.1.zip`

安装前请关闭 CAD，并完整解压对应压缩包后运行 `安装.cmd`。不要只复制主 DLL，以免遗漏依赖文件、安装脚本或绘图仪资源。
