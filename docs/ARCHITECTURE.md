# 批量打印插件架构文档

> 覆盖 ZWCAD 和 AutoCAD 双平台，版本 1.13 — 本文档反映当前项目结构，包含可选字段（日期/版次/阶段/信息）、任意纸张单张打印、图号重排、CSV导出、PDF/PNG/JPG/DWF/DWG 多格式输出、插件自有栅格绘图仪、所选格式预览、PIA 版本自动适配等特性。

---

## 目录

1. [命令入口一览](#1-命令入口一览)
2. [源码组织：Partial Class 拆分](#2-源码组织partial-class-拆分)
3. [流程一：新增图框](#3-流程一新增图框)
4. [流程二：扫描图框（图框库匹配）](#4-流程二扫描图框图框库匹配)
5. [流程三：扫描矩形框](#5-流程三扫描矩形框)
6. [流程四：单张打印](#6-流程四单张打印)
   - [6.4 自定义纸张尺寸（非标图纸）](#64-自定义纸张尺寸非标图纸)
7. [打印引擎](#7-打印引擎)
   - [7.2 输出格式、绘图仪与纸张单位](#72-输出格式绘图仪与纸张单位)
   - [7.3 输出文件命名](#73-输出文件命名)
8. [PDF 合并](#8-pdf-合并)
9. [UCS 坐标变换](#9-ucs-坐标变换)
10. [动态块处理](#10-动态块处理)
11. [ZWCAD vs AutoCAD 差异](#11-zwcad-vs-autocad-差异)
12. [项目文件结构](#12-项目文件结构)

---

## 1. 命令入口一览

所有命令定义在 `BatchPlotCommands`（partial class，跨 4 个文件），通过 CAD 命令行或菜单触发：

| 命令 | 功能 | 所在文件 |
|------|------|---------|
| `ZBP_ADD_TITLE_BLOCK` | 新增图框到图框库 | `BatchPlotCommands.cs` 入口 → `AddTitleBlockCommands.cs` 实现 |
| `ZBP_SHOW_PANEL` | 打开批量打印面板（图框库匹配模式） | `BatchPlotCommands.cs` |
| `ZBP_SINGLE_PLOT` | 单张打印（手动框选） | `BatchPlotCommands.cs` 入口 → `SinglePlotCommands.cs` 实现 |
| `ZBP_RECTANGLE_BATCH_PLOT` | 批量打印（矩形框扫描模式） | `BatchPlotCommands.cs` |
| `ZBP_MANAGE_LIBRARY` | 管理图框库 | `BatchPlotCommands.cs` |
| `ZBP_SETTINGS` | 设置 | `BatchPlotCommands.cs` |
| `ZBP_OPEN_CONFIG` | 打开配置目录 | `BatchPlotCommands.cs` |
| `ZBP_RELOAD_MENU` | 刷新菜单 | `BatchPlotCommands.cs` |
| `ZBP_INSTALL_AUTOLOAD` | 安装自动加载 | `BatchPlotCommands.cs` |
| `ZBP_UNINSTALL_AUTOLOAD` | 卸载自动加载 | `BatchPlotCommands.cs` |

每个命令有一个对应的 `_ZBP_INTERNAL_*` 别名，用于兼容旧版菜单。所有 UI 面板类命令带有 `CommandFlags.Session` 标记。

---

## 2. 源码组织：Partial Class 拆分

`BatchPlotCommands` 是一个 `partial class`，跨 4 个文件：

| 文件 | 职责 |
|------|------|
| [`BatchPlotCommands.cs`](src/Common/BatchPlotCommands.cs) | 命令注册入口、面板生命周期、坐标工具方法、扫描范围对话框、通用工具（`RevealFileInExplorer`、`ShowModalDialog`、`TryGetRegion` 等） |
| [`AddTitleBlockCommands.cs`](src/Common/AddTitleBlockCommands.cs) | `AddTitleBlockCore()` — 新增图框向导：选择块→框选区域→检测纸张→保存到库 |
| [`SinglePlotCommands.cs`](src/Common/SinglePlotCommands.cs) | `SinglePlotCore()` — 单张打印：UCS框选→检测/自定义纸张→输出PDF |
| [`CoordinateUtils.cs`](src/Common/CoordinateUtils.cs) | `TransformPlotWindow()` / `BuildWcsToDcsMatrix()` / `BuildUcsToDcsMatrix()` — UCS↔WCS↔DCS 坐标变换 |

所有文件统一在 `namespace ZwcadBatchPlot` 下，通过 `#if AUTOCAD` 条件编译适配双平台。

---

## 3. 流程一：新增图框

**触发**：用户运行 `ZBP_ADD_TITLE_BLOCK`，选择一个动态块或普通块参照。
**实现**：[`AddTitleBlockCommands.cs`](src/Common/AddTitleBlockCommands.cs)

### 3.1 整体流程

```
用户选择 BlockReference
  │
  ├─ 检查 UCS → 必须是 WCS（避免后续扫描不匹配）
  │
  ├─ GetBlockName(blockRef) → 获取有效块名
  │   // 普通块: 返回 BlockTableRecord.Name ("A2图框")
  │   // 动态块: 返回 DynamicBlockTableRecord.Name ("【地铁院】图框")
  │   // 匿名块名 (*U12) 不会暴露给调用方
  │
  ├─ TryGetVisibleNestedBlock(tr, blockRef)
  │   │ // 守卫: IsDynamicBlock=false → 直接返回 false，不干扰普通块
  │   │ // 只针对动态块：深入一层找到当前可见的内层嵌套块
  │   ├─ 进入匿名块定义 (*U12)
  │   ├─ 遍历所有嵌套 BlockReference
  │   ├─ entity.Visible == true? → CAD 引擎原生判断可见性
  │   │   // 动态块切换状态时，CAD 自动将隐藏状态的 Visible 设为 false
  │   ├─ 多个可见时：选包围盒面积最大的（transform 行列式 |
  │   └─ 返回 (可见嵌套块名, 嵌套块变换矩阵)
  │       // 例: "【地铁院】图框" → 深入 → 可见的 "A2" → 库 key = "A2"
  │
  ├─ 用户框选打印边界（可选，回车则用块外包框）
  │   // 框选时通过 inverse 矩阵变换回块内坐标
  ├─ 用户框选图名区域
  ├─ 用户框选图号区域
  ├─ FieldBoxSelectDialog → 可选字段框选（日期/版次/设计阶段/信息1/信息2）
  │   // 每个可选字段独立框选，也可全部跳过
  ├─ PaperSizeDetector.Detect(width, height) → 自动检测纸张尺寸
  │   // 匹配 A0~A3 标准/加长尺寸 × 常用比例 (0.5~100)
  ├─ 用户确认/调整纸张
  │
  └─ TitleBlockLibraryStore.Upsert(definition)
       // 序列化为 JSON，原子写入（先写 .tmp，再替换，保留 .bak）
       // AutoCAD 版: %APPDATA%\AcadBatchPlot\TitleBlockLibrary.json
       // ZWCAD 版:    %APPDATA%\ZwcadBatchPlot\TitleBlockLibrary.json
```

### 3.2 存储的数据结构（Version 2）

```json
{
  "Version": 2,
  "Blocks": [
    {
      "BlockName": "A2",
      "HasPrintRegion": true,
      "CoordinateMode": "Frame",
      "PrintRegion": { "MinX": 0, "MinY": 0, "MaxX": 594, "MaxY": 420 },
      "PaperName": "A2",
      "PaperWidthMm": 594.0,
      "PaperHeightMm": 420.0,
      "TitleRegion": { "MinX": 20, "MinY": 10, "MaxX": 200, "MaxY": 40 },
      "DrawingNumberRegion": { "MinX": 500, "MinY": 10, "MaxX": 580, "MaxY": 40 },
      "DateRegion": { "MinX": 0, "MinY": 0, "MaxX": 0, "MaxY": 0 },
      "RevisionRegion": { "MinX": 0, "MinY": 0, "MaxX": 0, "MaxY": 0 },
      "PhaseRegion": { "MinX": 0, "MinY": 0, "MaxX": 0, "MaxY": 0 },
      "Info1Region": { "MinX": 0, "MinY": 0, "MaxX": 0, "MaxY": 0 },
      "Info2Region": { "MinX": 0, "MinY": 0, "MaxX": 0, "MaxY": 0 },
      "CreatedAt": "2025-01-01T00:00:00",
      "UpdatedAt": "2025-01-01T00:00:00"
    }
  ]
}
```

**新增字段（v2）**：`DateRegion`、`RevisionRegion`、`PhaseRegion`、`Info1Region`、`Info2Region` — 零区域（.HasArea()=false）表示未配置。

---

## 4. 流程二：扫描图框（图框库匹配）

**触发**：用户运行 `ZBP_SHOW_PANEL` 打开批量打印面板 → 自动扫描当前图纸。
**实现**：[`TitleBlockScanner.cs`](src/Common/TitleBlockScanner.cs)

### 4.1 整体流程

```
TitleBlockScanner.Scan(Document, TitleBlockLibrary)
  │
  ├─ 加载 TitleBlockLibrary (JSON)
  │   // 一次加载，全扫描期间共享
  │
  ├─ 遍历所有布局 (Model + 所有 Paper Space)
  │   │ // 按 ShouldScanLayout() 过滤：AllSpaces / PaperLayouts / ModelSpace / CurrentSpace
  │   │
  │   └─ 遍历布局中所有 BlockReference（通过 owner 遍历顶层实体）
  │       │
  │       ├─ ① CadTextExtractor.GetBlockName(blockRef)
  │       │      // 动态块 → DynamicBlockTableRecord.Name
  │       │
  │       ├─ ② 查图框库: library.Blocks.FirstOrDefault(x => x.BlockName == blockName)
  │       │      │
  │       │      ├─ 有 → 直接匹配 ✅
  │       │      │
  │       │      └─ 没有 → ③ ResolveNestedLibraryMatch(tr, blockRef)
  │       │            │ // 外层块名不匹配时，深入动态块找可见内层
  │       │            ├─ 进入匿名块定义 (*U12)
  │       │            ├─ 遍历嵌套 BlockReference
  │       │            ├─ entity.Visible? → CAD 原生可见性过滤（不猜图层不猜名字）
  │       │            └─ 逐个用嵌套块名查库 → 命中则返回 ✅
  │       │
  │       ├─ ④ 解析坐标模式: Frame / World / Local
  │       │      // Frame: 相对参考框偏移
  │       │      // World: 图纸绝对坐标
  │       │      // Local: 块内局部坐标
  │       ├─ ⑤ CadTextExtractor.ExtractRegionText() → 提取文字
  │       │      // 主字段: 图名/图号
  │       │      // 可选字段: 日期/版次/设计阶段/信息1/信息2
  │       │      // 三级优先级: Attribute(最高) > OwnerSpace > BlockDefinition(最低)
  │       │      // 文字清洗: %%C→Φ, %%D→°, 移除 MTEXT 格式码
  │       ├─ ⑥ PaperSizeDetector.Detect(宽度, 高度) → 纸张识别
  │       │      // 库中有固定纸张则优先使用
  │       │      // 加长图优先使用实际检测尺寸
  │       └─ ⑦ new PlotJob { ..., Date, Revision, Phase, Info1, Info2, ... } → 加入结果列表
  │
  ├─ DeduplicateOverlappingJobs()
  │   │ // 去重逻辑
  │   ├─ 按 ScoreJob() 降序排列
  │   │   // 有图名 +10, 有图号 +10, 含字母+数字的图号 +10
  │   │   // 含审签文字(审定/审核/校对/设计) -20
  │   └─ 重叠率 ≥ 90% 且同空间 → 保留评分高的，丢弃低的
  │
  └─ 排序: 按 DrawingNumber 自然排序
      // "JZ-02" 排在 "JZ-10" 前面，而不是字典序
```

---

## 5. 流程三：扫描矩形框

**触发**：用户运行 `ZBP_RECTANGLE_BATCH_PLOT`，打开 RectangleBatchPlotForm（先弹窗，后扫描）。
**实现**：[`RectangleFrameScanner.cs`](src/Common/RectangleFrameScanner.cs)

> 注意：与图框库模式不同，矩形框批打采用"先弹窗后扫描"的 UX 设计。
> 用户打开面板后，点击"扫描当前图"（选择范围）或"框选扫描"（框选区域）触发扫描，
> 而非打开命令后立即扫描。

### 5.1 整体流程

矩形框扫描提供两个入口：

- `ScanWindow(Document, scanWindow)` — 扫描当前空间的框选窗口（单布局）
- `ScanScope(Document, scope)` — 按范围扫描多个布局（全部/仅布局/当前/仅模型）

#### ScanScope 多布局流程

```
RectangleFrameScanner.ScanScope(Document, scope)
  │
  ├─ 第一阶段：遍历所有布局，按 scope 筛选
  │   ├─ 每个匹配布局 → CollectRectanglesFromSpace(tr, owner)
  │   │   // 遍历布局中所有顶层实体，递归收集矩形 Polyline
  │   ├─ 记录 (rectangles, ownerId, layoutName, isPaperSpace, TabOrder)
  │   └─ tr.Commit()
  │
  ├─ 按布局 TabOrder 排序
  │   // 模型空间先于图纸布局，图纸布局按选项卡顺序
  │
  └─ 第二阶段：对每个空间独立过滤打包
      └─ FilterAndPackageRectangles(...)
          ├─ 窗口裁剪（ScanWindow 传入时）
          ├─ 纸张标准比例过滤（DetectCandidates）
          ├─ 去重去嵌套（FilterRectangles）
          ├─ 空框过滤（FilterEmptyRectangles）
          └─ 生成 Result 列表
```

### 5.2 三种场景的识别逻辑

#### 场景 A：直接 Polyline（不在任何块里）

图纸空间的顶层矩形多段线 → 直接检测图层、矩形合法性、纸张匹配。

#### 场景 B：普通块内含 Polyline

进入块定义递归遍历子实体 → 累积变换矩阵 → 检测矩形。

#### 场景 C：动态块（可见性控制不同尺寸）

遍历匿名定义的所有嵌套块 → `entity.Visible` 过滤隐藏状态 → 只扫描当前可见状态的矩形。

### 5.3 空框过滤

`FilterEmptyRectangles()` 检查每个候选矩形内是否存在可见、可打印的绘图实体。遍历布局所有实体（含块内嵌套），检查 `GeometricExtents` 是否与目标矩形相交。矩形框多段线自身不计为"内容"。

---

## 6. 流程四：单张打印

**触发**：用户运行 `ZBP_SINGLE_PLOT`，手动框选图纸外框 → 自动识别纸张 → 直接输出 PDF。
**实现**：[`SinglePlotCommands.cs`](src/Common/SinglePlotCommands.cs)

### 6.1 整体流程

```
用户框选两个角点（UCS 坐标）
  │
  ├─ ① UCS 四点法变换到 DCS，取一次包围盒
  │     // 避免中间取 WCS 包围盒导致的二次放大
  │
  ├─ ② PaperSizeDetector.DetectCandidates(width, height)
  │     ├─ 有候选 → 继续
  │     │   // 只有一个候选: 直接使用
  │     │   // 有多个候选: 弹出 SinglePlotPaperSelectionForm 让用户选择
  │     └─ 无候选 → 进入自定义纸张流程（详见 6.4）
  │         ├─ GuessScale(width, height) → 推测整比例
  │         ├─ CustomScaleForm 弹窗确认比例
  │         ├─ InstallBundledPlotter() 确保打印机已安装
  │         ├─ PmpCustomPaper.RegisterCustomPaper() 写入 PMP
  │         ├─ 组装自定义纸张候选
  │         └─ finally: RemoveCustomPaper() 清理 PMP
  │
  ├─ ③ SinglePlotForm 弹窗 → 用户确认预览/纸张/路径/留边
  │     // 支持预览和直接打印两种模式
  │
  ├─ ④ 组装 PlotJob
  │     {
  │       IsManualWindow = true,
  │       IsDcsWindow = true,    // 坐标已变换为 DCS
  │       RequireExactPaperSize,  // 自定义纸张严格匹配
  │       UseExactWindowScale,    // 自定义纸张精确等比缩放
  │       LeavePaperMargin,       // 是否留边
  │       PaperMarginMm           // 留边距离
  │     }
  │
  └─ ⑤ PlotterService.Plot() 或 PlotterService.Preview()
```

### 6.2 与批量打印的差异

| 方面 | 单张打印 | 批量打印 |
|------|---------|---------|
| 扫描方式 | 用户手动框选两个角点 | 自动扫描图纸中所有匹配的块/矩形 |
| 纸张确认 | 多候选时弹窗选择 | 默认用第一个候选，用户可在面板中调整 |
| 输出路径 | 每次弹 SaveFileDialog | 统一输出目录 + 自动命名 |
| 多文件处理 | 只处理当前 DWG | 可跨 DWG 文件扫描和打印 |
| 图名图号 | 使用文件名 | 从图框块中自动提取文字 |
| 合并 PDF | 不支持 | 支持 |
| 自定义纸张 | 支持非标尺寸 | 仅标准纸张 |

### 6.3 预览模式

用户在 SinglePlotForm 中可切换为预览模式，调用 `PlotterService.Preview()` 使用 CAD PlotEngine 直接预览排版效果，无需生成临时 PDF。

### 6.4 自定义纸张尺寸（非标图纸）

当用户框选的区域无法匹配 A0~A4 标准纸张时，系统自动进入自定义纸张流程。

**触发条件**：`PaperSizeDetector.DetectCandidates()` 返回空列表。

**流程**：

```
SinglePlotCore() 中 candidates.Count == 0
  │
  ├─ ① GuessScale(width, height)
  │   // 根据短边尺寸推测最可能的整数比例
  │   // 尝试 [1,2,4,5,8,10,20,25,50,100,200,500,1000]
  │   // 使纸张短边落入 100-900mm 范围
  │
  ├─ ② CustomScaleForm(width, height, guessedScale)
  │   // 弹窗显示当前图形尺寸和推测比例
  │   // 用户可调整整数比例值
  │   // 根据所选比例反算纸张尺寸: paperW = drawingW / scale
  │
  ├─ ③ 确保 LA_pdf 打印机和 PMP 已安装
  │
  ├─ ④ PmpCustomPaper.RegisterCustomPaper(pmpPath, paperW, paperH)
  │   // 向 LA_pdf.pmp 写入自定义纸张条目
  │   // 自动检测 PMP 格式:
  │   │   ├─ "PIAFILEVERSION_3.0,..." → PIA 3.0 JSON (AutoCAD 2024+)
  │   │   ├─ "[Meta]" → ZWCAD INI 格式
  │   │   └─ 其他 → PIA 2.0 压缩 (AutoCAD 2019-2023, 使用 PianNoCN)
  │   // 如果同尺寸已存在则返回已有 paperName 而不重复添加
  │   // 返回 paperName（用于后续删除）
  │
  ├─ ⑤ AutoCAD: EnsurePmpAttachment() 刷新 PC3 对 PMP 的引用
  │
  ├─ ⑥ 组装自定义纸张候选
  │   // PaperName = customPaperName, RequireExactPaperSize=true
  │
  └─ ⑦ finally: PmpCustomPaper.RemoveCustomPaper(pmpPath, paperName)
       // 无论打印成功或失败，清理 PMP 中的自定义条目
       // 防止污染用户 PMP 文件
```

**PIA 版本适配**：

| CAD 版本 | PMP 格式 | 读/写方式 |
|---------|----------|----------|
| 2024+ | PIA 3.0 JSON | Newtonsoft.Json 解析/修改 JSON |
| 2019-2023 | PIA 2.0 压缩 | PianNoCN 库解压→修改→重新压缩 |
| ZWCAD | INI 文本 | Regex 匹配 `[Meta]/[user]` 段 |

**关键代码路径**：

| 步骤 | 代码位置 |
|------|---------|
| 比例推测 | `PaperSizeDetector.GuessScale()` — [`PaperSizeDetector.cs`](src/Common/PaperSizeDetector.cs) |
| 自定义比例对话框 | `CustomScaleForm` — [`Pages/CustomScaleForm.cs`](src/Common/Pages/CustomScaleForm.cs) |
| PMP 注册（入口） | `PmpCustomPaper.RegisterCustomPaper()` — [`PmpCustomPaper.cs`](src/Common/PmpCustomPaper.cs) |
| PMP 清理 | `PmpCustomPaper.RemoveCustomPaper()` — [`PmpCustomPaper.cs`](src/Common/PmpCustomPaper.cs) |
| PIA 版本检测 | `PmpPiaConverter.IsCadPia3Compatible()` — [`PmpPiaConverter.cs`](src/Common/PmpPiaConverter.cs) |
| PIA 3→2 转换 | `PmpPiaConverter.ConvertToPia2()` — [`PmpPiaConverter.cs`](src/Common/PmpPiaConverter.cs) |
| PC3 关联刷新 | `AcadPlotterInstaller.EnsurePmpAttachment()` — 平台特有 |

---

## 7. 打印引擎

**触发**：用户在 `BatchPlotForm` 或 `RectangleBatchPlotForm` 中点击"打印"，或运行单张打印。
**实现**：[`PlotterService.cs`](src/ZWCAD/PlotterService.cs) / [`PlotterService.cs`](src/AutoCAD/PlotterService.cs)

### 7.1 打印整体流程

```
PlotterService.PlotMany(Jobs, deviceName, styleSheet, settings)
  │
  ├─ 按源文件分组: jobs.GroupBy(job => job.SourceFile)
  │   // 同一 DWG 的 Job 一次打开，减少 IO
  │
  ├─ 对于当前文件: SourceFile == currentFileName
  │   └─ PlotDatabase(db, fileJobs, deviceName, styleSheet, settings)
  │       // 当前已打开的 DWG，直接使用
  │
  ├─ 对于外部文件: settings.OpenExternalDwgForPlot 为 true 时
  │   ├─ new Database(false, true)  // 创建临时数据库
  │   ├─ db.ReadDwgFile(externalFile, ...)  // 打开外部 DWG
  │   ├─ RefreshJobsFromDatabase → 重新扫描布局
  │   │   // 外部文件可能已被修改，用 DWG 内当前状态刷新
  │   ├─ PlotDatabase(db, fileJobs, ...) → 打印
  │   └─ db.CloseInput(true) → 关闭，不保存修改
  │
  └─ 依次处理每个文件
       │
       └─ PlotDatabase(db, jobs, deviceName, styleSheet):
            │
            ├─ 按布局分组: jobs.GroupBy(job => job.SpaceName)
            │   // 同一布局的 Job 共享 PlotSettings
            │
            └─ 对每个布局:
                 ├─ new PlotSettings(layout.ModelType)
                 │   // 模型空间和图纸空间使用不同的默认设置
                 ├─ validator.SetPlotConfigurationName(deviceName, ...)
                 │   // 使用当前输出格式对应的绘图仪；预览与正式打印传入同一个 deviceName
                 ├─ ChooseMedia(mediaNames, paperWidth, paperHeight)
                 │   // AutoCAD: 复杂匹配+旋转候选+加长纸处理；栅格设备按 DPI 匹配像素纸张
                 │   // ZWCAD: SelectMedia 简单匹配
                 ├─ 配置打印参数:
                 │   ├─ PlotType = Window
                 │   ├─ PlotWindow = (job.MinX, job.MinY) → (job.MaxX, job.MaxY)
                 │   │   // 打印窗口 = 图框包围盒（IsDcsWindow 时跳过 WCS→DCS 变换）
                 │   ├─ StandardScale = ScaleToFit（或 UseExactWindowScale 时精确计算）
                 │   ├─ PlotCentered = true
                 │   ├─ PlotRotation = 自动 (比较宽高比，一致则0°否则90°)
                 │   ├─ ShadePlotType = AsDisplayed
                 │   └─ CustomPrintScale (微调比例精度)
                 │
                 └─ 逐 Job 输出当前格式:
                      ├─ plotInfo.DeviceOverride → job.OutputPath (.pdf/.png/.jpg/.dwf)
                      ├─ RunPlot(engine, plotInfo, pageIndex)
                      │   │ // PublishEngine 生命周期
                      │   ├─ BeginPlot(progress)       // 初始化引擎
                      │   ├─ BeginDocument(plotInfo, ...) // 开始文档
                      │   ├─ BeginPage(plotPageInfo, ...) // 开始页面
                      │   ├─ BeginGenerateGraphics(...)   // 生成图形
                      │   ├─ EndGenerateGraphics(...)
                      │   ├─ EndPage(...)
                      │   ├─ EndDocument(...)
                      │   └─ EndPlot(...)
                      │
                      └─ ValidatePlotOutput(job.OutputPath)
                           // PDF: PdfSharp 打开且至少 1 页
                           // PNG/JPG/DWF: 检查文件存在、非空及格式签名
                           // 验证失败 → 标记为失败，不阻塞后续 Job
```

> `DWG` 输出不进入 `PublishEngine`，而是由 `DwgSplitService` 按每个 `PlotJob` 的窗口或布局拆分为独立 DWG。

### 7.2 输出格式、绘图仪与纸张单位

| 输出格式 | AutoCAD 设备 | ZWCAD 设备 | AutoCAD 纸张单位 | ZWCAD 纸张单位 | 设备选择规则 |
|----------|--------------|------------|------------------|----------------|--------------|
| PDF | `LA_pdf.pc3` | `LA_pdf.pc5` | 毫米 | 毫米 | 使用插件自有设备 |
| PNG | `LA_png.pc3` | `LA_png.pc5` | 像素 | 毫米 | 只接受插件自有设备，不使用 CAD 自带 PNG 设备兜底 |
| JPG | `LA_jpg.pc3` | `LA_jpg.pc5` | 像素 | 毫米 | 只接受插件自有设备，不使用 CAD 自带 JPG 设备兜底 |
| DWF | 优先 `LA_dwf.pc3` | 优先 `LA_dwf.pc5` | 毫米 | 毫米 | 优先插件设备，兼容 CAD 原生 DWF 设备 |
| DWG | 无绘图仪 | 无绘图仪 | 不适用 | 不适用 | 由 `DwgSplitService` 拆分；不提供打印预览 |

#### 设备安装、枚举与预览

```text
插件初始化/批量打印窗体首次打开
  ├─ 安装或修复插件自有 LA_pdf / LA_png / LA_jpg / LA_dwf 配置
  ├─ 刷新 CAD 绘图仪设备列表
  └─ 按当前输出格式解析设备
       ├─ 预览 → SelectedPlotDevice
       └─ 打印 → SelectedPlotDevice
```

- 选择什么格式，就使用该格式的设备进行预览和正式打印，避免预览仍固定走 PDF 设备。
- PNG/JPG 必须枚举到插件自有的 `LA_png` / `LA_jpg`；如果配置安装失败或 CAD 尚未识别，直接给出明确错误，不回退到 `PublishToWeb PNG/JPG` 等自带设备。
- 安装器只创建、覆盖或修复 `LA_*` 文件，不修改用户已有的其他 PC3/PC5/PMP 配置。

#### AutoCAD 栅格设备

AutoCAD 的 `PublishToWeb PNG.pc3` / `PublishToWeb JPG.pc3` 仅作为驱动和图像参数模板。安装器基于它们生成插件自有的 `LA_png.pc3` / `LA_jpg.pc3`，并生成对应的 PIA2 或 PIA3 PMP。标准 A4～A0 及加长规格共有 85 个毫米规格，每个规格写入横、竖两个像素介质，共 170 个介质项。

AutoCAD 栅格驱动要求 `PlotPaperUnit.Pixels`。业务层仍以毫米识别图框和纸张，`PlotterService` 读取设备 DPI 后执行双向换算：

```text
毫米纸张尺寸 × DPI ÷ 25.4 → 匹配 PMP 中的像素介质
像素可打印区域 × 25.4 ÷ DPI → 参与窗口比例和居中计算
```

该边界是 PNG/JPG 与 PDF/DWF 的关键差异：不能把栅格设备强制设置为毫米单位，否则 AutoCAD 会在预览或批量打印阶段抛出 `eInvalidInput`。

#### ZWCAD 栅格设备

ZWCAD 使用系统 PNG/JPG PC5 作为驱动模板，但把 PMP 关联改写为插件自有的 `LA_png.pmp` / `LA_jpg.pmp`。栅格纸张表来自插件随包资源，不依赖用户机器已有的自定义纸张文件；打印服务按 ZWCAD 驱动约定使用毫米单位。安装完成后，通过临时 `PlotSettings` 执行 `RefreshLists()`，使当前 CAD 会话重新枚举设备和纸张。

### 7.3 输出文件命名

```
{OutputDirectory}\{字段1}{分隔符}{字段2}.{当前格式扩展名}
例: D:\Output\JZ-01_一层平面图.png
```

命名字段和连接符可在设置中配置：
- 默认字段: `["DrawingNumber", "Title"]`
- 默认连接符: `_`
- 可选字段: Date, Revision, Phase, Info1, Info2

如果勾选"合并为一个 PDF"：
```
{OutputDirectory}\{FileName}_批量打印.pdf
```

---

## 8. PDF 合并

**实现**：[`PdfDocumentService.cs`](src/Common/PdfDocumentService.cs)

```
PdfDocumentService.Merge(pdfFiles, outputPath)
  │
  ├─ 创建输出 PdfDocument (PdfSharp)
  ├─ 遍历每个输入 PDF:
  │   ├─ PdfReader.Open(inputFile) → 打开源文件
  │   ├─ 逐页 ClonePage → 克隆到输出文档
  │   │   // PdfSharp 不支持跨文档直接复制，需逐页克隆
  │   └─ 可选: 添加书签（每组一个书签节点）
  ├─ outputDocument.Save(outputPath) → 写入磁盘
  └─ 验证: 输出页数 == 输入总页数
```

合并失败不会阻塞打印 — 单页 PDF 仍然可用。

---

## 9. UCS 坐标变换

**实现**：[`CoordinateUtils.cs`](src/Common/CoordinateUtils.cs)

全面支持用户坐标系（UCS）。三个打印功能共用同一套变换链路。

### 9.1 核心原则

**四点变换，一次包围盒。** 将实际角点一步变换到 DCS，只取一次包围盒。避免中间取 WCS 包围盒导致的重复放大。

### 9.2 变换矩阵

```
BuildUcsToDcsMatrix = UCS→WCS × WCS→DCS
BuildWcsToDcsMatrix = PlaneToWorld × Displacement × Rotation → Inverse

UCS=WCS 时所有矩阵退化为单位矩阵，行为不变。
```

### 9.3 三个功能的变换路径

| 功能 | 输入坐标系 | 变换 | 输出 |
|------|-----------|------|------|
| 单张打印 | UCS 角点 | `BuildUcsToDcsMatrix` | DCS 包围盒 → PlotJob（IsDcsWindow=true） |
| 矩形框批量 | WCS 角点 (CornerPoints) | `BuildWcsToDcsMatrix` | DCS 包围盒 → PlotJob |
| 图框块批量 | WCS 角点 (ComputeWcsCorners) | `BuildWcsToDcsMatrix` | DCS 包围盒 → PlotJob |

三条路径殊途同归：`IsDcsWindow=true` → `GetPlotWindow` 跳过 → `PrepareEditorViewForPlot` 跳过。

### 9.4 Overlay UCS 跟随

红框和数字按 UCS X 轴角度旋转后绘制到 WCS，保证任何 UCS 视图下显示为正。

---

## 10. 动态块处理

> 动态块（Dynamic Block）是具有可见性状态、拉伸等参数化行为的块参照。

### 10.1 核心原则

**不使用名字猜测，不使用图层猜测。直接问 CAD 引擎。**

```csharp
// 统一使用 entity.Visible 判断可见性
// CAD 引擎原生维护，动态块切换可见性状态时自动更新
// 普通块中所有实体默认 Visible=true，不影响正常扫描
private static bool IsEntityVisible(Entity entity)
{
    try { return entity.Visible; }
    catch { return true; }  // 老版 API 无此属性时，宁可多扫不丢
}
```

### 10.2 涉及的三处位置

| 位置 | 文件:方法 | 作用 | 守卫条件 |
|------|----------|------|---------|
| 矩形框扫描 | `RectangleFrameScanner.CollectEntityRectangles` | 遍历子实体时过滤隐藏状态 | 无守卫，所有实体通用 |
| 新增图框 | `AddTitleBlockCommands.TryGetVisibleNestedBlock` | 定位当前可见嵌套块名入库 | `IsDynamicBlock` — 普通块直接返回 false |
| 图框库扫描 | `TitleBlockScanner.ResolveNestedLibraryMatch` | 深入动态块找可见嵌套块的库匹配 | 仅 `definition==null` 时触发 |

---

## 11. ZWCAD vs AutoCAD 差异

### 11.1 条件编译

```csharp
#if AUTOCAD
    using Autodesk.AutoCAD.DatabaseServices;  // AutoCAD API
#else
    using ZwSoft.ZwCAD.DatabaseServices;      // ZWCAD API
#endif
```

所有 `src/Common/` 下的文件使用 `#if AUTOCAD` 条件编译，共享逻辑不变，仅切换命名空间。
AutoCAD Core 版本额外使用 `#if ACAD_CORE` 子条件处理 `CadApp.ShowModalDialog` 等 API 差异。

### 11.2 平台差异清单

| 方面 | AutoCAD | ZWCAD |
|------|---------|-------|
| 命名空间 | `Autodesk.AutoCAD.*` | `ZwSoft.ZwCAD.*` |
| 绘图仪配置 | 插件自有 `LA_pdf/LA_png/LA_jpg/LA_dwf.pc3` | 插件自有 `LA_pdf/LA_png/LA_jpg/LA_dwf.pc5`（基于模板改写 PMP 路径） |
| PNG/JPG 设备策略 | 只使用 `LA_png/LA_jpg`，CAD 自带设备仅用于生成配置 | 只使用 `LA_png/LA_jpg`，CAD 自带设备仅作为 PC5 驱动模板 |
| 栅格纸张来源 | 安装时生成 PIA2/PIA3 PMP，毫米规格转换为像素介质 | 使用插件随包 PMP 纸张表并关联到插件自有 PC5 |
| 栅格纸张单位 | `Pixels`；根据设备 DPI 与毫米互换 | `Millimeters`；业务层按毫米选择规格 |
| 设备列表刷新 | `PlotConfigManager` 刷新全局设备列表 | 临时 `PlotSettings` 调用 `RefreshLists()` |
| 打印纸张匹配 | 复杂权重排序, 支持旋转, 多候选 | `MediaSelection` 简化匹配 |
| Core Console | `ACAD_CORE` 宏, 无菜单栏, 不同对话框 API | 无此概念 |
| 菜单命令前缀 | 无 `^C^C` | 需 `^C^C`（取消当前命令再执行） |
| 自动加载注册表 | `HKCU\Software\Autodesk\AutoCAD` | `HKCU\Software\ZWSOFT\ZWCAD` |
| 图框库路径 | `%APPDATA%\AcadBatchPlot\` | `%APPDATA%\ZwcadBatchPlot\` |
| 图框库迁移 | 首次加载时自动从 ZWCAD 路径导入 | 无迁移逻辑 |
| 动态块 API | `IsDynamicBlock` / `DynamicBlockTableRecord` 稳定 | 老版本可能异常 → 已用 try/catch 保护 |

### 11.3 编译项目对应

| .csproj | 平台 | Target | Output |
|---------|------|--------|--------|
| `BatchPlotter.csproj` | ZWCAD | net48 | `bin\BatchPlotter.dll` |
| `AcadBatchPlot.csproj` | AutoCAD 2015-2024 | net48 | `bin-acad\AcadBatchPlot.dll` |
| `AcadBatchPlot.Core.csproj` | AutoCAD 2025-2027 Core | net8.0-windows | `bin-acad2025-2027\AcadBatchPlot.Core.dll` |

> 最低支持 AutoCAD 2015。主项目使用 AutoCAD.NET 20.0 SDK (2015) 编译，2015~2024 全系列共用 `AcadBatchPlot.dll`；2025~2027 全系列共用 `AcadBatchPlot.Core.dll`。

---

## 12. 项目文件结构

```
批量打印/
├── docs/
│   ├── ARCHITECTURE.md              ← 本文档
│   ├── platform-architecture.md
│   ├── repository-layout.md
│   ├── tutorial.html                ← 图文教程网页
│   ├── 用户使用说明.md
│   └── 软件说明.txt
│
├── BatchPlotter.csproj             ← ZWCAD 编译入口 (net48)
├── AcadBatchPlot.csproj            ← AutoCAD 2015-2024 编译入口 (net48)
├── AcadBatchPlot.Core.csproj       ← AutoCAD 2025-2027 Core 编译入口 (net8.0-windows)
├── Directory.Build.props           ← 共享 MSBuild 属性 (BaseIntermediateOutputPath)
├── global.json                     ← .NET SDK 版本 (9.0.315)
│
├── src/
│   ├── Common/                     ← 双平台共享代码 (#if AUTOCAD)
│   │   ├── BatchPlotCommands.cs        ← 命令注册入口 + 面板生命周期 + UI工具 (partial class)
│   │   ├── AddTitleBlockCommands.cs    ← 新增图框向导 + 动态块可见性 (partial class)
│   │   ├── SinglePlotCommands.cs       ← 单张打印核心 + 打印机选择 + 自定义纸张 (partial class)
│   │   ├── CoordinateUtils.cs          ← UCS/DCS 坐标变换矩阵 (partial class)
│   │   ├── Models.cs                   ← 数据模型: PlotJob, TitleBlockDefinition, LocalRectangle, PaperDetection
│   │   ├── TitleBlockScanner.cs        ← 图框库扫描器: 扫描→匹配→生成PlotJob
│   │   ├── RectangleFrameScanner.cs    ← 矩形框扫描器: 递归扫描→XCLIP过滤→空框过滤→TabOrder排序
│   │   ├── PaperSizeDetector.cs        ← 纸张尺寸检测: A0~A3标准/加长 + GuessScale (非标图纸比例推测)
│   │   ├── CadTextExtractor.cs         ← 文字提取: 属性/文字/多行文字, XCLIP 过滤, 三级优先级
│   │   ├── CadTextUpdater.cs           ← 文字回写: 将图号图名写回DWG
│   │   ├── CadMenuInstaller.cs         ← 菜单栏安装: 创建"批量打印"菜单
│   │   ├── TitleBlockLibraryStore.cs    ← 图框库持久化 (JSON 原子写入)
│   │   ├── AppSettingsStore.cs         ← 设置持久化 (JSON), 含文件名连接符/字段合法性检查
│   │   ├── PdfDocumentService.cs       ← PDF 合并 (PdfSharp)
│   │   ├── PmpCustomPaper.cs           ← PMP 自定义纸张注册/删除 (PIA3 JSON / PIA2 / ZWCAD INI)
│   │   ├── PmpPiaConverter.cs          ← PIA 版本检测 + PIA 3→2 转换
│   │   ├── DwgSplitService.cs          ← DWG 拆分 (模型空间WBLOCK / 布局空间复制)
│   │   ├── DirectoryTableGenerator.cs  ← 图纸目录表生成: 在CAD中绘制表格 + 框选单元格尺寸
│   │   ├── TemporarySequenceOverlay.cs ← 打印序号标注: 红框+数字，点击高亮，增量更新
│   │   ├── CsvExporter.cs              ← CSV 导出 (UTF-8 BOM)
│   │   ├── FileNameSanitizer.cs        ← 文件名清洗: 非法字符、路径过长
│   │   ├── NaturalStringComparer.cs    ← 自然排序: "JZ-02" < "JZ-10"
│   │   ├── BatchPlotLogger.cs          ← 日志输出
│   │   │
│   │   └── Pages/                      ← WinForms UI 面板
│   │       ├── BatchPlotForm.cs            ← 批量打印主面板 (图框库匹配模式)
│   │       ├── RectangleBatchPlotForm.cs   ← 批量打印面板 (矩形框扫描模式, TabOrder分组+行列排序)
│   │       ├── SinglePlotForm.cs           ← 单张打印确认面板（预览/纸张/路径/留边）
│   │       ├── SettingsForm.cs             ← 设置面板 (多Tab: 通用+目录+命名)
│   │       ├── TitleBlockLibraryManagerForm.cs ← 图框库管理面板
│   │       ├── PaperSizeSelectionForm.cs   ← 新增图框时纸张选择对话框
│   │       ├── SinglePlotPaperSelectionForm.cs ← 单张打印纸张选择对话框
│   │       ├── CustomScaleForm.cs          ← 非标图纸整数比例选择对话框
│   │       ├── FieldBoxSelectDialog.cs     ← 新增图框可选字段框选对话框 (日期/版次/阶段/信息1/信息2)
│   │       ├── DrawingNumberReorderDialog.cs ← 图号重排对话框 (前缀+起始号+排序方向+预览)
│   │       └── UiLayout.cs                ← WinForms 布局: DPI缩放、按钮创建、窗口配置
│   │
│   ├── PianNoCN/                    ← PIA 2.0 文件格式序列化（仅 AutoCAD 编译, namespace PiaNO）
│   │   ├── Pia/
│   │   │   ├── PiaFile.cs              ← PIA 文件容器
│   │   │   ├── PiaNode.cs              ← PIA 树节点
│   │   │   ├── PiaHeader.cs            ← PIA 文件头
│   │   │   ├── PiaSerializer.cs        ← deflate 解压/序列化
│   │   │   ├── PiaException.cs         ← 异常类型
│   │   │   └── EnumDecompressionType.cs
│   │   └── Plot/
│   │       ├── PlotterConfiguration.cs ← 绘图仪配置类型访问
│   │       └── Media.cs
│   │
│   ├── AutoCAD/                     ← AutoCAD 专用实现
│   │   ├── PlotterService.cs        ← 打印引擎: 多格式输出、栅格 DPI/像素换算、结果签名验证
│   │   ├── AcadPlotterInstaller.cs  ← 安装 LA_pdf/png/jpg/dwf.pc3，生成 PIA2/PIA3 栅格 PMP
│   │   └── AutoloadManager.cs       ← 自动加载: 注册表写入/卸载
│   │
│   └── ZWCAD/                       ← ZWCAD 专用实现（接口同名，平台适配）
│       ├── PlotterService.cs        ← 多格式输出、简化纸张匹配、结果签名验证
│       ├── AcadPlotterInstaller.cs  ← 安装 LA_pdf/png/jpg/dwf.pc5，模板改写自有 PMP 路径
│       ├── AutoloadManager.cs       ← 注册表路径: ZWSOFT\ZWCAD
│       └── Properties/
│           └── AssemblyInfo.cs
│
├── resources/
│   ├── acad/Plotters/               ← AutoCAD PDF 基础配置及 PIA 兼容资源
│   │   ├── PIA3/                    ← PIA 3.0 JSON 格式 (AutoCAD 2024+)
│   │   │   ├── LA_pdf.pc3
│   │   │   └── PMP Files/LA_pdf.pmp
│   │   ├── PIA2/                    ← PIA 2.0 压缩格式 (AutoCAD 2019-2023)
│   │   │   ├── LA_pdf.pc3
│   │   │   └── PMP Files/LA_pdf.pmp
│   │   └── README.md
│   └── zwcad/Plotters/              ← ZWCAD 基础 PC5/PMP 资源；PMP 同时作为栅格纸张表模板
│       ├── LA_pdf.pc5
│       └── PMP Files/LA_pdf.pmp
│
├── scripts/
│   ├── build-dll.ps1                ← 编译脚本
│   └── generate-zwcad-plotter.ps1   ← ZWCAD 绘图仪配置生成
│
├── installer/                       ← ZWCAD 用户安装包
│   ├── install.ps1 / uninstall.ps1
│   ├── 安装.cmd / 卸载.cmd
│   └── 使用说明.txt
│
├── installer_acad/                  ← AutoCAD 用户安装包
│   ├── install.ps1 / uninstall.ps1
│   ├── 安装.cmd / 卸载.cmd
│   └── 使用说明.txt
│
├── release/                         ← 本地发布目录（不纳入 Git）
│   └── v1.13/
│       ├── ZWCAD/
│       ├── AutoCAD2015-2024/
│       ├── AutoCAD2025-2027/
│       ├── docs/
│       └── *.zip
│
├── dist/                            ← 分发目录
│   └── ...
│
├── lib/
│   └── PianNoCN/                    ← PianNoCN 原始上游源码 (参考用，编译时使用 src/PianNoCN/)
│
├── bin/                             ← ZWCAD 编译输出
├── bin-acad/                        ← AutoCAD 2015-2024 编译输出
├── bin-acad2025-2027/                ← AutoCAD 2025-2027 Core 编译输出
│
└── *.mp4                            ← 功能演示视频（加载设置、选图框块、选矩形框）
```

---

## 附录 A：主要数据模型

### PlotJob（[`Models.cs`](src/Common/Models.cs)）

```csharp
public sealed class PlotJob
{
    // 基本标识
    public bool Selected { get; set; }           // 是否勾选打印
    public bool IsManualWindow { get; set; }     // 是否手动框选（单张打印）
    public string SourceFile { get; set; }       // 源 DWG 路径
    public string SpaceName { get; set; }        // 空间名称
    public bool IsPaperSpace { get; set; }       // 是否图纸空间
    public string BlockName { get; set; }        // 匹配的块名

    // 图框提取字段
    public string DrawingNumber { get; set; }    // 图号（用户可编辑）
    public string Title { get; set; }            // 图名（用户可编辑）
    public string CadDrawingNumber { get; set; } // CAD 中的原始图号
    public string CadTitle { get; set; }         // CAD 中的原始图名

    // 可选字段（v2 新增）
    public string Date { get; set; }             // 日期
    public string Revision { get; set; }         // 版次
    public string Phase { get; set; }            // 设计阶段
    public string Info1 { get; set; }            // 信息1
    public string Info2 { get; set; }            // 信息2
    public string CadDate { get; set; }          // CAD 原始日期
    public string CadRevision { get; set; }      // CAD 原始版次
    public string CadPhase { get; set; }         // CAD 原始阶段
    public string CadInfo1 { get; set; }         // CAD 原始信息1
    public string CadInfo2 { get; set; }         // CAD 原始信息2

    // 纸张检测
    public string PaperName { get; set; }
    public string ScaleText { get; set; }
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }

    // 打印窗口
    public double MinX, MinY, MaxX, MaxY { get; set; }

    // DCS 窗口标记
    public bool IsDcsWindow { get; set; }        // 坐标已是 DCS，跳过变换
    public bool RequireExactPaperSize { get; set; } // 严格纸张匹配（自定义纸张）
    public bool UseExactWindowScale { get; set; }   // 精确等比缩放（自定义纸张）
    public bool CustomPaperWasAdded { get; set; }   // 本 Job 是否新增了 PMP 纸张
    public double[]? CornerPoints { get; set; }      // WCS 四角点坐标

    // 留边支持
    public bool LeavePaperMargin { get; set; }
    public double PaperMarginMm { get; set; }
}
```

### TitleBlockDefinition（[`Models.cs`](src/Common/Models.cs)）

```csharp
public sealed class TitleBlockDefinition
{
    public string BlockName { get; set; }
    public bool HasPrintRegion { get; set; }
    public string CoordinateMode { get; set; } = "Local";
    public LocalRectangle PrintRegion { get; set; }
    public string PaperName { get; set; }
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }

    // 主字段
    public LocalRectangle TitleRegion { get; set; }
    public LocalRectangle DrawingNumberRegion { get; set; }

    // v2 新增可选字段
    public LocalRectangle DateRegion { get; set; }
    public LocalRectangle RevisionRegion { get; set; }
    public LocalRectangle PhaseRegion { get; set; }
    public LocalRectangle Info1Region { get; set; }
    public LocalRectangle Info2Region { get; set; }
}
```

### AppSettings（[`AppSettingsStore.cs`](src/Common/AppSettingsStore.cs)）

```csharp
public sealed class AppSettings
{
    // 打印选项
    public string LastPlotDevice { get; set; }
    public string LastStyleSheet { get; set; }
    public bool OpenExternalDwgForPlot { get; set; } = true;
    public bool ShowPlotProgress { get; set; } = true;

    // PDF 输出
    public bool MergePdf { get; set; }
    public bool AddFileNameSequence { get; set; }
    public bool AddSequenceWhenPdfExists { get; set; }
    public string PdfFileNamePattern { get; set; }                    // 字母占位符命名规则
    public int FileNameSequenceDigits { get; set; } = 2;
    public bool AutoFileNameSequenceDigits { get; set; }
    public int FileNameSequenceStartNumber { get; set; } = 1;

    // 留边
    public bool LeavePaperMargin { get; set; }
    public double PaperMarginMm { get; set; } = 1;

    // 纸张匹配
    public double PaperMatchToleranceMm { get; set; } = 1.0;
    public double OutputLongPaperSnapToleranceMm { get; set; } = 4.0;
    public bool AllowStandardPaperNameFallback { get; set; } = true;

    // 目录表格
    public double DirectoryIndexWidth { get; set; } = 900;
    public double DirectoryNumberWidth { get; set; } = 3200;
    public double DirectoryTitleWidth { get; set; } = 5200;
    public double DirectoryPaperWidth { get; set; } = 1200;
    public double DirectoryRemarkWidth { get; set; } = 1400;
    public double DirectoryRowHeight { get; set; } = 650;
    public double DirectoryTextHeightRatio { get; set; } = 0.42;
    public string DirectoryTextStyleName { get; set; } = "";
}
```

---

## 附录 B：外部依赖

| 包 | 用途 |
|----|------|
| `Newtonsoft.Json` 13.0.3 | JSON 序列化（设置、图框库、PIA 3.0 PMP） |
| `PDFsharp` 1.50.5147 | PDF 合并、验证、页数检查 |
| `SharpZipLib` 1.3.3–1.4.2 | PIA 2.0 deflate 解压/压缩（PianNoCN 序列化依赖） |
| `AutoCAD.NET` (20.0.1 / 23.0.0 / 25.0.0) | AutoCAD .NET API（仅 AutoCAD，运行时由 CAD 提供） |
| `ZwManaged.dll` / `ZwDatabaseMgd.dll` | ZWCAD .NET API（仅 ZWCAD，运行时由 ZWCAD 提供） |
| `PianNoCN` (内嵌源码) | PIA 2.0 文件格式解析（自定义纸张时读取/写入 PIA 2.0 压缩 PMP） |
