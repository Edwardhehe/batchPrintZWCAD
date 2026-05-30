# 中望 CAD 批量打印插件

一个用于中望 CAD 2025 的 .NET 批量打印插件。插件可以学习图框块，识别图名、图号、图幅、比例，并按 `图号_图名.pdf` 批量输出 PDF。

## 功能

- 图框信息库：新增、编辑、删除、导入、导出图框定义。
- 图框识别：按块名匹配图框，支持用户框选图名、图号和打印外边界。
- 图幅识别：支持 A0、A1、A2、A3 及加长图。
- 比例识别：支持常见比例，如 1:1、1:10、1:100。
- 跨文件批量打印：可添加多个 DWG，按图号自然排序后输出。
- PDF 命名：默认 `图号_图名.pdf`，非法文件名字符自动替换为 `_`。
- 重名处理：默认覆盖，也可在设置中改为自动追加序号。
- 图纸修正：支持在表格中修改图名、图号，并同步回当前打开的 CAD 图纸。
- 自动加载：可安装为中望 CAD 启动自动加载，也支持卸载。

## 使用

### 方式一：发布包安装

1. 下载 Release 里的 `ZwcadBatchPlot.zip`。
2. 解压后关闭中望 CAD。
3. 双击 `安装.cmd`。
4. 重新打开中望 CAD，菜单栏会出现“批量打印”。

卸载时关闭中望 CAD，双击 `卸载.cmd`。

### 方式二：手动加载

在中望 CAD 中执行 `NETLOAD`，选择 `BatchPlotter.dll`。

## 菜单

- 新增图框：选择图框块，框选打印外边界、图名区域、图号区域，并设置输出纸张。
- 图框库管理：管理本地图框定义。
- 批量打印：扫描当前图或添加 DWG，预览清单并打印。
- 设置：管理输出目录、重名处理、打印进度、跨文件打印方式等。
- 安装自动加载：写入当前用户的中望 CAD 自动加载注册表项。
- 卸载自动加载：删除自动加载注册表项。

## 用户数据

插件用户数据保存在：

```text
%APPDATA%\ZwcadBatchPlot
```

主要文件：

- `TitleBlockLibrary.json`：图框信息库。
- `Settings.json`：用户设置。
- `Logs`：批量打印日志。

这些用户数据不应提交到 Git 仓库。

## 开发

项目目标框架为 `.NET Framework 4.8`，依赖中望 CAD 的托管 DLL：

- `ZwManaged.dll`
- `ZwDatabaseMgd.dll`
- `Newtonsoft.Json.dll`

默认项目文件引用路径为：

```text
C:\Program Files\ZWSOFT\ZWCAD Enterprise\
```

如果你的中望 CAD 安装路径不同，需要修改 `BatchPlotter.csproj` 中的 `HintPath`。

编译：

```powershell
dotnet build .\BatchPlotter.csproj -c Release
```

发布测试包：

```powershell
dotnet build .\BatchPlotter.csproj -c Release -o .\release\ZwcadBatchPlot
```

## 说明

本项目主要面向中望 CAD 2025。其他版本中望 CAD 可能可以运行，但需要自行验证对应的托管 DLL 兼容性。
