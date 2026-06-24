# 批量打印插件架构文档

> 覆盖 ZWCAD 和 AutoCAD 双平台，版本 1.9.1

---

## 目录

1. [命令入口一览](#1-命令入口一览)
2. [流程一：新增图框](#2-流程一新增图框)
3. [流程二：扫描图框（图框库匹配）](#3-流程二扫描图框图框库匹配)
4. [流程三：扫描矩形框](#4-流程三扫描矩形框)
5. [打印引擎](#5-打印引擎)
6. [PDF 预览与合并](#6-pdf-预览与合并)
7. [动态块处理](#7-动态块处理)
8. [ZWCAD vs AutoCAD 差异](#8-zwcad-vs-autocad-差异)
9. [项目文件结构](#9-项目文件结构)

---

## 1. 命令入口一览

所有命令定义在 [BatchPlotCommands.cs](src/Common/BatchPlotCommands.cs)，通过 CAD 命令行或菜单触发：

| 命令 | 功能 |
|------|------|
| `ZBP_ADD_TITLE_BLOCK` | 新增图框到图框库 |
| `ZBP_SHOW_PANEL` | 打开批量打印面板（图框库匹配模式） |
| `ZBP_SINGLE_PLOT` | 单张打印（手动框选） |
| `ZBP_RECTANGLE_BATCH_PLOT` | 批量打印（矩形框扫描模式） |
| `ZBP_MANAGE_LIBRARY` | 管理图框库 |
| `ZBP_SETTINGS` | 设置 |
| `ZBP_PDF_VIEWER` | PDF 预览/合并/拆分 |

每个命令有一个对应的 `_ZBP_INTERNAL_*` 别名，用于兼容旧版菜单。

---

## 2. 流程一：新增图框

**触发**：用户运行 `ZBP_ADD_TITLE_BLOCK`，选择一个动态块或普通块参照。

### 2.1 整体流程

```
用户选择 BlockReference
  │
  ├─ GetBlockName(blockRef) → 获取有效块名
  │   └─ 如果是动态块: 返回 DynamicBlockTableRecord.Name ("【地铁院】图框")
  │
  ├─ TryGetVisibleNestedBlock(tr, blockRef)
  │   ├─ 进入匿名块定义 (*U12)
  │   ├─ 遍历所有嵌套 BlockReference
  │   ├─ 检查 entity.Visible → 只留 CAD 判定为可见的
  │   ├─ 多个可见时：选包围盒面积最大的
  │   └─ 返回可见嵌套块的有效名和变换矩阵
  │
  ├─ 如果有可见嵌套块 → 以嵌套块名作为图框库 key
  │   例: "【地铁院】图框" → 深入一层 → 可见嵌套块 = "A2" → 库 key = "A2"
  │
  ├─ 用户框选打印边界（可选，回车则用块外包框）
  ├─ 用户框选图名区域
  ├─ 用户框选图号区域
  ├─ 自动检测纸张尺寸 → 用户确认/调整
  │
  └─ TitleBlockLibraryStore.Upsert(definition)
       └─ 保存到 %APPDATA%\ZwcadBatchPlot\TitleBlockLibrary.json
```

### 2.2 关键代码路径

| 步骤 | 代码位置 |
|------|---------|
| 获取块名 | `CadTextExtractor.GetBlockName()` — [ZWCAD](src/ZWCAD/CadTextExtractor.cs#L23) / [AutoCAD](src/AutoCAD/CadTextExtractor.cs#L23) |
| 深入动态块 | `BatchPlotCommands.TryGetVisibleNestedBlock()` — [line 951](src/Common/BatchPlotCommands.cs#L951) |
| 可见性判断 | `IsEntityVisible()` — 使用 `entity.Visible` 属性 |
| 坐标变换 | `BatchPlotCommands.TransformRegion/TransformExtents/ToFrameRelative` |
| 纸张检测 | `PaperSizeDetector.Detect()` — [PaperSizeDetector.cs](src/Common/PaperSizeDetector.cs) |
| 持久化 | `TitleBlockLibraryStore.Upsert()` — [ZWCAD](src/ZWCAD/TitleBlockLibraryStore.cs) / [AutoCAD](src/AutoCAD/TitleBlockLibraryStore.cs) |

### 2.3 动态块 vs 普通块

```
场景 A：普通块 "A2图框"
  └─ GetBlockName → "A2图框" → 直接作为库 key → 存储 ✅

场景 B：动态块 "【地铁院】图框" (可见性=A2)
  ├─ GetBlockName → "【地铁院】图框"
  ├─ TryGetVisibleNestedBlock → 深入定义
  │   ├─ 嵌套块 A0 (Visible=false) → 跳过
  │   ├─ 嵌套块 A1 (Visible=false) → 跳过
  │   ├─ 嵌套块 A2 (Visible=true)  → ✅ 选中
  │   └─ 嵌套块 A3 (Visible=false) → 跳过
  └─ 库 key = "A2" → 存储 ✅
```

### 2.4 存储的数据结构

```json
{
  "Version": 1,
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
      "CreatedAt": "2025-01-01T00:00:00",
      "UpdatedAt": "2025-01-01T00:00:00"
    }
  ]
}
```

---

## 3. 流程二：扫描图框（图框库匹配）

**触发**：用户运行 `ZBP_SHOW_PANEL` 打开批量打印面板 → 自动扫描当前图纸。

### 3.1 整体流程

```
TitleBlockScanner.Scan(Document, TitleBlockLibrary)
  │
  ├─ 加载 TitleBlockLibrary (JSON)
  │
  ├─ 遍历所有布局 (Model + 所有 Paper Space)
  │   │
  │   └─ 遍历布局中所有 BlockReference
  │       │
  │       ├─ ① GetBlockName(blockRef) → "【地铁院】图框"
  │       ├─ ② 查图框库: 有没有 "【地铁院】图框"?
  │       │      │
  │       │      ├─ 有 → 直接匹配 ✅
  │       │      │
  │       │      └─ 没有 → ③ ResolveNestedLibraryMatch(tr, blockRef)
  │       │            │
  │       │            ├─ 进入匿名块定义 (*U12)
  │       │            ├─ 遍历嵌套 BlockReference
  │       │            ├─ 检查 entity.Visible → 过滤隐藏状态
  │       │            └─ 用嵌套块名查库 → 匹配则返回 ✅
  │       │
  │       ├─ ④ 解析坐标：块内坐标 / 世界坐标 / Frame相对坐标
  │       ├─ ⑤ 提取文字：CadTextExtractor.ExtractRegionText()
  │       │      ├─ 图名区域 → "一层平面图"
  │       │      └─ 图号区域 → "JZ-01"
  │       ├─ ⑥ 检测纸张：PaperSizeDetector.Detect(宽度, 高度)
  │       └─ ⑦ 生成 PlotJob → 加入结果列表
  │
  ├─ 去重：DeduplicateOverlappingJobs()
  │   └─ 重叠率 ≥ 90% 的两个 Job → 保留评分高的
  │
  └─ 排序：按图号自然排序 → 返回 List<PlotJob>
```

### 3.2 关键代码路径

| 步骤 | 代码位置 |
|------|---------|
| 扫描入口 | `TitleBlockScanner.Scan()` — [line 54](src/Common/TitleBlockScanner.cs#L54) |
| 块名匹配 | `CadTextExtractor.GetBlockName()` — 处理动态块 |
| 嵌套匹配 | `TitleBlockScanner.ResolveNestedLibraryMatch()` — [line 633](src/Common/TitleBlockScanner.cs#L633) |
| 可见性判断 | `IsEntityVisible()` — 使用 `entity.Visible` 属性 |
| 坐标模式 | `GetCoordinateMode()` / `ResolveWorldExtents()` / `ResolveLocalRegion()` |
| 文字提取 | `CadTextExtractor.ExtractRegionText()` |
| 去重评分 | `ScoreJob()` — 有图名+10, 有图号+10, 含审签文字-20 |
| 自然排序 | `NaturalStringComparer` |

### 3.3 动态块匹配示例

```
图纸中有 BlockRef → *U12 (动态块 "【地铁院】图框", 当前可见性=A2)

  GetBlockName(blockRef) → "【地铁院】图框"
  查库: 库里有 "【地铁院】图框" 吗? → 没有 ❌

  ResolveNestedLibraryMatch: 深入 *U12
    ├─ BlockRef → "A0" (Visible=false) → 跳过
    ├─ BlockRef → "A1" (Visible=false) → 跳过
    ├─ BlockRef → "A2" (Visible=true)  → 查库: 有 "A2" ✅ 匹配！
    └─ BlockRef → "A3" (Visible=false) → 跳过

  使用 "A2" 的图框定义，计算坐标、提取文字、生成 PlotJob
```

### 3.4 扫描范围控制

| Scope | 说明 |
|-------|------|
| `AllSpaces` | 扫描所有布局（模型空间 + 所有图纸空间） |
| `PaperLayouts` | 仅扫描图纸空间布局 |
| `ModelSpace` | 仅扫描模型空间 |
| `CurrentSpace` | 仅扫描当前激活的空间 |

---

## 4. 流程三：扫描矩形框

**触发**：用户运行 `ZBP_RECTANGLE_BATCH_PLOT`，手动框选一个扫描范围。

### 4.1 整体流程

```
RectangleFrameScanner.ScanWindow(Document, scanWindow)
  │
  ├─ 确定扫描目标空间:
  │   ├─ TileMode=1 (模型空间) → 扫描 ModelSpace
  │   └─ TileMode=0 (图纸空间) → 扫描当前 PaperSpace 布局
  │
  ├─ 遍历目标空间中所有顶层实体
  │   └─ 每个实体 → CollectEntityRectangles()
  │       │
  │       ├─ ① 是 Polyline?
  │       │   ├─ IsEntityLayerScannable? → 图层可扫描? (!Off & !Frozen & IsPlottable)
  │       │   ├─ TryGetRectangle? → 顶点≥4, 无bulge, 闭合, 四点直角矩形?
  │       │   ├─ 纸张比例匹配? → PaperSizeDetector.DetectCandidates()
  │       │   └─ 匹配 → 加入 rectangles 列表 ✅
  │       │
  │       └─ ② 是 BlockReference? (递归深度 ≤ 12)
  │           ├─ visitedDefinitions 防循环
  │           ├─ 进入块定义，遍历所有子实体:
  │           │   ├─ IsEntityLayerScannable? → 图层过滤
  │           │   ├─ IsEntityVisible (entity.Visible)? → 可见性过滤
  │           │   │   └─ 动态块隐藏状态: Visible=false → 跳过
  │           │   │   └─ 动态块显示状态: Visible=true  → 递归进入
  │           │   └─ 递归 → CollectEntityRectangles(子实体)
  │           └─ 递归处理子 BlockReference 和 Polyline
  │
  ├─ 过滤矩形 List<LocalRectangle>:
  │   ├─ FilterRectangles()
  │   │   ├─ 去重：重叠率 ≥ 90% 的同尺寸矩形 → 只保留一个
  │   │   └─ 去嵌套：小矩形在大矩形内部且面积小于 1.5 倍 → 移除小矩形
  │   └─ 按扫描窗口裁剪：Intersects(rectangle, scanWindow)
  │
  └─ 每个矩形生成 PlotJob:
      ├─ PaperSizeDetector.DetectCandidates(width, height) → 候选纸张
      ├─ 取最优匹配 paper = candidates[0]
      └─ 返回 List<Result>
```

### 4.2 关键代码路径

| 步骤 | 代码位置 |
|------|---------|
| 扫描入口 | `RectangleFrameScanner.ScanWindow()` — [line 27](src/Common/RectangleFrameScanner.cs#L27) |
| 递归遍历 | `CollectEntityRectangles()` — [line 117](src/Common/RectangleFrameScanner.cs#L117) |
| 矩形检测 | `TryGetRectangle()` — 顶点检查、bulge、闭合、去共线点 |
| 图层过滤 | `IsEntityLayerScannable()` — !Off & !Frozen & IsPlottable |
| 可见性过滤 | `IsEntityVisible()` — `entity.Visible` |
| 去重去嵌套 | `FilterRectangles()` — [line 305](src/Common/RectangleFrameScanner.cs#L305) |

### 4.3 三种场景的识别逻辑

#### 场景 A：直接 Polyline（不在任何块里）

```
图纸空间
  └─ Polyline (矩形, layer=0)

CollectEntityRectangles(Polyline):
  ① Polyline 检测 → TryGetRectangle → ✅
  ② 纸张匹配 → A2 → rectangles.Add(...)
  ③ 不是 BlockReference → 结束
```

#### 场景 B：普通块内含 Polyline

```
图纸空间
  └─ BlockRef → "MyFrame" (普通块, layer=0)
       └─ 定义 "MyFrame"
            └─ Polyline (矩形, layer=0)

CollectEntityRectangles(BlockRef"MyFrame"):
  ① 不是 Polyline → 跳过
  ② 是 BlockReference → 进入定义
     遍历子实体:
       Polyline → CollectEntityRectangles(Polyline)
         ① Polyline 检测 → ✅
       （没有 BlockReference，不触发可见性过滤）
```

#### 场景 C：动态块（可见性控制不同尺寸）

```
图纸空间
  └─ BlockRef → *U12 (动态块, IsDynamicBlock=true, Visibility="A2")
       └─ 定义 *U12
            ├─ BlockRef → "Frame_A0" (layer=0, Visible=false)
            │    └─ 定义含 Polyline A0尺寸
            ├─ BlockRef → "Frame_A1" (layer=0, Visible=false)
            │    └─ 定义含 Polyline A1尺寸
            ├─ BlockRef → "Frame_A2" (layer=0, Visible=true) ✅
            │    └─ 定义含 Polyline A2尺寸
            └─ BlockRef → "Frame_A3" (layer=0, Visible=false)
                 └─ 定义含 Polyline A3尺寸

CollectEntityRectangles(BlockRef*U12):
  ① 不是 Polyline → 跳过
  ② 是 BlockReference → 进入定义 *U12
     遍历子实体:
       Frame_A0 → IsEntityLayerScannable ✅ (layer 0)
               → IsEntityVisible? → false ❌ → 跳过
       Frame_A1 → 同上 → 跳过
       Frame_A2 → IsEntityVisible? → true ✅ → 递归
         → 进入 Frame_A2 定义 → 找到 Polyline → A2 矩形 ✅
       Frame_A3 → 同上 → 跳过
```

**核心机制**：`entity.Visible` 是 CAD 引擎原生维护的属性。动态块切换可见性状态时，CAD 自动将隐藏状态的实体的 `Visible` 设为 `false`。不需要猜名字、不需要判断图层。

---

## 5. 打印引擎

**触发**：用户在 `BatchPlotForm` 或 `RectangleBatchPlotForm` 中点击"打印"。

### 5.1 整体流程

```
PlotterService.PlotMany(Jobs, deviceName, styleSheet, settings)
  │
  ├─ 按源文件分组: jobs.GroupBy(job => job.SourceFile)
  │
  ├─ 对于当前文件: SourceFile == currentFileName
  │   └─ PlotDatabase(db, fileJobs, deviceName, styleSheet, settings)
  │
  ├─ 对于外部文件: 打开 external DWG
  │   ├─ Database.ReadDwgFile(externalFile, FileOpenMode.OpenForReadAndReadShare, ...)
  │   ├─ RefreshJobsFromDatabase → 重新扫描块参照（外部文件可能已修改）
  │   ├─ PlotDatabase(db, fileJobs, ...) → 打印
  │   └─ db.CloseInput(true) → 关闭
  │
  └─ 依次处理每个文件
       │
       └─ PlotDatabase(db, jobs, deviceName, styleSheet):
            │
            ├─ 按布局分组: jobs.GroupBy(job => job.SpaceName)
            │
            └─ 对每个布局:
                 ├─ 获取 PlotSettings (clone 默认设置)
                 ├─ 选择打印机: SetPlotConfigurationName(deviceName)
                 ├─ 选择纸张: ChooseMedia(mediaNames, paperWidth, paperHeight)
                 ├─ 配置打印参数:
                 │   ├─ PlotType = Window
                 │   ├─ PlotWindow = (job.MinX, job.MinY) → (job.MaxX, job.MaxY)
                 │   ├─ StandardScale = ScaleToFit
                 │   ├─ PlotCentered = true
                 │   ├─ PlotRotation = 自动 (横/竖)
                 │   ├─ ShadePlotType = AsDisplayed
                 │   └─ CustomPrintScale (precision adjustment)
                 │
                 └─ 逐 Job 输出 PDF:
                      ├─ plotInfo.DeviceOverride → job.OutputPath (.pdf)
                      ├─ RunPlot(engine, plotInfo, pageIndex)
                      │   ├─ BeginPlot(progress)
                      │   ├─ BeginDocument(plotInfo, ...)
                      │   ├─ BeginPage(plotPageInfo, ...)
                      │   ├─ BeginGenerateGraphics(...)
                      │   ├─ EndGenerateGraphics(...)
                      │   ├─ EndPage(...)
                      │   ├─ EndDocument(...)
                      │   └─ EndPlot(...)
                      │
                      └─ ValidatePdfOutput(job.OutputPath)
                           └─ PdfSharp 验证: 文件存在、非空、至少 1 页
```

### 5.2 关键代码路径

| 组件 | 文件 |
|------|------|
| 批量打印入口 | `PlotterService.PlotMany()` — [ZWCAD](src/ZWCAD/PlotterService.cs) / [AutoCAD](src/AutoCAD/PlotterService.cs) |
| 单文件打印 | `PlotterService.PlotDatabase()` |
| 单 Job 打印 | `PlotterService.Plot()` |
| 纸张匹配 | `ChooseMedia()` (AutoCAD) / `SelectMedia()` (ZWCAD) |
| PDF 验证 | `PlotterService.ValidatePdfOutput()` |
| 打印机安装 | `AcadPlotterInstaller.InstallBundledPlotter()` |
| PDF 合并 | `PdfDocumentService.Merge()` — [PdfDocumentService.cs](src/Common/PdfDocumentService.cs) |

### 5.3 输出文件命名

```
{OutputDirectory}\{DrawingNumber} {Title}.pdf
例: D:\Output\JZ-01 一层平面图.pdf
```

如果勾选"合并为一个 PDF"：
```
{OutputDirectory}\{FileName} 批量打印.pdf
例: D:\Output\项目A 批量打印.pdf
```

---

## 6. PDF 预览与合并

**触发**：用户在打印完成后打开 `ZBP_PDF_VIEWER`，或打印时勾选"合并 PDF"。

### 6.1 PDF 预览 (PdfPreviewForm)

```
PdfPreviewForm(fileList)
  │
  ├─ 左侧: 文件列表 (DataGridView)
  │   ├─ 重命名 (F2)
  │   └─ 拖拽排序
  │
  ├─ 右侧: WebView2 嵌入 PDF 渲染
  │   └─ Microsoft.Web.WebView2 控件
  │
  └─ 操作按钮:
      ├─ 合并 → PdfDocumentService.Merge()
      ├─ 拆分 → 从多页 PDF 分离单页
      └─ 打开文件夹
```

### 6.2 PDF 合并 (PdfDocumentService)

```
PdfDocumentService.Merge(pdfFiles, outputPath)
  │
  ├─ 创建 PdfDocument (PdfSharp)
  ├─ 遍历每个输入文件
  │   ├─ 打开输入 PDF
  │   ├─ 逐页克隆到输出文档
  │   └─ 添加书签 (每组文件一个书签)
  ├─ 保存输出文件
  └─ 验证: 页数 = 输入总页数
```

---

## 7. 动态块处理

> 动态块（Dynamic Block）是具有可见性状态、拉伸等参数化行为的块参照。

### 7.1 核心原则

**不使用名字猜测，不使用图层猜测。直接问 CAD 引擎。**

```csharp
// 统一使用 entity.Visible 判断可见性
// CAD 引擎原生维护，动态块切换状态时自动更新
private static bool IsEntityVisible(Entity entity)
{
    try { return entity.Visible; }
    catch { return true; }  // API 不可用时，宁可多扫不丢
}
```

### 7.2 涉及的三处位置

| 位置 | 文件:方法 | 作用 |
|------|----------|------|
| 矩形框扫描 | `RectangleFrameScanner.CollectEntityRectangles` | 跳过隐藏状态内的 Polyline，只扫可见状态 |
| 新增图框 | `BatchPlotCommands.TryGetVisibleNestedBlock` | 定位当前可见的嵌套块，用它的名字入库 |
| 图框扫描 | `TitleBlockScanner.ResolveNestedLibraryMatch` | 深入动态块，找到可见嵌套块的库匹配 |

### 7.3 GetBlockName 的行为

```csharp
// CadTextExtractor.GetBlockName:
//   普通块: 返回 BlockTableRecord.Name  ("A2图框")
//   动态块: 返回 DynamicBlockTableRecord.Name  ("【地铁院】图框")
//   → 即返回"有效块名"，不会返回匿名块名 (*U12)
```

### 7.4 新增图框时的处理

```
用户选择动态块 "【地铁院】图框"
  → GetBlockName → "【地铁院】图框"
  → TryGetVisibleNestedBlock → 深入一层找到可见嵌套块
  → 库 key = 可见嵌套块的 BlockTableRecord.Name (如 "A2")

下次扫描时:
  → 图纸中有 BlockRef "【地铁院】图框"
  → GetBlockName → "【地铁院】图框" → 库中无匹配
  → ResolveNestedLibraryMatch → 深入一层 → 找到可见嵌套块 "A2"
  → 库中有 "A2" ✅ 匹配成功
```

---

## 8. ZWCAD vs AutoCAD 差异

### 8.1 条件编译

```
#if AUTOCAD
    using Autodesk.AutoCAD.DatabaseServices;  // AutoCAD API
#else
    using ZwSoft.ZwCAD.DatabaseServices;      // ZWCAD API
#endif
```

所有 `src/Common/` 下的文件使用 `#if AUTOCAD` 条件编译。

### 8.2 平台差异清单

| 方面 | AutoCAD | ZWCAD |
|------|---------|-------|
| 命名空间 | `Autodesk.AutoCAD.*` | `ZwSoft.ZwCAD.*` |
| 绘图仪配置 | `LA_pdf.pc3` | `LA_pdf.pc5` (需模板替换 PMP 路径) |
| 打印纸张匹配 | 复杂权重排序, 支持旋转 | `MediaSelection` 简单匹配 |
| Core Console | `ACAD_CORE` 宏, 无菜单, 不同对话框 API | N/A |
| 菜单命令前缀 | 无 `^C^C` | 需 `^C^C` |
| 自动加载注册表 | `HKCU\Software\Autodesk\AutoCAD` | `HKCU\Software\ZWSOFT\ZWCAD` |
| 图框库路径 | `%APPDATA%\AcadBatchPlot\` | `%APPDATA%\ZwcadBatchPlot\` |
| 动态块 API | 原生支持 | 老版本可能不稳定 (TryGetVisibleNestedBlock 有 try/catch 保护) |
| PlotSystemVariables | 管理 BACKGROUNDPLOT, PDFSHX 等 | 无此类 |
| 图框库迁移 | 自动从 ZWCAD 路径导入 | 无 |

### 8.3 编译项目对应

| .csproj | 平台 | Target | Output |
|---------|------|--------|--------|
| `BatchPlotter.csproj` | ZWCAD | net48 | `bin\BatchPlotter.dll` |
| `AcadBatchPlot.csproj` | AutoCAD 2019+ | net48 | `bin-acad\AcadBatchPlot.dll` |
| `AcadBatchPlot.Core.csproj` | AutoCAD 2025+ Core | net8.0-windows | `bin-acad-core\` |
| `AcadBatchPlot.AutoCAD2016.csproj` | AutoCAD 2016 | net45 | `bin-acad-2016\` |
| `AcadBatchPlot.AutoCAD2017.csproj` | AutoCAD 2017 | net46 | `bin-acad-2017\` |
| `AcadBatchPlot.AutoCAD2018.csproj` | AutoCAD 2018 | net46 | `bin-acad-2018\` |
| `AcadBatchPlot.AutoCAD2019.csproj` | AutoCAD 2019 | net47 | `bin-acad-2019\` |

---

## 9. 项目文件结构

```
批量打印/
├── ARCHITECTURE.md              ← 本文档
├── BatchPlotter.csproj          ← ZWCAD 编译入口
├── AcadBatchPlot.csproj         ← AutoCAD 编译入口
├── AcadBatchPlot.Core.csproj    ← AutoCAD Core 2025+ 编译入口
├── Directory.Build.props        ← 共享 MSBuild 属性
│
├── src/
│   ├── Common/                  ← 双平台共享代码 (#if AUTOCAD)
│   │   ├── BatchPlotCommands.cs     ← 命令入口 + 新增图框 UI
│   │   ├── BatchPlotForm.cs         ← 批量打印主面板 (图框库模式)
│   │   ├── RectangleBatchPlotForm.cs ← 批量打印面板 (矩形框模式)
│   │   ├── TitleBlockScanner.cs     ← 图框库扫描器
│   │   ├── RectangleFrameScanner.cs ← 矩形框扫描器
│   │   ├── PaperSizeDetector.cs     ← 纸张尺寸检测
│   │   ├── Models.cs               ← 数据模型 (PlotJob, TitleBlockDefinition...)
│   │   ├── AppSettingsStore.cs      ← 设置持久化
│   │   ├── PdfDocumentService.cs    ← PDF 合并
│   │   ├── PdfPreviewForm.cs        ← PDF 预览器 (WebView2)
│   │   ├── SettingsForm.cs          ← 设置面板
│   │   ├── TitleBlockLibraryManagerForm.cs ← 图框库管理面板
│   │   ├── DwgSplitService.cs       ← DWG 拆分
│   │   ├── CsvExporter.cs           ← CSV 导出
│   │   ├── FileNameSanitizer.cs     ← 文件名清洗
│   │   ├── NaturalStringComparer.cs ← 自然排序
│   │   ├── UiLayout.cs              ← WinForms 布局工具
│   │   ├── BatchPlotLogger.cs       ← 日志
│   │   ├── DirectoryTableGenerator.cs ← 图纸目录生成
│   │   └── TemporarySequenceOverlay.cs ← 打印序号标注
│   │
│   ├── AutoCAD/                  ← AutoCAD 专用实现
│   │   ├── CadTextExtractor.cs
│   │   ├── CadTextUpdater.cs
│   │   ├── CadMenuInstaller.cs
│   │   ├── PlotterService.cs
│   │   ├── AcadPlotterInstaller.cs
│   │   ├── AutoloadManager.cs
│   │   ├── TitleBlockLibraryStore.cs
│   │   └── ScanDiagnostics.cs
│   │
│   └── ZWCAD/                    ← ZWCAD 专用实现
│       ├── CadTextExtractor.cs
│       ├── CadTextUpdater.cs
│       ├── CadMenuInstaller.cs
│       ├── PlotterService.cs
│       ├── AcadPlotterInstaller.cs
│       ├── AutoloadManager.cs
│       └── TitleBlockLibraryStore.cs
│
├── resources/
│   ├── acad/Plotters/            ← AutoCAD PDF 打印机配置
│   │   ├── LA_pdf.pc3
│   │   └── PMP Files/LA_pdf.pmp
│   └── zwcad/Plotters/           ← ZWCAD PDF 打印机配置
│       ├── LA_pdf.pc5
│       └── PMP Files/LA_pdf.pmp
│
├── bin/                          ← ZWCAD 编译输出
├── bin-acad/                     ← AutoCAD 编译输出
├── bin-new/                      ← 临时编译输出（bin 被锁时）
│
└── tests/
    └── RobustnessTests/          ← 单元测试项目
```

---

## 附录：外部依赖

| 包 | 用途 |
|----|------|
| `Newtonsoft.Json` 13.0.3 | JSON 序列化 |
| `PdfSharp` 1.50.5147 | PDF 合并、验证、页数检查 |
| `Microsoft.Web.WebView2` 1.0.2535 | PDF 预览 (Chromium 内核) |
| `AutoCAD.NET` | AutoCAD .NET API (仅 AutoCAD 版本) |
| `ZwManaged.dll` / `ZwDatabaseMgd.dll` | ZWCAD .NET API (仅 ZWCAD 版本) |
