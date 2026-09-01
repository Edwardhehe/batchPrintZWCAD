# 批量打印插件 v1.15.6.4 发布说明

本版本覆盖 ZWCAD、AutoCAD 2015–2024 和 AutoCAD 2025–2027。重点改进中望打印机目录解析（含建筑版），并减少完好绘图仪配置的重复覆盖。

## 中望打印机目录

- 安装 `LA_pdf` 等配置时，读取选项中的全部打印机配置搜索路径（`PrinterConfigPath` / `PrinterConfigDir`），不要求必须是第一项。
- 只要当前产品默认 `ROAMABLEROOTPREFIX\Plotters` 仍在搜索路径中且目录存在，优先写入该目录（建筑版对应 `ZwArch` / `ZWCADA` 等产品根）。
- 保留已有完好 PC5/PMP 时，仅校正 `pmp_filepath` 绝对路径关联，不覆盖用户已注册的自定义纸张。
- 可读取 `PrinterDescPath` / `PrinterDescDir`，与打印机说明文件搜索路径对齐。

## 绘图仪安装与刷新

- AutoCAD / 中望：完好 `LA_*` 配置跳过模板重装；仅缺失、损坏或关联需修正时写盘。
- 设备列表按会话按需刷新，避免每次开窗强制刷新。
- 自定义纸在打印/预览前整批准备，需要时统一刷新介质，不再按 CAD 版本特判。

## 发布包

- `LA批打印-ZWCAD-v1.15.6.4.zip`
- `LA批打印-AutoCAD2015-2024-v1.15.6.4.zip`
- `LA批打印-AutoCAD2025-2027-v1.15.6.4.zip`
