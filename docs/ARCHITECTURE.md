# LA批量打印架构文档

> 产品名 **LA批量打印**，覆盖 ZWCAD 与 AutoCAD 双平台，当前版本 **1.15.6.2**。  
> 本文档反映当前实现：图框库（含日期/版次/阶段/信息可选字段）、动态块按「块名+可见性名」识别、图框块任意比例与固定图框长宽比选纸、矩形框比例列表、图号重排、CSV 导出、PDF/PNG/JPG/DWF/DWG 多格式输出、自有栅格绘图仪、所选格式预览、随包 PIA2 模板、另存副本式 DWG 拆图、纸面 1mm 外边框内退等。

---

## 目录

1. [命令入口一览](#1-命令入口一览)
2. [源码组织：Partial Class 拆分](#2-源码组织partial-class-拆分)
3. [流程一：新增图框](#3-流程一新增图框)
4. [流程二：扫描图框（图框库匹配）](#4-流程二扫描图框图框库匹配)
5. [流程三：扫描矩形框](#5-流程三扫描矩形框)
6. [流程四：单张打印](#6-流程四单张打印)
   - [6.4 自定义纸张与长宽比选纸](#64-自定义纸张与长宽比选纸)
7. [打印引擎](#7-打印引擎)
   - [7.2 输出格式、绘图仪与纸张单位](#72-输出格式绘图仪与纸张单位)
   - [7.3 输出文件命名](#73-输出文件命名)
   - [7.4 DWG 拆图（CAD 按图框拆分）](#74-dwg-拆图cad-按图框拆分)
   - [7.5 不打印外边框内退](#75-不打印外边框内退)
8. [PDF 合并](#8-pdf-合并)
9. [UCS 坐标变换](#9-ucs-坐标变换)
10. [动态块处理](#10-动态块处理)
    - [10.7 任意纸张与长宽比选纸](#107-任意纸张与长宽比选纸)
11. [快捷键设置（命令别名）](#11-快捷键设置命令别名)
12. [ZWCAD vs AutoCAD 差异](#12-zwcad-vs-autocad-差异)
13. [项目文件结构](#13-项目文件结构)

---

## 1. 命令入口一览

所有命令定义在 `BatchPlotCommands`（partial class，跨 5 个文件），通过 CAD 命令行或菜单触发：

| 命令 | 功能 | 所在文件 |
|------|------|---------|
| `ZBP_ADD_TITLE_BLOCK` | 新增图框到图框库 | `BatchPlotCommands.cs` 入口 → `AddTitleBlockCommands.cs` 实现 |
| `ZBP_SHOW_PANEL` | 打开批量打印面板（图框库匹配模式） | `BatchPlotCommands.cs` |
| `ZBP_SINGLE_PLOT` | 单张打印（手动框选） | `BatchPlotCommands.cs` 入口 → `SinglePlotCommands.cs` 实现 |
| `ZBP_RECTANGLE_BATCH_PLOT` | 批量打印（矩形框扫描模式） | `BatchPlotCommands.cs` |
| `ZBP_MANAGE_LIBRARY` | 管理图框库 | `BatchPlotCommands.cs` 入口 → `TitleBlockLibraryManagerForm.cs` + `EditTitleBlockCommands.cs` |
| `ZBP_SETTINGS` | 设置 | `BatchPlotCommands.cs` |
| `ZBP_SHORTCUT_SETTINGS` | 快捷键设置（命令别名） | `BatchPlotCommands.cs` 入口 → `ShortcutSettingsDialog.cs` |
| `ZBP_OPEN_CONFIG` | 打开配置目录 | `BatchPlotCommands.cs` |
| `ZBP_ABOUT` | 关于对话框 | `BatchPlotCommands.cs` |
| `ZBP_INSTALL_AUTOLOAD` | 安装自动加载 | `BatchPlotCommands.cs` |
| `ZBP_UNINSTALL_AUTOLOAD` | 卸载自动加载 | `BatchPlotCommands.cs` |

每个命令有一个对应的 `_ZBP_INTERNAL_*` 别名，用于兼容旧版菜单。所有 UI 面板类命令带有 `CommandFlags.Session` 标记。

---

## 2. 源码组织：Partial Class 拆分

`BatchPlotCommands` 是一个 `partial class`，跨 5 个文件：

| 文件 | 职责 |
|------|------|
| [`BatchPlotCommands.cs`](src/Common/Commands/BatchPlotCommands.cs) | 命令注册入口、面板生命周期、坐标工具方法、扫描范围对话框、通用工具（`RevealFileInExplorer`、`ShowModalDialog`、`TryGetRegion` 等） |
| [`AddTitleBlockCommands.cs`](src/Common/Commands/AddTitleBlockCommands.cs) | `AddTitleBlockCore()` — 新增图框向导：选择块→框选区域→检测纸张→保存到库 |
| [`SinglePlotCommands.cs`](src/Common/Commands/SinglePlotCommands.cs) | `SinglePlotCore()` — 单张打印：UCS框选→检测/自定义纸张→输出PDF |
| [`CoordinateUtils.cs`](src/Common/Commands/CoordinateUtils.cs) | `TransformPlotWindow()` / `BuildWcsToDcsMatrix()` / `BuildUcsToDcsMatrix()` — UCS↔WCS↔DCS 坐标变换 |
| [`EditTitleBlockCommands.cs`](src/Common/Commands/EditTitleBlockCommands.cs) | `EditTitleBlockFromLibrary()` — 从图框库管理界面编辑已有图框记录（partial class） |

所有文件统一在 `namespace ZwcadBatchPlot` 下，通过 `#if AUTOCAD` 条件编译适配双平台。

---

## 3. 流程一：新增图框

**触发**：用户运行 `ZBP_ADD_TITLE_BLOCK`，选择一个动态块或普通块参照。
**实现**：[`AddTitleBlockCommands.cs`](src/Common/Commands/AddTitleBlockCommands.cs)

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
  ├─ GetLibraryIdentityName(blockRef) → 图框库身份名
  │   // 带可见性属性: 库 key = "【地铁院】图框+A2"（块名+当前可见性名）
  │   // 普通块 / 纯拉伸块: 库 key = 块名
  │   // 读不到可见性名时，才回退 TryGetVisibleNestedBlock 用“外层+内层块名”
  │   // 外框优先用当前求值定义（BlockFrameGeometry 递归进可见内层）
  │
  ├─ BlockFrameGeometry.TryGetFrame → 自动识别打印外框（最大闭合矩形 / 线包围盒）
  ├─ FieldBoxSelectDialog → 框选图名/图号/可选字段，可改打印范围与纸张
  │   // 可选字段: 日期/版次/设计阶段/信息1/信息2（可全部跳过）
  ├─ ArbitraryPaperPicker.DetectCandidatesOrPrompt(width, height)
  │   // ① 常用比例 × A0~A4 / 加长图
  │   // ② 无候选时弹 CustomScaleForm：
  │   //    - 固定图框：长宽比接近标准/加长图 → 可选目标图幅（任意比例）
  │   //    - 可自由拉伸块：不允许长宽比套图幅，只手填自定义比例
  │   //    - 否则按用户比例生成 PaperName="自定义"
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
      "BlockName": "【地铁院】图框+A2",
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

`BlockName` 为图框库身份键：普通块为块名；带可见性属性的动态块为 `块名+可见性名`；旧库可能仍是 `外层+内层块名`。

**新增字段（v2）**：`DateRegion`、`RevisionRegion`、`PhaseRegion`、`Info1Region`、`Info2Region` — 零区域（`.HasArea()=false`）表示未配置。

---

## 4. 流程二：扫描图框（图框库匹配）

**触发**：用户运行 `ZBP_SHOW_PANEL` 打开批量打印面板 → 自动扫描当前图纸。
**实现**：[`TitleBlockScanner.cs`](src/Common/Services/Scanning/TitleBlockScanner.cs)

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
  │       ├─ ① CadTextExtractor.GetLibraryIdentityName(blockRef)
  │       │      // 动态块 → “块名+当前可见性名”；无可见性 → 块名
  │       │
  │       ├─ ② 查图框库: library.Blocks.FirstOrDefault(x => x.BlockName == identityName)
  │       │      │
  │       │      ├─ 有 → 直接匹配 ✅
  │       │      │
  │       │      └─ 没有 → ③ ResolveNestedLibraryMatch / 外层块名
  │       │            │ // 兼容旧库“外层+内层块名”或仅外层名
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
  │       ├─ ⑥ 纸张识别
  │       │      // 优先 TryDetectTitleBlockAtArbitraryScale：
  │       │      //   当前短边 ÷ 录入纸张短边 → 任意比例（含 1:143、10:1）
  │       │      //   长边只判断标准/加长/实测动态纸
  │       │      // 否则 PaperSizeDetector.Detect；库中固定纸张优先
  │       └─ ⑦ new PlotJob { BlockName=identity 或库命中名, ... } → 加入结果列表
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
**实现**：[`RectangleFrameScanner.cs`](src/Common/Services/Scanning/RectangleFrameScanner.cs)

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
**实现**：[`SinglePlotCommands.cs`](src/Common/Commands/SinglePlotCommands.cs)

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
  │     └─ 无候选 → 进入非标流程（详见 6.4）
  │         ├─ CustomScaleForm（默认允许长宽比选纸）
  │         ├─ 选中标准/加长图幅 → 按该纸张物理尺寸打印，不必注册自定义纸
  │         └─ 手填自定义比例 → RegisterCustomPaper / EnsurePmpAttachment / finally 清理
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

### 6.4 自定义纸张与长宽比选纸

当框选区域匹配不到常用比例下的 A0~A4 / 加长图时，进入非标流程。

**触发条件**：PaperSizeDetector.DetectCandidates() 返回空列表。

**两条分支**（[CustomScaleForm](src/Common/Views/CustomScaleForm.cs)，单张默认 llowAspectRatioPapers=true）：

1. **长宽比选纸**：PaperSizeDetector.DetectByAspectRatio 命中标准图幅或 1/8 模数加长图时，下拉可选目标图幅；比例按短边反推（可为任意值）。选中后按该图幅物理尺寸打印，**不**写入自定义 PMP。
2. **手填自定义比例**：输入 143 / 1:143 等，按 图面尺寸 / 比例 = 纸张mm 注册任意纸张。

`
SinglePlotCore() 中 candidates.Count == 0
  │
  ├─ CustomScaleForm(width, height, GuessScale(...))
  │   ├─ SelectedStandardPaper != null → 直接使用该 PaperDetection
  │   └─ 否则 → 手填比例，进入下方 PMP 注册
  │
  ├─ PmpCustomPaper.RegisterCustomPaper(pmpPath, paperW, paperH)
  │   // 自动适配 PIA 3.0 / PIA 2.0 / ZWCAD INI；同尺寸已存在则复用
  ├─ AutoCAD: EnsureActivePdfPmpAttachment() 刷新 PC3↔PMP
  └─ finally: RemoveCustomPaper(...) 清理本次新增条目
`

**PIA 版本适配**：

| CAD 版本 | PMP 格式 | 读/写方式 |
|---------|----------|----------|
| 2024+ | PIA 3.0 JSON | Newtonsoft.Json 解析/修改 JSON |
| 2019-2023 | PIA 2.0 压缩 | PianNoCN 库解压→修改→重新压缩 |
| ZWCAD | INI 文本 | Regex 匹配 Meta/user 段 |

**关键代码路径**：

| 步骤 | 代码位置 |
|------|---------|
| 长宽比匹配 | PaperSizeDetector.DetectByAspectRatio — [PaperSizeDetector.cs](src/Common/Services/Paper/PaperSizeDetector.cs) |
| 比例/图幅对话框 | CustomScaleForm — [CustomScaleForm.cs](src/Common/Views/CustomScaleForm.cs) |
| 图框录入任意纸 | ArbitraryPaperPicker.DetectCandidatesOrPrompt — [ArbitraryPaperPicker.cs](src/Common/Services/Paper/ArbitraryPaperPicker.cs) |
| PMP 注册/清理 | PmpCustomPaper — [PmpCustomPaper.cs](src/Common/Services/Paper/PmpCustomPaper.cs) |
| PC3 关联刷新 | AcadPlotterInstaller.EnsureActivePdfPmpAttachment — 平台特有 |

> 图框录入侧与单张类似，但可自由拉伸动态块（IncludeGenericDynamicTitleBlockPaper=true）**禁止**长宽比套标准图幅，避免把拉伸长度当成任意比例。详见 [10.7](#107-任意纸张与长宽比选纸)。

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

> `DWG` 输出不进入 `PublishEngine`，而是由 `DwgSplitService` 按每个 `PlotJob` 的窗口或布局拆分为独立 DWG。详见 [7.4 DWG 拆图（CAD 按图框拆分）](#74-dwg-拆图cad-按图框拆分)。

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

AutoCAD 的 `PublishToWeb PNG.pc3` / `PublishToWeb JPG.pc3` 只读，仅提供当前 CAD 的驱动路径。安装器始终以随包、已验证的 PIA2 模板生成插件自有的 `LA_png.pc3` / `LA_jpg.pc3` 及 PIA2 PMP。标准 A4～A0 及加长规格共有 85 个毫米规格，每个规格写入横、竖两个像素介质，共 170 个介质项。

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

### 7.4 DWG 拆图（CAD 按图框拆分）

> `DWG` 输出**不进入** `PublishEngine`：不走绘图仪、CTB、打印预览和纸面留白逻辑，由 `DwgSplitService` 按每个 `PlotJob` 在磁盘上生成独立 DWG。v1.15.4 起内核为**另存副本后删框外**，不使用 `Wblock()` 整库克隆。

#### 7.4.1 与打印引擎的边界

| 项目 | PDF / PNG / JPG / DWF | DWG 拆图 |
|------|----------------------|----------|
| 内核 | `PlotterService` + `PublishEngine` | `DwgSplitService` + `DwgModelSplitter` / `DwgPaperSplitter` |
| 绘图仪 / CTB | 需要 | 不使用 |
| 预览 | 支持（CAD PlotEngine） | 不支持，界面提示「拆图操作」 |
| 留白 / 不打印外边框 | 适用 | 不适用 |
| 自定义纸张 PMP | 批量注册 | 不适用 |
| 源图修改 | 只读打印 | 只改**副本**；原图与内存库不 Purge、不改 UCS/视图 |

#### 7.4.2 UI 入口

| 界面 | 触发方式 | 实现 |
|------|----------|------|
| 图框块批量打印 [`BatchPlotForm`](src/Common/Views/BatchPlotForm.cs) | 输出格式选 **DWG** →「开始打印」；或工具栏「批量拆图」 | `PrintOrStop()` → `SplitSelectedDwgs()` → `DwgSplitService.SplitMany` |
| 矩形框批量打印 [`RectangleBatchPlotForm`](src/Common/Views/RectangleBatchPlotForm.cs) | 输出格式选 **DWG** →「开始打印」；或「批量拆图」 | `Print()` → `SplitDwgs()` → `SplitMany` |

选 DWG 时禁用 CTB、合并 PDF、纸面留白等仅用于打印输出的控件；输出路径列标题变为「DWG文件名」。拆图文件名规则与 PDF 相同，使用设置中的 `PdfFileNamePattern`（`A/B/C/D/E/F/G/T/N` 占位符）。

默认输出目录：每个源 DWG 所在目录下的 `DWG` 子目录（与「当前文件夹/输出格式」快捷方式一致）。

#### 7.4.3 PlotJob 窗口字段（拆图用哪些、不用哪些）

扫描阶段写入 `PlotJob` 的窗口信息是拆图与打印的**共同载体**，但拆图几何判定**不用 DCS 窗口**：

| 字段 | 打印 | 拆图去留 |
|------|------|----------|
| `CornerPoints`（WCS 四角） | 可转 DCS 作打印窗口 | WCS/布局：构建保留多边形 `BuildKeepPolygon` |
| `UsesUserCoordinateSystem` + `UcsMin/Max` + UCS 基轴 | 参与 DCS 变换 | 模型 UCS：在 UCS 矩形内判定，不先转 WCS 包盒 |
| `MinX/MinY/MaxX/MaxY`（常为 DCS） | `GetPlotWindow` 直接使用 | **不用于**拆图去留 |
| `IsDcsWindow` | 跳过二次变换 | 拆图忽略 |
| `IsPaperSpace` + `SpaceName` | 布局打印 | 选择 `DwgPaperSplitter` 或 `DwgModelSplitter` |

矩形框扫描注释明确要求：打印窗口可转 DCS，但 `CornerPoints` 必须保留给 DWG 拆图（见 [`RectangleFrameScanner`](src/Common/Services/Scanning/RectangleFrameScanner.cs)）。

#### 7.4.4 调度、源图与输出安全

**入口**：[`DwgSplitService.SplitMany`](src/Common/Services/Plotting/DwgSplit/DwgSplitService.cs)

```
SplitMany(jobs, document, settings, ...)
  │
  ├─ BuildOutputPaths / explicitOutputPaths
  │   ├─ FileNameSanitizer.FormatFileNamePattern(PdfFileNamePattern, …)
  │   ├─ 目录：customOutputDirectory 或 源目录 / 源目录\DWG
  │   └─ MakeUnique：重名时按 AddSequenceWhenPdfExists 追加序号
  │
  └─ 对每个 job:
      ├─ SourceDatabaseContext.Open
      │   ├─ 当前图路径与 job.SourceFile 一致 → LockDocument + 内存 Database
      │   └─ 外部 DWG → ReadDwgFile 侧库（只读引用，拆图副本仍 File.Copy 磁盘文件）
      ├─ EnsureSourceAndOutputDiffer（禁止输出路径与源路径相同）
      ├─ ExecuteWithSafeOutput(outputPath, buildTemporaryDwg)
      │   ├─ 写入 .{stem}.split-{guid}.dwg
      │   ├─ ModelSplitter 或 PaperSplitter 生成临时文件
      │   ├─ ValidateGeneratedDwg（ReadDwgFile 可读且非空）
      │   └─ File.Move 或 File.Replace 替换正式输出；失败时旧文件可恢复
      └─ 单 job 异常记入 SplitResult.Error，不阻断其余任务
```

**另存源路径**（[`ResolveSavedSourcePath`](src/Common/Services/Plotting/DwgSplit/DwgDatabaseCleanup.cs)）：按 `sourcePath` → `Database.Filename` → `job.SourceFile` 查找**磁盘上已存在**的 DWG。找不到则抛错「请先保存」。`File.Copy` **不含**未保存的内存修改。

#### 7.4.5 模型空间 vs 布局空间

| 步骤 | 模型 [`DwgModelSplitter`](src/Common/Services/Plotting/DwgSplit/DwgModelSplitter.cs) | 布局 [`DwgPaperSplitter`](src/Common/Services/Plotting/DwgSplit/DwgPaperSplitter.cs) |
|------|-------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------|
| 复制 | `File.Copy` 已保存源图 | 同左 |
| 擦除范围 | 仅**模型空间** `BlockTableRecord.ModelSpace` | 目标布局对应 `BlockTableRecord`（纸空间） |
| 布局 | **不** `DeleteUnneededLayouts`、不切 `TileMode` | `DeleteUnneededLayouts`：删非目标纸面布局，保留 Model |
| 视口 | 无 | 根视口（Number==1 / `GetViewports()[0]`）必留；浮动视口按中心或纸面矩形是否伸入图框 |
| 临时 overlay | 删 `ZBP_TEMP_SEQUENCE_OVERLAY` 层实体 | 同左 |
| 图层 | 临时解锁全部图层，提交后恢复 | 同左 |
| Purge | 副本上多轮 `PurgeUnusedNamedObjects` | 同左 |
| 空结果 | `KeptEntities==0` 抛错，不生成空 DWG | 无此硬校验 |
| WorkingDatabase | 拆图期间切到副本 `Database` | 同左；删布局时 `LayoutManager.Current` 指向副本 |

布局拆图通过 `LayoutManager.Current` 删除多余布局；模型拆图若个别纸面布局因代理数据无法删除，**保留该布局**也比让已清理的模型拆图整体失败更安全。

#### 7.4.6 子模块职责

`src/Common/Services/Plotting/DwgSplit/`：

| 文件 | 职责 |
|------|------|
| [`DwgSplitService.cs`](src/Common/Services/Plotting/DwgSplit/DwgSplitService.cs) | 批量调度、路径、临时文件替换、`SplitResult` 统计 |
| [`DwgModelSplitter.cs`](src/Common/Services/Plotting/DwgSplit/DwgModelSplitter.cs) | 模型：Copy → 擦模型框外 → Purge → SaveAs |
| [`DwgPaperSplitter.cs`](src/Common/Services/Plotting/DwgSplit/DwgPaperSplitter.cs) | 布局：Copy → 擦纸面框外 → 删其他布局 → Purge → SaveAs |
| [`DwgSplitGeometry.cs`](src/Common/Services/Plotting/DwgSplit/DwgSplitGeometry.cs) | 保留多边形、UCS 矩形、XCLIP、邻框、穿框相交 |
| [`DwgDatabaseCleanup.cs`](src/Common/Services/Plotting/DwgSplit/DwgDatabaseCleanup.cs) | 已保存路径、解锁图层、删布局、Purge、排除序号 overlay |

#### 7.4.7 几何去留（`DwgSplitGeometry`）

**保留窗口构建**：

- **WCS / 布局**：`BuildKeepPolygon(job)` — UCS 图框时将 `UcsMin/Max` 四角经 `UcsToWorld` 连成斜矩形；否则用 `CornerPoints` 或 `Min/Max` 四角。
- **UCS 模型**：判定窗口为 UCS 轴对齐矩形（`UcsMin/Max`），与 [`CadSelectionWindow.TransformWorldPointsToBounds`](src/Common/Geometry/CadCoordinateSystem.cs) 同一套上下文；**禁止**先取 WCS 四角包围盒再变换（旋转时约放大 √2）。

**`ShouldKeepEntity` 判定顺序**：

1. **XCLIP 块**（`TryGetXclipBoundary`）：`XclipFrameHitsPrintRange` — 裁剪多边形与图框打印范围相交即留，不看插入点或未裁剪外包。
2. **UCS 模型**（`ShouldKeepByUcsRectangle`）：邻框过滤 → 曲线采样/与 UCS 矩形边求交 → 变换后外包与 UCS 矩形相交；失败则保守保留。
3. **WCS / 布局**：邻框过滤（仅块、闭合多段线；中心在框外、尺寸接近、未伸入内缩 2% 多边形）→ `EntityHitsKeepPolygon`（曲线求交 + 外包与多边形相交）。
4. **浮动视口**（仅布局）：`ViewportHitsKeepPolygon` — 中心在内，或纸面视口矩形伸入内缩多边形。
5. **异常**：`UnknownExtentsKept++`，默认保留。

**与打印 UCS 文档（§9）的关系**：打印用 DCS 包围盒排版；拆图在 WCS 多边形或 UCS 矩形内做**实体级**相交，两者共用 `CornerPoints` / `CadSelectionWindow.GetJobUcsToWorld`，但拆图**不读** `IsDcsWindow` 与 DCS `Min/Max`。

#### 7.4.8 约束与禁止事项

- **先保存**：副本来自磁盘；未保存修改不会进入拆出文件。
- **禁止 Wblock**：避免视口 Off、UCS 被改写、拆出空图。
- **禁止 Purge 原图**：Purge 仅对副本，减小体积；失败不阻断拆图。
- **模型不删光布局**：DWG 须保留 Model + 至少一个 Layout，否则 CAD 打开报错。
- **不改副本 UCS/视图**：UCS 只参与去留计算，不写回实体坐标。
- **输出路径**：不能与源 DWG 相同；多任务不能共用同一输出路径。

`SplitResult` 字段：`KeptEntities`、`RemovedEntities`、`UnknownExtentsKept` 写入批量打印日志（受「生成打印日志」总开关控制）。

### 7.5 不打印外边框内退

**实现**：[`PlotWindowInset.cs`](src/Common/Services/Plotting/PlotWindowInset.cs)（设置项 `HideFrameBoundaryWhenPlotting`）。

勾选「不打印外边框」时：

- 先按**原打印窗口**完成选纸、比例、旋转和留白计算。
- 再将 DCS 打印窗口四边各内退 **1mm 纸面**（`PaperInsetMm`），只裁打印内容，不把图框临时移到不打印层。
- 内退后的窗口**不参与**比例或留白反算，避免 `ScaleToFit` 把内容放大铺满。
- 纸面短边内退后不足 `MinimumRemainingShortSideMm` 时放弃内退。

---

## 8. PDF 合并

**实现**：[`PdfDocumentService.cs`](src/Common/Services/Plotting/PdfDocumentService.cs)

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

**实现**：[`CoordinateUtils.cs`](src/Common/Commands/CoordinateUtils.cs)

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

### 10.2 身份匹配与可见性过滤

| 位置 | 文件:方法 | 作用 | 守卫条件 |
|------|----------|------|---------|
| 矩形框扫描 | `RectangleFrameScanner.CollectEntityRectangles` | 遍历子实体时过滤隐藏状态 | 无守卫，所有实体通用 |
| 身份名 | `CadTextExtractor.GetLibraryIdentityName` | 带可见性则 `块名+可见性名` | `TryGetVisibilityStateName`（属性名含「可见/visibility」） |
| 图框库匹配 | `CadTextExtractor.BlockNameMatches` | 先身份名，再外层名，再旧「外层+内层」 | — |
| 新增图框 | `AddTitleBlockCommands` | 按身份名入库；外框用当前求值定义 | 无可见性名才回退嵌套复合名 |
| 图框库扫描 | `TitleBlockScanner` | 先按身份名命中，再嵌套/外层兼容 | 身份名未命中时才嵌套查找 |
| 图框库管理高亮 | `TitleBlockLibraryManagerForm` | 当前图中已存在的身份名整行淡粉 | 同时登记旧内层复合名 |

### 10.3 可拉伸图框（FrameRightBottomDynamic 坐标模式）

> 部分图框块具有距离拉伸参数或查寻列表（加长列表），可按实际需要拉伸到任意长度。

**检测方法**：
- `HasStretchDistanceProperty` — 检查动态属性集合中是否有可写的 Distance 类型参数（自由拉伸）
- `HasLookupStretchProperty` — 检查是否有 NoUnits+string 类型、不含"可见/Visibility"的查寻列表属性（定长加长列表）
- `HasHiddenNestedBlockReference` — 检查当前求值定义中是否存在被隐藏的内层 BlockReference（可见性切换的结构特征）

**三种类型独立判断**，可组合：外层可见性切换 × 内层可拉伸、纯拉伸、纯可见性等。

**FrameRightBottomDynamic 模式**：字段区域锚定外框右边界（`ToFrameRightBottomRelative`），拉伸后文字自动跟随。纸张存为 `A1+` 泛型形式（不带具体分数），扫描时按当前实例的实际外框重新识别纸张尺寸。

**录入流程**：
```
检测块类型 → 三种组合判断 → 确定坐标模式和纸张策略
  ├─ 带可见性属性 → 身份名“块名+可见性名”（如 地铁图框+A2），外框用当前求值定义
  ├─ 拉伸块（包括查寻列表）→ FrameRightBottomDynamic + A1+ 泛型纸
  └─ 读不到可见性名但有隐藏内层图框 → 回退旧身份“外层+内层块名”
```

### 10.4 公共矩形几何函数（RectangleGeometry）

矩形框扫描和图框录入共用同一套矩形验证逻辑，已提取到 [`RectangleGeometry.cs`](src/Common/Geometry/RectangleGeometry.cs)：

- `TryGetRectangle` / `TryGetRectangleFrom2d` / `TryGetRectangleFrom3d` — 从三种多段线类型验证几何矩形
- `TransformRectangle` — 四角点变换后重算 ActualWidth/ActualHeight 和包围盒
- `GetActualArea` — 矩形实际面积（优先真实边长）

### 10.5 块定义帧几何（BlockFrameGeometry）

[`BlockFrameGeometry.cs`](src/Common/Geometry/BlockFrameGeometry.cs) 是图框录入和扫描共享的"找外框"逻辑：
- 递归遍历块定义，每层先用 `RectangleGeometry` 找闭合矩形
- 找不到矩形时回退到可见线类图素（Line/Polyline/Polyline2d/Polyline3d）合并包围盒

### 10.6 界面焦点管理（CadWindowFocus）

[`CadWindowFocus.cs`](src/Common/Infrastructure/CadWindowFocus.cs) 统一管理 CAD 内嵌 WinForms 的焦点切换：
- `HideForCadInput(this)` — 隐藏插件窗口并强制把输入焦点交还 CAD 主窗口
- `RestoreDialog(this)` — CAD 取点结束后恢复原窗口并置顶
- 所有回到 CAD 取点的流程（框选字段、框选打印范围、生成目录等）统一使用

### 10.7 任意纸张与长宽比选纸

> 常用比例匹配失败时，有两条出路：长宽比仍接近标准/加长图 → 选定目标图幅并反推任意比例；否则手填比例生成自定义纸张。

**图框录入/编辑**（[`ArbitraryPaperPicker.DetectCandidatesOrPrompt`](src/Common/Services/Paper/ArbitraryPaperPicker.cs)）：

```
DetectCandidates(常用比例 × A0~A4/加长)
  │
  ├─ 有候选 → 直接返回
  └─ 无候选 → CustomScaleForm
        ├─ allowAspectRatioPapers = !IncludeGenericDynamicTitleBlockPaper
        │     // 可自由拉伸块禁止长宽比套图幅
        ├─ 选标准/加长图幅 → 保存该图幅物理尺寸（后续扫描按短边反推比例）
        └─ 手填比例 → PaperName="自定义" + RequiresCustomPaper
```

接入点：`AddTitleBlockCommands`、`EditTitleBlockCommands`、`FieldBoxSelectDialog`（改打印范围 /「任意纸张…」按钮）。

**扫描**：

- 已录入标准图幅：`TryDetectTitleBlockAtArbitraryScale` — 当前短边 ÷ 录入短边 → 任意比例；长边只做标准/加长/实测判断。
- `PaperName="自定义"`：`ApplyFixedPaper` 固定库中纸张尺寸，并按当前外框重算比例。

**打印**：自定义纸走 `CustomPaperBatchPreparer` → `RequireExactPaperSize` / `UseExactWindowScale` → `PlotterService.SetExactWindowScale`。

**与矩形框批打的边界**：矩形框仍只认「比例设置」中的内置/自定义比例列表；图框块任意比例不受该列表限制。

---

## 11. 快捷键设置（命令别名）

**触发**：用户运行 `ZBP_SHORTCUT_SETTINGS`，或点击菜单"快捷键设置"。
**实现**：[`ShortcutSettingsControl`](src/Common/Views/ShortcutSettingsControl.xaml.cs) + [`CommandAliasManager.cs`](src/Common/Infrastructure/CommandAliasManager.cs)

**原理**：CAD 的命令别名通过 PGP 程序参数文件（`acad.pgp` / `ZWCAD.pgp`）定义。用户在 WPF 界面为 6 个常用命令设置简化命令（如 `TK` → `ZBP_ADD_TITLE_BLOCK`），保存后写入 PGP 文件末尾的管理块，执行 `REINIT`（勾选 PGP）或重启 CAD 后生效。

```
ShortcutSettingsDialog (WinForms 壳 + ElementHost)
  └─ ShortcutSettingsControl (WPF)
       ├─ 左列：功能名称 + 原始命令名
       ├─ 右列：简化命令输入框（字母开头、只含字母数字、最长16位）
       └─ 确定 → NormalizeAliases → AppSettingsStore.Save → CommandAliasManager.Apply
            ├─ 定位 PGP 文件（ACADPREFIX + ROAMABLEROOTPREFIX 双路径扫描）
            ├─ 移除旧管理块 → 追加新别名 → 写回
            └─ 提示 REINIT 或重启 CAD
```

**启动恢复**：`IExtensionApplication.Initialize()` 中调用 `CommandAliasManager.Apply()`，确保 PGP 别名在 CAD 重装或 PGP 重置后自动恢复。

---

## 12. ZWCAD vs AutoCAD 差异

### 12.1 条件编译

```csharp
#if AUTOCAD
    using Autodesk.AutoCAD.DatabaseServices;  // AutoCAD API
#else
    using ZwSoft.ZwCAD.DatabaseServices;      // ZWCAD API
#endif
```

所有 `src/Common/` 下的文件使用 `#if AUTOCAD` 条件编译，共享逻辑不变，仅切换命名空间。
AutoCAD Core 版本额外使用 `#if ACAD_CORE` 子条件处理 `CadApp.ShowModalDialog` 等 API 差异。

### 12.2 平台差异清单

| 方面 | AutoCAD | ZWCAD |
|------|---------|-------|
| 命名空间 | `Autodesk.AutoCAD.*` | `ZwSoft.ZwCAD.*` |
| 绘图仪配置 | 插件自有 `LA_pdf/LA_png/LA_jpg/LA_dwf.pc3` | 插件自有 `LA_pdf/LA_png/LA_jpg/LA_dwf.pc5`（基于模板改写 PMP 路径） |
| PNG/JPG 设备策略 | 只使用 `LA_png/LA_jpg`，CAD 自带设备仅用于生成配置 | 只使用 `LA_png/LA_jpg`，CAD 自带设备仅作为 PC5 驱动模板 |
| 栅格纸张来源 | 固定 PIA2 模板，毫米规格转换为像素介质 | 使用插件随包 PMP 纸张表并关联到插件自有 PC5 |
| 栅格纸张单位 | `Pixels`；根据设备 DPI 与毫米互换 | `Millimeters`；业务层按毫米选择规格 |
| 设备列表刷新 | `PlotConfigManager` 刷新全局设备列表 | 临时 `PlotSettings` 调用 `RefreshLists()` |
| 打印纸张匹配 | 复杂权重排序, 支持旋转, 多候选 | `MediaSelection` 简化匹配 |
| Core Console | `ACAD_CORE` 宏, 无菜单栏, 不同对话框 API | 无此概念 |
| 菜单命令前缀 | 无 `^C^C` | 需 `^C^C`（取消当前命令再执行） |
| 自动加载注册表 | `HKCU\Software\Autodesk\AutoCAD` | `HKCU\Software\ZWSOFT\ZWCAD` |
| 图框库路径 | `%APPDATA%\AcadBatchPlot\` | `%APPDATA%\ZwcadBatchPlot\` |
| 图框库迁移 | 首次加载时自动从 ZWCAD 路径导入 | 无迁移逻辑 |
| 动态块 API | `IsDynamicBlock` / `DynamicBlockTableRecord` 稳定 | 老版本可能异常 → 已用 try/catch 保护 |

### 12.3 编译项目对应

| .csproj | 平台 | Target | Output |
|---------|------|--------|--------|
| `BatchPlotter.csproj` | ZWCAD | net48 | `bin\BatchPlotter.dll` |
| `AcadBatchPlot.csproj` | AutoCAD 2015-2024 | net48 | `bin-acad\AcadBatchPlot.dll` |
| `AcadBatchPlot.Core.csproj` | AutoCAD 2025-2027 Core | net8.0-windows | `bin-acad2025-2027\AcadBatchPlot.Core.dll` |

> 最低支持 AutoCAD 2015。主项目使用 AutoCAD.NET 20.0 SDK (2015) 编译，2015~2024 全系列共用 `AcadBatchPlot.dll`；2025~2027 全系列共用 `AcadBatchPlot.Core.dll`。

---

## 13. 项目文件结构

```
LA批量打印/
├── docs/
│   ├── ARCHITECTURE.md              ← 本文档
│   ├── tutorial.html                ← 图文教程网页
│   ├── RELEASE_NOTES_v1.15.6.2.md   ← 当前版本发布说明
│   ├── 用户使用说明.md
│   └── 软件说明.txt
│
├── BatchPlotter.csproj             ← ZWCAD 编译入口 (net48)
├── AcadBatchPlot.csproj            ← AutoCAD 2015-2024 编译入口 (net48)
├── AcadBatchPlot.Core.csproj       ← AutoCAD 2025-2027 Core 编译入口 (net8.0-windows)
├── Directory.Build.props           ← 共享 MSBuild 属性 (BaseIntermediateOutputPath)
├── global.json                     ← 最低 SDK 9.0.100，同主版本内自动用最新已安装 SDK
│
├── src/
│   ├── Common/                     ← 双平台共享代码 (#if AUTOCAD)，按 C# 分层目录组织，namespace 仍为 ZwcadBatchPlot
│   │   ├── Commands/                   ← 命令层：CAD 命令入口（BatchPlotCommands partial class）
│   │   │   ├── BatchPlotCommands.cs        ← 命令注册入口 + 面板生命周期 + UI工具
│   │   │   ├── AddTitleBlockCommands.cs    ← 新增图框向导 + 动态块可见性身份
│   │   │   ├── SinglePlotCommands.cs       ← 单张打印核心 + 长宽比/自定义纸张
│   │   │   ├── EditTitleBlockCommands.cs   ← 从图框库编辑已有图框记录
│   │   │   └── CoordinateUtils.cs          ← UCS/DCS 坐标变换矩阵
│   │   ├── Models/                     ← 模型层：数据模型与持久化
│   │   │   ├── Models.cs                   ← PlotJob, TitleBlockDefinition, LocalRectangle, PaperDetection
│   │   │   ├── AppSettingsStore.cs         ← 设置持久化 (JSON)
│   │   │   └── TitleBlockLibraryStore.cs    ← 图框库持久化 (JSON 原子写入)
│   │   ├── Services/                   ← 服务层：业务逻辑
│   │   │   ├── Scanning/                   ← 扫描与文字提取
│   │   │   │   ├── TitleBlockScanner.cs        ← 图框库扫描: 身份名匹配 + 任意比例识别
│   │   │   │   ├── RectangleFrameScanner.cs    ← 矩形框扫描: 递归/XCLIP/空框/TabOrder
│   │   │   │   ├── CadTextExtractor.cs         ← 文字提取 + GetLibraryIdentityName / BlockNameMatches
│   │   │   │   └── CadTextUpdater.cs           ← 文字回写: 将图号图名写回DWG
│   │   │   ├── Paper/                      ← 纸张识别与 PMP
│   │   │   │   ├── PaperSizeDetector.cs        ← 常用比例检测 + DetectByAspectRatio + 图框任意比例
│   │   │   │   ├── ArbitraryPaperPicker.cs      ← 无标准候选时弹窗：长宽比选纸或手填比例
│   │   │   │   ├── ScaleSettingsPicker.cs       ← 比例设置: 图中拾取图框反推自定义比例
│   │   │   │   ├── PmpCustomPaper.cs           ← PMP 自定义纸张注册/删除
│   │   │   │   ├── PmpPiaConverter.cs          ← 历史兼容工具（LA 安装链路不调用）
│   │   │   │   ├── CustomPaperBatchPreparer.cs  ← 批量打印一次性注册任意纸张
│   │   │   │   └── OutputPaperNameResolver.cs   ← 输出用图幅名（不回写实际纸张）
│   │   │   └── Plotting/                    ← 打印/输出服务
│   │   │       ├── DwgSplit/                    ← DWG 拆图内核（另存副本后删框外）
│   │   │       │   ├── DwgSplitService.cs       ← 批量调度、路径与临时文件替换
│   │   │       │   ├── DwgModelSplitter.cs      ← 模型空间拆图
│   │   │       │   ├── DwgPaperSplitter.cs      ← 布局空间拆图
│   │   │       │   ├── DwgSplitGeometry.cs      ← 去留、UCS、XCLIP、穿框相交
│   │   │       │   └── DwgDatabaseCleanup.cs    ← 解锁图层、删布局、Purge
│   │   │       ├── PdfDocumentService.cs       ← PDF 合并 (PdfSharp)
│   │   │       ├── PlotStyleManager.cs          ← CTB 打印样式列表与编辑入口
│   │   │       ├── PlotTextGeometryFileUpdater.cs ← 绘图仪文字转几何字段
│   │   │       ├── PlotTextGeometryModeResult.cs  ← 文字输出模式切换结果
│   │   │       ├── PlotWindowInset.cs           ← 不打印外边框: 纸面四边各内退 1mm
│   │   │       └── RasterPlotOrientation.cs     ← 栅格输出 DCS 方向判断
│   │   ├── Geometry/                   ← 领域几何：坐标、矩形、空间排序
│   │   │   ├── BlockFrameGeometry.cs        ← 块定义帧几何: 递归找外框
│   │   │   ├── RectangleGeometry.cs         ← 公共矩形几何函数: Polyline/2d/3d→矩形验证
│   │   │   ├── CadCoordinateSystem.cs       ← UCS/WCS 选择窗口上下文
│   │   │   └── SpatialSorter.cs             ← 行列空间排序
│   │   ├── Infrastructure/             ← 基础设施层：CAD 宿主集成
│   │   │   ├── CadMenuInstaller.cs         ← 菜单栏安装
│   │   │   ├── CadWindowFocus.cs            ← CAD 焦点管理: HideForCadInput/RestoreDialog
│   │   │   ├── CommandAliasManager.cs       ← 命令别名管理: PGP 读写+REINIT
│   │   │   ├── TemporarySequenceOverlay.cs ← 打印序号标注: 红框+数字
│   │   │   ├── TransientFrameMarkers.cs    ← 新增图框临时红色标识
│   │   │   └── DirectoryTableGenerator.cs  ← 图纸目录表生成
│   │   ├── Utilities/                  ← 横切工具
│   │   │   ├── CsvExporter.cs              ← CSV 导出 (UTF-8 BOM)
│   │   │   ├── FileNameSanitizer.cs        ← 文件名清洗
│   │   │   ├── NaturalStringComparer.cs    ← 自然排序: "JZ-02" < "JZ-10"
│   │   │   └── BatchPlotLogger.cs          ← 日志输出
│   │   │
│   │   └── Views/                      ← 表现层：WinForms / WPF 界面
│   │       ├── BatchPlotForm.cs            ← 批量打印主面板 (图框库匹配模式)
│   │       ├── RectangleBatchPlotForm.cs   ← 批量打印面板 (矩形框扫描模式, TabOrder分组+行列排序)
│   │       ├── SinglePlotForm.cs           ← 单张打印确认面板（预览/纸张/路径/留边）
│   │       ├── SettingsForm.cs             ← 设置面板 (多Tab: 通用+目录+命名)
│   │       ├── TitleBlockLibraryManagerForm.cs ← 图框库管理面板
│   │       ├── PaperSizeSelectionForm.cs   ← 新增图框时纸张选择对话框
│   │       ├── SinglePlotPaperSelectionForm.cs ← 单张打印纸张选择对话框
│   │       ├── CustomScaleForm.cs          ← 非标图纸自定义比例对话框 (支持小数, 图框录入/单张打印共用)
│   │       ├── FieldBoxSelectDialog.cs     ← 新增图框可选字段框选对话框 (日期/版次/阶段/信息1/信息2)
│   │       ├── DrawingNumberReorderDialog.cs ← 图号重排对话框 (WinForms壳)
│   │       ├── DrawingNumberReorderControl.xaml + .cs ← 图号重排 WPF 控件
│   │       ├── ShortcutSettingsDialog.cs ← 快捷键设置 (WinForms壳)
│   │       ├── ShortcutSettingsControl.xaml + .cs ← 快捷键设置 WPF 控件
│   │       ├── AboutDialog.cs + AboutControl.xaml + .cs ← 关于对话框 (WPF)
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
│   │   ├── AcadPlotterInstaller.cs  ← 用固定 PIA2 模板安装 LA_pdf/png/jpg/dwf.pc3
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
│   ├── acad/Plotters/               ← AutoCAD LA 系列 PIA 模板
│   │   ├── PIA3/                    ← 历史格式样本，不参与生成或安装
│   │   │   ├── LA_pdf.pc3
│   │   │   └── PMP Files/LA_pdf.pmp
│   │   ├── PIA2/                    ← 所有 AutoCAD 版本统一使用的 LA 模板
│   │   │   ├── LA_pdf/png/jpg/dwf.pc3
│   │   │   └── PMP Files/LA_pdf/png/jpg/dwf.pmp
│   │   └── README.md
│   └── zwcad/Plotters/              ← ZWCAD 基础 PC5/PMP 资源；PMP 同时作为栅格纸张表模板
│       ├── LA_pdf.pc5
│       └── PMP Files/LA_pdf.pmp
│
├── scripts/
│   ├── build-dll.ps1                ← 编译脚本
│   ├── package-release.ps1          ← 本地发布目录与 ZIP 打包
│   └── generate-zwcad-plotter.ps1   ← ZWCAD 绘图仪配置生成
│
├── installer/                       ← ZWCAD 发布包说明（NETLOAD 后菜单安装自动加载）
│   └── 使用说明.txt
│
├── installer_acad/                  ← AutoCAD 发布包说明（NETLOAD 后菜单安装自动加载）
│   └── 使用说明.txt
│
├── release/                         ← 本地发布目录（不纳入 Git）
│   └── v1.15.6.2/
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

### PlotJob（[`Models.cs`](src/Common/Models/Models.cs)）

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

    // 输出文件名（表格显示用，不受合并 PDF 临时路径覆盖）
    public string DisplayOutputFileName { get; set; }
}
```

### TitleBlockDefinition（[`Models.cs`](src/Common/Models/Models.cs)）

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

### AppSettings（[`AppSettingsStore.cs`](src/Common/Models/AppSettingsStore.cs)）

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
    public double LongPaperSnapToleranceMm { get; set; } = 3.0;
    public bool RecognizeFourLineRectangleFrames { get; set; }

    // 排序
    public bool SortOrderHorizontalFirst { get; set; }

    // 命令快捷键
    public Dictionary<string, string> CommandAliases { get; set; } = new();

    // 图框外边框不打印（正式打印时四边各内退 1mm 纸面）
    public bool HideFrameBoundaryWhenPlotting { get; set; } = false;

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
