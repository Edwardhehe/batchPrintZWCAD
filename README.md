# 中望CAD批量打印插件

目标是生成可通过 `NETLOAD` 加载的测试 DLL。

## 命令

- `BPADD`：学习当前图中的图框块。选择块后，分别框选图名区域和图号区域，插件会保存到本地配置。
- `BPLOT`：打开批量打印窗口，扫描当前图和用户添加的 DWG 文件，按图号自然排序后输出 PDF。

## 配置

图框库默认保存到：

`%APPDATA%\ZwcadBatchPlot\TitleBlockLibrary.json`

用户偏好默认保存到：

`%APPDATA%\ZwcadBatchPlot\Settings.json`

批量打印窗口支持导入、导出图框库。

日志默认保存到：

`%APPDATA%\ZwcadBatchPlot\Logs`

## 当前约定

- 图框是普通块。
- 打印范围使用块几何外包框。
- 图名/图号区域使用用户框选矩形保存为块局部坐标。
- 文字自动兼容块属性、块内 `TEXT/MTEXT`、图框区域内外部 `TEXT/MTEXT`。
- 图幅识别支持 A0/A1/A2/A3 和加长图，比例由图框实际尺寸反推。
- PDF 命名规则为 `图号_图名.pdf`，非法文件名字符自动替换成 `_`，重名自动追加 `_1`。

## 已有窗口功能

- 扫描当前图、添加多个 DWG。
- 全选、全不选、反选、删除选中。
- 选择输出目录、PDF 打印机、CTB。
- 查看图号、图名、图幅、比例、实际尺寸、识别说明。
- 导出 CSV 识别清单。
- 打印时逐张记录成功/失败，不会因为某一张失败直接中断整批。

## 开发提示

如果中望 CAD 已经 `NETLOAD` 了 `bin\BatchPlotter.dll`，该文件会被 CAD 锁定。继续开发时可以先编译到临时目录：

```powershell
dotnet build -p:OutputPath=bin-dev\
```

要加载最新版 DLL，通常需要重启中望 CAD 后再 `NETLOAD`。
