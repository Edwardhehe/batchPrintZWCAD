# ZwcadBatchPlot / AcadBatchPlot v1.5.2

本版本是 AutoCAD 菜单热修版，同时包含使用教程网页。

## 修复

- 修复 AutoCAD 2019 ~ 2024 点击菜单后提示 `未知命令 "^C^CZBP_..."` 的问题。
- AutoCAD 2019 ~ 2024 菜单项现在直接发送插件命令，不再把 `^C^C` 当作普通文本传给命令行。

## 文档

- 新增图文使用教程网页：`docs/tutorial.html`。
- README 增加 AutoCAD 菜单异常排查说明。

## 升级提示

如果已经安装过 v1.5.0 或 v1.5.1，旧菜单项可能仍保存在 AutoCAD 菜单栏里。升级到 v1.5.2 后建议：

1. 关闭 AutoCAD。
2. 解压对应版本发布包并双击 `安装.cmd`。
3. 重新打开 AutoCAD。
4. 如果菜单仍然异常，执行一次 `ZBP_RELOAD_MENU`，或在菜单中点击“刷新菜单”。

## 当前发布包

- `ZwcadBatchPlot-v1.5.2.zip`：中望 CAD 版本，加载 `BatchPlotter.dll`。
- `AcadBatchPlot-AutoCAD2019-2020-v1.5.2.zip`：AutoCAD 2019 ~ 2020，加载 `AcadBatchPlot.dll`。
- `AcadBatchPlot-AutoCAD2021-2024-v1.5.2.zip`：AutoCAD 2021 ~ 2024，加载 `AcadBatchPlot.dll`。
- `AcadBatchPlot-AutoCAD2025Plus-v1.5.2.zip`：AutoCAD 2025 及以后，加载 `AcadBatchPlot.Core.dll`。
