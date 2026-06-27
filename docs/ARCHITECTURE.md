# 批量打印插件架构文档

> 覆盖 ZWCAD 和 AutoCAD 双平台，版本 1.10.1 — 本文档反映当前项目结构，包含单张打印自定义纸张、XCLIP 过滤、空框过滤、TabOrder 排序等新特性。

---

## 目录

1. [命令入口一览](#1-命令入口一览)
2. [流程一：新增图框](#2-流程一新增图框)
3. [流程二：扫描图框（图框库匹配）](#3-流程二扫描图框图框库匹配)
4. [流程三：扫描矩形框](#4-流程三扫描矩形框)
5. [流程四：单张打印](#5-流程四单张打印)
   - [5.4 自定义纸张尺寸（非标图纸）](#54-自定义纸张尺寸非标图纸)
6. [打印引擎](#6-打印引擎)
7. [PDF 合并](#7-pdf-合并)
8. [UCS 坐标变换](#8-ucs-坐标变换)
9. [动态块处理](#9-动态块处理)
10. [ZWCAD vs AutoCAD 差异](#10-zwcad-vs-autocad-差异)
11. [项目文件结构](#11-项目文件结构)

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

每个命令有一个对应的 `_ZBP_INTERNAL_*` 别名，用于兼容旧版菜单。

---

## 2. 流程一：新增图框

**触发**：用户运行 `ZBP_ADD_TITLE_BLOCK`，选择一个动态块或普通块参照。

### 2.1 整体流程

```
用户选择 BlockReference
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
  ├─ PaperSizeDetector.Detect(width, height) → 自动检测纸张尺寸
  │   // 匹配 A0~A3 标准/加长尺寸 × 常用比例 (0.5~100)
  ├─ 用户确认/调整纸张
  │
  └─ TitleBlockLibraryStore.Upsert(definition)
       // 序列化为 JSON，原子写入（先写 .tmp，再替换，保留 .bak）
       // AutoCAD 版: %APPDATA%\AcadBatchPlot\TitleBlockLibrary.json
       // ZWCAD 版:    %APPDATA%\ZwcadBatchPlot\TitleBlockLibrary.json
```

### 2.2 关键代码路径

| 步骤 | 代码位置 |
|------|---------|
| 获取块名 | `CadTextExtractor.GetBlockName()` — [ZWCAD](src/ZWCAD/CadTextExtractor.cs#L23) / [AutoCAD](src/AutoCAD/CadTextExtractor.cs#L23) |
| 深入动态块 | `BatchPlotCommands.TryGetVisibleNestedBlock()` — [line 951](src/Common/BatchPlotCommands.cs#L951)，入口有 `IsDynamicBlock` 守卫 |
| 可见性判断 | `IsEntityVisible()` — 调用 `entity.Visible` 属性，try/catch 兜底 |
| 坐标变换 | `BatchPlotCommands.TransformRegion/TransformExtents/ToFrameRelative` |
| 纸张检测 | `PaperSizeDetector.Detect()` — [PaperSizeDetector.cs](src/Common/PaperSizeDetector.cs) |
| 持久化 | `TitleBlockLibraryStore.Upsert()` — [ZWCAD](src/ZWCAD/TitleBlockLibraryStore.cs) / [AutoCAD](src/AutoCAD/TitleBlockLibraryStore.cs) |

### 2.3 动态块 vs 普通块

```
场景 A：普通块 "A2图框"
  └─ GetBlockName → "A2图框" → 直接作为库 key → 存储 ✅

场景 B：动态块 "【地铁院】图框" (可见性=A2)
  ├─ GetBlockName → "【地铁院】图框"
  │   // 动态块返回的是 DynamicBlockTableRecord.Name
  ├─ TryGetVisibleNestedBlock → 深入匿名定义
  │   ├─ 嵌套块 A0 (entity.Visible=false) → 跳过，隐藏状态
  │   ├─ 嵌套块 A1 (entity.Visible=false) → 跳过，隐藏状态
  │   ├─ 嵌套块 A2 (entity.Visible=true)  → ✅ 当前可见状态
  │   └─ 嵌套块 A3 (entity.Visible=false) → 跳过，隐藏状态
  └─ 库 key = "A2" → 存储 ✅
      // 以可见内层块名入库，下次扫描时从外层深入匹配内层
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
  │       ├─ ⑤ CadTextExtractor.ExtractRegionText() → 提取图名/图号
  │       │      // 三级优先级: Attribute(最高) > OwnerSpace > BlockDefinition(最低)
  │       │      // 文字清洗: %%C→Φ, %%D→°, 移除 MTEXT 格式码
  │       ├─ ⑥ PaperSizeDetector.Detect(宽度, 高度) → 纸张识别
  │       │      // 库中有固定纸张则优先使用
  │       │      // 加长图优先使用实际检测尺寸
  │       └─ ⑦ new PlotJob { ... } → 加入结果列表
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

  ① GetBlockName(blockRef) → "【地铁院】图框"
     // DynamicBlockTableRecord.Name，不是匿名名 *U12
  ② 查库: "【地铁院】图框" → 没有 ❌

  ③ ResolveNestedLibraryMatch: 深入 *U12
     // 遍历匿名定义中的所有嵌套块，看谁当前可见且库中有登记
     ├─ BlockRef → "A0" (entity.Visible=false) → 跳过
     ├─ BlockRef → "A1" (entity.Visible=false) → 跳过
     ├─ BlockRef → "A2" (entity.Visible=true)  → 查库: "A2" ✅ 匹配!
     └─ BlockRef → "A3" (entity.Visible=false) → 跳过

  ④ 加载 "A2" 的 TitleBlockDefinition:
     CoordinateMode=Frame, PrintRegion/PanelRegion/NumberRegion 等
  ⑤ 提取文字, ⑥ 检测纸张, ⑦ 生成 PlotJob
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

**触发**：用户运行 `ZBP_RECTANGLE_BATCH_PLOT`，打开 RectangleBatchPlotForm（先弹窗，后扫描）。

> 注意：与图框库模式不同，矩形框批打采用"先弹窗后扫描"的 UX 设计。
> 用户打开面板后，点击"扫描当前图"（选择范围）或"框选扫描"（框选区域）触发扫描，
> 而非打开命令后立即扫描。

### 4.1 整体流程

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

#### ScanWindow 单布局流程

```
RectangleFrameScanner.ScanWindow(Document, scanWindow)
  │
  ├─ 确定扫描目标空间:
  │   ├─ TileMode=1 (模型空间) → 扫描 BlockTableRecord.ModelSpace
  │   └─ TileMode=0 (图纸空间) → 获取当前布局的 BlockTableRecord
  │       // 用 LayoutManager.Current.CurrentLayout 而不是 CurrentSpaceId
  │       // 防止用户在视口内编辑时扫到模型空间
  │
  ├─ 遍历目标空间中所有顶层实体
  │   └─ 每个实体 → CollectEntityRectangles(entity, Identity, ...)
  │       │
  │       ├─ ① 是 Polyline?
  │       │   ├─ 跳过 ZBP_TEMP_SEQUENCE_OVERLAY 临时图层
  │       │   ├─ IsEntityLayerScannable(tr, entity)
  │       │   │   // !Off && !Frozen && IsPlottable → 只扫可打印图层的实体
  │       │   ├─ TryGetRectangle(polyline, transform)
  │       │   │   // 顶点≥4, 无bulge(圆弧), 闭合
  │       │   │   // 去连续重复点, 去共线点 → 必须剩4个直角顶点
  │       │   ├─ PaperSizeDetector.DetectCandidates(width, height)
  │       │   │   // 宽高比匹配 A0~A3 常见尺寸 → 至少返回1个候选
  │       │   └─ rectangles.Add(rectangle) ✅
  │       │
  │       └─ ② 是 BlockReference? (depth ≤ 12)
  │           ├─ XCLIP 裁切检查: IsBlockClipped(tr, blockRef)
  │           │   // 扩展字典中存在 "ACAD_FILTER" → 跳过整个块参照
  │           ├─ visitedDefinitions.Add(definitionId)
  │           │   // 防循环引用: 同一个块定义只处理一次
  │           ├─ 进入块定义 (BlockTableRecord)
  │           ├─ nestedTransform = blockRef.BlockTransform * parentTransform
  │           │   // 矩阵累积，子实体坐标变换到世界坐标系
  │           ├─ 遍历定义内所有子实体:
  │           │   ├─ IsEntityLayerScannable? → 图层过滤
  │           │   │   // !Off && !Frozen && IsPlottable
  │           │   ├─ IsEntityVisible(entity.Visible)? → 可见性过滤
  │           │   │   // CAD 引擎原生判断，动态块隐藏状态自动为 false
  │           │   │   // try/catch 兜底 → 取不到时返回 true（宁可多扫）
  │           │   └─ CollectEntityRectangles(子实体, nestedTransform, ...)
  │           │       // 递归: Polyline 走分支①, BlockReference 走分支②
  │           └─ finally: visitedDefinitions.Remove(definitionId)
  │               // 离开时移除，允许不同路径再次进入（不同父级下的同一定义）
  │
  └─ 过滤打包 (FilterAndPackageRectangles):
      ├─ ① 窗口裁剪 → Intersects(rectangle, scanWindow)
      ├─ ② 纸张比例过滤 → DetectCandidates 必须有至少1个候选
      ├─ ③ FilterRectangles()
      │   ├─ 按面积降序
      │   ├─ 去重: 重叠率 ≥ 90% + 宽高相似度 ≥ 90% → 保留一个
      │   └─ 去嵌套: 小矩形完全在大矩形内 + 大面积 ≥ 小面积 × 1.5 → 移除小的
      ├─ ④ FilterEmptyRectangles()
      │   // 检查每个候选矩形内是否存在可见、可打印的绘图实体
      │   // 遍历布局所有实体（含块内嵌套），检查 GeometricExtents 是否与目标矩形相交
      │   // 矩形框多段线自身不计为"内容"
      └─ ⑤ 生成 Result 列表
          ├─ PaperSizeDetector.DetectCandidates(width, height) → 候选纸张
          ├─ 取最优匹配 paper = options[0]
          └─ 返回 List<Result>（每个 Result 含 PlotJob + 候选纸张列表）
```

### 4.2 关键代码路径

| 步骤 | 代码位置 |
|------|---------|
| 扫描入口（单窗口） | `RectangleFrameScanner.ScanWindow()` — [line 55](src/Common/RectangleFrameScanner.cs#L55) |
| 扫描入口（多布局） | `RectangleFrameScanner.ScanScope()` — [line 104](src/Common/RectangleFrameScanner.cs#L104) |
| TabOrder 排序 | `spaceData.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder))` — [line 138](src/Common/RectangleFrameScanner.cs#L138) |
| 递归遍历 | `CollectEntityRectangles()` — [line 350](src/Common/RectangleFrameScanner.cs#L350) |
| 矩形检测 | `TryGetRectangle()` — 顶点≥4, bulge=0, 闭合, 去重后=4直角顶点 |
| 图层过滤 | `IsEntityLayerScannable()` — !Off && !Frozen && IsPlottable |
| 可见性过滤 | `IsEntityVisible()` — `entity.Visible`，try/catch 兜底为 true |
| XCLIP 过滤 | `IsBlockClipped()` — 检查扩展字典 `ACAD_FILTER` — [line 490](src/Common/RectangleFrameScanner.cs#L490) |
| 去重去嵌套 | `FilterRectangles()` — [line 305](src/Common/RectangleFrameScanner.cs#L305)（已更新行号请以实际为准） |
| 空框过滤 | `FilterEmptyRectangles()` — [line 841](src/Common/RectangleFrameScanner.cs#L841) |
| 纸张比例过滤 | `PaperSizeDetector.DetectCandidates()` — [PaperSizeDetector.cs](src/Common/PaperSizeDetector.cs) |

### 4.3 三种场景的识别逻辑

#### 场景 A：直接 Polyline（不在任何块里）

```
图纸空间
  └─ Polyline (矩形, layer=0)

CollectEntityRectangles(Polyline, Identity):
  // 第130行: 是 Polyline → 直接检测
  IsEntityLayerScannable? → layer 0 可打印 ✅
  TryGetRectangle? → 4顶点, 直角, 闭合 ✅
  DetectCandidates? → 匹配 A2 ✅
  rectangles.Add(...)  // 命中
  第142行: 不是 BlockReference → return  // 结束
```

#### 场景 B：普通块内含 Polyline

```
图纸空间
  └─ BlockRef → "MyFrame" (layer=0, IsDynamicBlock=false)
       └─ 块定义 "MyFrame"
            └─ Polyline (矩形, layer=0)

CollectEntityRectangles(BlockRef"MyFrame", Identity):
  第130行: 不是 Polyline → 跳过
  第142行: 是 BlockReference, depth=0 < 12 ✅
  第148行: visitedDefinitions.Add("MyFrame") → 首次 ✅
  第155行: 获取块定义 "MyFrame"
  foreach 子实体:
    Polyline → IsEntityLayerScannable ✅
    // 第179行: 不是 BlockReference → 不触发可见性过滤
    → CollectEntityRectangles(Polyline, blockTransform)
      第130行: 是 Polyline + blockTransform 变换坐标 → ✅ 命中
```

#### 场景 C：动态块（可见性控制不同尺寸）

```
图纸空间
  └─ BlockRef → *U12 (动态块, IsDynamicBlock=true, Visibility 当前=A2)
       └─ 匿名定义 *U12
            ├─ BlockRef → "Frame_A0" (layer=0, entity.Visible=false)  隐藏
            │    └─ 定义含 Polyline A0尺寸
            ├─ BlockRef → "Frame_A1" (layer=0, entity.Visible=false)  隐藏
            │    └─ 定义含 Polyline A1尺寸
            ├─ BlockRef → "Frame_A2" (layer=0, entity.Visible=true)   显示 ✅
            │    └─ 定义含 Polyline A2尺寸
            └─ BlockRef → "Frame_A3" (layer=0, entity.Visible=false)  隐藏
                 └─ 定义含 Polyline A3尺寸

CollectEntityRectangles(BlockRef*U12, Identity):
  第130行: 不是 Polyline → 跳过
  第142行: 是 BlockReference ✅
  第148行: visitedDefinitions.Add("*U12") ✅
  第155行: 获取匿名定义 *U12
  foreach 子实体:
    Frame_A0: IsEntityLayerScannable? → layer 0, plottable ✅
              IsEntityVisible? → entity.Visible = false ❌ → continue 跳过
    Frame_A1: 同上 → 跳过
    Frame_A2: IsEntityLayerScannable? → ✅
              IsEntityVisible? → true ✅ → 递归进入
              → 进入 Frame_A2 定义 → 找到 Polyline → A2 矩形 ✅
    Frame_A3: 同上 → 跳过

关键: entity.Visible 是 CAD 引擎原生维护的属性
动态块切换可见性状态时，CAD 自动将隐藏状态的 Visible 设为 false
不需要猜名字、不需要猜图层 — 四个嵌套块全在 layer 0 一样能正确区分
```

---

## 5. 流程四：单张打印

**触发**：用户运行 `ZBP_SINGLE_PLOT`，手动框选图纸外框 → 自动识别纸张 → 直接输出 PDF。

### 5.1 整体流程

```
用户框选两个角点
  │
  ├─ ① editor.GetPoint("\n选择图纸外框第一个角点: ")
  │     // 用户点击图纸外框的左上/左下角
  ├─ ② editor.GetCorner("\n选择图纸外框对角点: ", firstPoint)
  │     // 用户点击对角点，CAD 自动计算矩形
  │
  ├─ ③ 计算世界坐标矩形
  │     minX = Min(p1.X, p2.X)
  │     minY = Min(p1.Y, p2.Y)
  │     maxX = Max(p1.X, p2.X)
  │     maxY = Max(p1.Y, p2.Y)
  │     width = maxX - minX
  │     height = maxY - minY
  │     // 宽高 ≤ 1e-6 → 无效，提示用户重新选择
  │
  │
  ├─ ④ PaperSizeDetector.DetectCandidates(width, height)
  │     // 用世界坐标下的实际宽高匹配常见纸张 × 常用比例
  │     // 例: 59400×42000 → A2 × 100 → 1:100
  │     ├─ 有候选 → 继续
  │     │   // 只有一个候选: 直接使用
  │     │   // 有多个候选: 弹出 SinglePlotPaperSelectionForm 让用户选择
  │     └─ 无候选 → 进入自定义纸张流程（详见 5.4）
  │         ├─ GuessScale(width, height) → 推测整比例
  │         ├─ CustomScaleForm 弹窗确认比例
  │         ├─ InstallBundledPlotter() 确保打印机已安装
  │         ├─ PmpCustomPaper.RegisterCustomPaper() 写入 PMP
  │         ├─ 组装自定义纸张候选
  │         └─ finally: RemoveCustomPaper() 清理 PMP
  │
  ├─ ⑤ 选择输出路径
  │     SaveFileDialog:
  │       默认文件名 = DWG文件名.pdf
  │       默认目录 = DWG 所在目录
  │       筛选器 = "PDF 文件 (*.pdf)|*.pdf"
  │
  ├─ ⑥ 组装 PlotJob
  │     {
  │       IsManualWindow = true,
  │       SourceFile = 当前DWG路径,
  │       SpaceName = 当前布局名,
  │       IsPaperSpace = !TileMode,
  │       DrawingNumber = 文件名（无扩展名）,
  │       Title = 文件名,
  │       PaperName/Width/Height = 纸张检测结果,
  │       MinX/Y/MaxX/Y = 用户框选的世界坐标矩形,
  │       OutputPath = 用户选择的PDF路径
  │     }
  │
  ├─ ⑦ PlotterService.Plot(job, deviceName, styleSheet, doc, settings)
  │     // 直接调用单 Job 打印，不走 PlotMany 分组逻辑
  │     // 和批量打印用的是同一个引擎（见第6章）
  │
  └─ ⑧ 完成提示
        MessageBox: "单张打印完成。纸张: A2 594×420mm"
        editor.WriteMessage → CAD 命令行输出文件路径
        RevealFileInExplorer → 打开资源管理器定位到 PDF
```

### 5.2 关键代码路径

| 步骤 | 代码位置 |
|------|---------|
| 命令入口 | `BatchPlotCommands.SinglePlotCore()` — [line 427](src/Common/BatchPlotCommands.cs#L427) |
| 用户框选 | `editor.GetPoint()` + `editor.GetCorner()` — CAD 原生交互 |
| 纸张检测 | `PaperSizeDetector.DetectCandidates()` — 返回候选列表 |
| 纸张选择 | `SinglePlotPaperSelectionForm` — 仅在多候选时弹出 |
| 输出路径 | `SaveFileDialog` — 系统原生保存对话框 |
| 打印机选择 | `ResolveSinglePlotOptions()` — 复用批打的选择逻辑 |
| 打印 | `PlotterService.Plot()` — [ZWCAD](src/ZWCAD/PlotterService.cs) / [AutoCAD](src/AutoCAD/PlotterService.cs) |

### 5.3 与批量打印的差异

| 方面 | 单张打印 | 批量打印（图框库/矩形框） |
|------|---------|------------------------|
| 扫描方式 | 用户手动框选两个角点 | 自动扫描图纸中所有匹配的块/矩形 |
| 纸张确认 | 多候选时弹窗选择 | 默认用第一个候选，用户可在面板中调整 |
| 输出路径 | 每次弹 SaveFileDialog | 统一输出目录 + 自动命名 |
| 多文件处理 | 只处理当前 DWG | 可跨 DWG 文件扫描和打印 |
| 图名图号 | 使用文件名 | 从图框块中自动提取文字 |
| 合并 PDF | 不支持 | 支持（PdfDocumentService.Merge） |
| 自定义纸张 | 支持非标尺寸（GuessScale + CustomScaleForm + PmpCustomPaper） | 不支持（仅标准纸张） |

### 5.4 自定义纸张尺寸（非标图纸）

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
  │   // 取最接近标准短边 (210/297/420/594/841) 的比例
  │
  ├─ ② CustomScaleForm(width, height, guessedScale)
  │   // 弹窗显示当前图形尺寸和推测比例
  │   // 用户可调整整数比例值
  │   // 根据所选比例反算纸张尺寸: paperW = drawingW / scale
  │
  ├─ ③ AcadPlotterInstaller.InstallBundledPlotter()
  │   // 确保 LA_pdf 打印机已安装（必须在 PMP 修改前执行）
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
  ├─ ⑤ 组装自定义纸张候选
  │   // PaperName = customPaperName ?? "UserDefined"
  │   // ScaleValue = scale
  │   // Note = "自定义纸张 W x H mm"
  │
  ├─ ⑥ 弹出 SinglePlotForm 让用户确认（含预览/路径/纸张选择）
  │
  └─ ⑦ finally: PmpCustomPaper.RemoveCustomPaper(pmpPath, paperName)
       // 无论打印成功或失败，清理 PMP 中的自定义条目
       // 防止污染用户 PMP 文件
```

**PIA 版本适配**：

| AutoCAD 版本 | PMP 格式 | 读/写方式 |
|-------------|----------|----------|
| 2024+ | PIA 3.0 JSON | Newtonsoft.Json 解析/修改 JSON |
| 2019-2023 | PIA 2.0 压缩 | PianNoCN 库解压→修改→重新压缩 |
| ZWCAD | INI 文本 | Regex 匹配 `[Meta]/[user]` 段 |

**判断代码路径**：

| 步骤 | 代码位置 |
|------|---------|
| 比例推测 | `PaperSizeDetector.GuessScale()` — [line 58](src/Common/PaperSizeDetector.cs#L58) |
| 自定义比例对话框 | `CustomScaleForm` — [CustomScaleForm.cs](src/Common/CustomScaleForm.cs) |
| PMP 注册（入口） | `PmpCustomPaper.RegisterCustomPaper()` — [PmpCustomPaper.cs](src/Common/PmpCustomPaper.cs#L26) |
| PMP 清理 | `PmpCustomPaper.RemoveCustomPaper()` — [line 56](src/Common/PmpCustomPaper.cs#L56) |
| PIA 版本检测 | `PmpPiaConverter.IsCadPia3Compatible()` — [PmpPiaConverter.cs](src/Common/PmpPiaConverter.cs#L14) |
| PIA 3→2 转换 | `PmpPiaConverter.ConvertToPia2()` — [line 31](src/Common/PmpPiaConverter.cs#L31) |
| PIA 2.0 序列化 | `PlotterConfiguration` / `PiaSerializer` — [src/PianNoCN/](src/PianNoCN/) |
| 打印前安装 | `AcadPlotterInstaller.InstallBundledPlotter()` — 平台特有 |
| 主流程 | `BatchPlotCommands.SinglePlotCore()` — [SinglePlotCommands.cs](src/Common/SinglePlotCommands.cs) |

---

## 6. 打印引擎

**触发**：用户在 `BatchPlotForm` 或 `RectangleBatchPlotForm` 中点击"打印"，或运行单张打印。

### 6.1 打印整体流程

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
                 │   // 选择 PDF 打印机: "LA_pdf.pc3" (AutoCAD) / "LA_pdf.pc5" (ZWCAD)
                 ├─ ChooseMedia(mediaNames, paperWidth, paperHeight)
                 │   // AutoCAD: 复杂匹配+旋转候选+加长纸处理
                 │   // ZWCAD: SelectMedia 简单匹配
                 ├─ 配置打印参数:
                 │   ├─ PlotType = Window
                 │   ├─ PlotWindow = (job.MinX, job.MinY) → (job.MaxX, job.MaxY)
                 │   │   // 打印窗口 = 图框包围盒
                 │   ├─ StandardScale = ScaleToFit
                 │   ├─ PlotCentered = true
                 │   ├─ PlotRotation = 自动 (比较宽高比，一致则0°否则90°)
                 │   ├─ ShadePlotType = AsDisplayed
                 │   └─ CustomPrintScale (微调比例精度)
                 │
                 └─ 逐 Job 输出 PDF:
                      ├─ plotInfo.DeviceOverride → job.OutputPath (.pdf)
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
                      └─ ValidatePdfOutput(job.OutputPath)
                           // PdfSharp 读取验证: 文件存在、非空、至少 1 页
                           // 验证失败 → 标记为失败，不阻塞后续 Job
```

### 6.2 关键代码路径

| 组件 | 文件 |
|------|------|
| 批量打印入口 | `PlotterService.PlotMany()` — [ZWCAD](src/ZWCAD/PlotterService.cs) / [AutoCAD](src/AutoCAD/PlotterService.cs) |
| 单文件打印 | `PlotterService.PlotDatabase()` — 配置 PlotSettings、循环 RunPlot |
| 单 Job 打印 | `PlotterService.Plot()` — PlotMany 的便捷包装 |
| 纸张匹配 | `ChooseMedia()` (AutoCAD) / `SelectMedia()` (ZWCAD) |
| PDF 验证 | `PlotterService.ValidatePdfOutput()` |
| 打印机安装 | `AcadPlotterInstaller.InstallBundledPlotter()` — 复制 LA_pdf.* 到 CAD 目录 |
| PDF 合并 | `PdfDocumentService.Merge()` — [PdfDocumentService.cs](src/Common/PdfDocumentService.cs) |

### 6.3 输出文件命名

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

## 7. PDF 合并

**触发**：打印时勾选"合并 PDF"，或在 `BatchPlotForm` / `RectangleBatchPlotForm` 中生成 PDF 后自动合并。

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

## 8. UCS 坐标变换

v1.10.0 全面支持用户坐标系（UCS）。三个打印功能共用同一套变换链路。

### 10.1 核心原则

**四点变换，一次包围盒。** 将实际角点一步变换到 DCS，只取一次包围盒。避免中间取 WCS 包围盒导致的重复放大。

### 10.2 变换矩阵

```
BuildUcsToDcsMatrix = UCS→WCS × WCS→DCS
BuildWcsToDcsMatrix = PlaneToWorld × Displacement × Rotation → Inverse

UCS=WCS 时所有矩阵退化为单位矩阵，行为不变。
```

两个方法在 [CoordinateUtils.cs](src/Common/CoordinateUtils.cs)。

### 10.3 三个功能的变换路径

| 功能 | 输入坐标系 | 变换 | 输出 |
|------|-----------|------|------|
| 单张打印 | UCS 角点 | `BuildUcsToDcsMatrix` | DCS 包围盒 → PlotJob |
| 矩形框批量 | WCS 角点 (CornerPoints) | `BuildWcsToDcsMatrix` | DCS 包围盒 → PlotJob |
| 图框块批量 | WCS 角点 (ComputeWcsCorners) | `BuildWcsToDcsMatrix` | DCS 包围盒 → PlotJob |

三条路径殊途同归：`IsDcsWindow=true` → `GetPlotWindow` 跳过 → `PrepareEditorViewForPlot` 跳过。

### 10.4 图框块 vs 矩形框的关键差异

| | 图框块 | 矩形框 |
|--|-------|-------|
| 角点来源 | 图框库参考框 4 角 × BlockTransform | 多段线实际顶点 |
| 存储 | `PlotJob.CornerPoints` | `LocalRectangle.CornerPoints` |
| 世界坐标获取 | `ComputeWcsCorners`（不取包围盒） | `TryGetRectangle`（实际顶点） |
| BlockTransform 影响 | 缩放、旋转自动跟随 | N/A（多段线已在 WCS） |

两者最终都是四点 × `WCS→DCS` 取一次包围盒。

### 10.5 框选范围 UCS 处理

所有 `GetPoint/GetCorner` 框选均用四点法：UCS 两个对角点展开为四个角点 × `UCS→WCS` 取一次包围盒，不在中间用 `Math.Min/Max` 放大。

### 10.6 Overlay UCS 跟随

红框和数字按 UCS X 轴角度旋转后绘制到 WCS，保证任何 UCS 视图下显示为正。

---

## 9. 动态块处理

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
| 新增图框 | `BatchPlotCommands.TryGetVisibleNestedBlock` | 定位当前可见嵌套块名入库 | `IsDynamicBlock` — 普通块直接返回 false |
| 图框库扫描 | `TitleBlockScanner.ResolveNestedLibraryMatch` | 深入动态块找可见嵌套块的库匹配 | 仅 `definition==null` 时触发 |

### 10.3 GetBlockName 的行为

```csharp
// CadTextExtractor.GetBlockName:
//   普通块: 返回 BlockTableRecord.Name  →  "A2图框"
//   动态块: 返回 DynamicBlockTableRecord.Name  →  "【地铁院】图框"
//
//   关键: 动态块的 BlockTableRecord 是匿名块 (*U12)
//        但 GetBlockName 绕过了它，返回的是非匿名的有效块名
//        避免把 *U12 泄露给图框库或用户界面
```

### 10.4 新增图框时的完整链路

```
用户选择动态块 "【地铁院】图框"
  → GetBlockName → "【地铁院】图框"
  → TryGetVisibleNestedBlock:
      IsDynamicBlock? → true ✅ → 深入匿名定义
      entity.Visible 过滤 → 找到内层可见块 → "A2"
  → 库 key = "A2"

用户选择普通块 "MyFrame" (内有嵌套块)
  → GetBlockName → "MyFrame"
  → TryGetVisibleNestedBlock:
      IsDynamicBlock? → false ❌ → 直接返回 false，不走嵌套逻辑
  → 库 key = "MyFrame"（普通块行为，不受影响）

下次扫描该图纸时:
  → 图纸中有 BlockRef → "【地铁院】图框"
  → GetBlockName → "【地铁院】图框" → 库中无直接匹配
  → ResolveNestedLibraryMatch → 深入匿名定义
      entity.Visible 过滤 → 找到内层可见块 → "A2"
  → 库中 "A2" ✅ 匹配 → 加载坐标区域 → 提取文字 → 生成 PlotJob
```

---

## 10. ZWCAD vs AutoCAD 差异

### 10.1 条件编译

```csharp
#if AUTOCAD
    using Autodesk.AutoCAD.DatabaseServices;  // AutoCAD API
#else
    using ZwSoft.ZwCAD.DatabaseServices;      // ZWCAD API
#endif
```

所有 `src/Common/` 下的文件使用 `#if AUTOCAD` 条件编译，共享逻辑不变，仅切换命名空间。

### 10.2 平台差异清单

| 方面 | AutoCAD | ZWCAD |
|------|---------|-------|
| 命名空间 | `Autodesk.AutoCAD.*` | `ZwSoft.ZwCAD.*` |
| 绘图仪配置 | `LA_pdf.pc3` | `LA_pdf.pc5`（需模板替换 PMP 路径） |
| 打印纸张匹配 | 复杂权重排序, 支持旋转, 多候选 | `MediaSelection` 简化匹配 |
| Core Console | `ACAD_CORE` 宏, 无菜单栏, 不同对话框 API | 无此概念 |
| 菜单命令前缀 | 无 `^C^C` | 需 `^C^C`（取消当前命令再执行） |
| 自动加载注册表 | `HKCU\Software\Autodesk\AutoCAD` | `HKCU\Software\ZWSOFT\ZWCAD` |
| 图框库路径 | `%APPDATA%\AcadBatchPlot\` | `%APPDATA%\ZwcadBatchPlot\` |
| 图框库迁移 | 首次加载时自动从 ZWCAD 路径导入 | 无迁移逻辑 |
| 动态块 API | `IsDynamicBlock` / `DynamicBlockTableRecord` 稳定 | 老版本可能异常 → 已用 try/catch 保护 |

### 10.3 编译项目对应

| .csproj | 平台 | Target | Output |
|---------|------|--------|--------|
| `BatchPlotter.csproj` | ZWCAD | net48 | `bin\BatchPlotter.dll` |
| `AcadBatchPlot.csproj` | AutoCAD 2019-2024 | net48 | `bin-acad\AcadBatchPlot.dll` |
| `AcadBatchPlot.AutoCAD2019.csproj` | AutoCAD 2019 | net47 | `bin-acad2019\AcadBatchPlot.dll` |
| `AcadBatchPlot.Core.csproj` | AutoCAD 2025+ Core | net8.0-windows | `bin-acad-core\AcadBatchPlot.Core.dll` |

> AutoCAD 2016-2018 项目已移除，最低支持 AutoCAD 2019。

---

## 11. 项目文件结构

```
批量打印/
├── ARCHITECTURE.md              ← 本文档
├── AcadBatchPlot.csproj         ← AutoCAD 2019-2024 编译入口 (net48)
├── AcadBatchPlot.AutoCAD2019.csproj ← AutoCAD 2019 专用编译入口 (net47)
├── AcadBatchPlot.Core.csproj    ← AutoCAD 2025+ Core 编译入口 (net8.0-windows)
├── BatchPlotter.csproj          ← ZWCAD 编译入口 (net48)
├── Directory.Build.props        ← 共享 MSBuild 属性
│
├── src/
│   ├── Common/                  ← 双平台共享代码 (#if AUTOCAD)
│   │   ├── BatchPlotCommands.cs     ← 命令入口 + 窗口扫描 + 工具方法 (partial class)
│   │   ├── CoordinateUtils.cs       ← UCS/DCS 坐标变换矩阵 (partial class)
│   │   ├── SinglePlotCommands.cs    ← 单张打印核心 + 打印机选择 + 自定义纸张 (partial class)
│   │   ├── AddTitleBlockCommands.cs ← 新增图框向导 + 动态块可见性 (partial class)
│   │   ├── BatchPlotForm.cs         ← 批量打印主面板 (图框库匹配模式)
│   │   ├── RectangleBatchPlotForm.cs ← 批量打印面板 (矩形框扫描模式, 先弹窗后扫描, TabOrder 分组 + 行列排序)
│   │   ├── SinglePlotForm.cs        ← 单张打印确认面板（预览/纸张/路径）
│   │   ├── TitleBlockScanner.cs     ← 图框库扫描器: 扫描→匹配→生成PlotJob
│   │   ├── RectangleFrameScanner.cs ← 矩形框扫描器: 递归扫描 → XCLIP过滤 → 空框过滤 → TabOrder排序 → 生成PlotJob
│   │   ├── PaperSizeDetector.cs     ← 纸张尺寸检测: A0~A3标准/加长 + GuessScale (非标图纸比例推测)
│   │   ├── Models.cs               ← 数据模型: PlotJob, TitleBlockDefinition, LocalRectangle
│   │   ├── AppSettingsStore.cs      ← 设置持久化 (JSON)
│   │   ├── PdfDocumentService.cs    ← PDF 合并 (PdfSharp)
│   │   ├── SettingsForm.cs          ← 设置面板 (双Tab: 通用 + 目录表)
│   │   ├── TitleBlockLibraryManagerForm.cs ← 图框库管理面板
│   │   ├── PaperSizeSelectionForm.cs ← 新增图框时纸张选择对话框
│   │   ├── SinglePlotPaperSelectionForm.cs ← 单张打印纸张选择对话框
│   │   ├── DwgSplitService.cs       ← DWG 拆分 (模型空间WBLOCK / 布局空间复制)
│   │   ├── CsvExporter.cs           ← CSV 导出
│   │   ├── FileNameSanitizer.cs     ← 文件名清洗: 非法字符、路径过长
│   │   ├── NaturalStringComparer.cs ← 自然排序: "JZ-02" < "JZ-10"
│   │   ├── UiLayout.cs              ← WinForms 布局: DPI缩放、按钮创建
│   │   ├── BatchPlotLogger.cs       ← 日志输出
│   │   ├── DirectoryTableGenerator.cs ← 图纸目录表生成: 在CAD中绘制表格
│   │   ├── PmpCustomPaper.cs        ← PMP 自定义纸张注册/删除 (PIA3 JSON / PIA2 / ZWCAD INI)
│   │   ├── PmpPiaConverter.cs       ← PIA 版本检测 + PIA 3→2 转换
│   │   ├── CustomScaleForm.cs       ← 非标图纸整数比例选择对话框
│   │   └── TemporarySequenceOverlay.cs ← 打印序号标注: 红框+数字，点击高亮
│   │
│   ├── PianNoCN/                 ← PIA 2.0 文件格式序列化（仅 AutoCAD 编译, namespace PiaNO）
│   │   ├── Pia/
│   │   │   ├── PiaFile.cs           ← PIA 文件容器
│   │   │   ├── PiaNode.cs           ← PIA 树节点
│   │   │   ├── PiaHeader.cs         ← PIA 文件头
│   │   │   ├── PiaSerializer.cs     ← deflate 解压/序列化
│   │   │   ├── PiaException.cs      ← 异常类型
│   │   │   └── EnumDecompressionType.cs
│   │   └── Plot/
│   │       ├── PlotterConfiguration.cs ← 绘图仪配置类型访问
│   │       └── Media.cs
│   │
│   ├── AutoCAD/                  ← AutoCAD 专用实现
│   │   ├── CadTextExtractor.cs   ← 文字提取: XCLIP 过滤, 属性/文字/多行文字/MText
│   │   ├── CadTextUpdater.cs     ← 文字回写: 将图号图名写回DWG
│   │   ├── CadMenuInstaller.cs   ← 菜单安装: 创建"批量打印"菜单
│   │   ├── PlotterService.cs     ← 打印引擎: PlotMany→PlotDatabase→RunPlot
│   │   ├── AcadPlotterInstaller.cs ← 打印机安装: 复制 LA_pdf.pc3 到CAD目录
│   │   ├── AutoloadManager.cs    ← 自动加载: 注册表写入/卸载
│   │   ├── TitleBlockLibraryStore.cs ← 图框库持久化:+ZWCAD迁移逻辑
│   │   └── ScanDiagnostics.cs    ← 调试命令: 矩形扫描诊断
│   │
│   └── ZWCAD/                    ← ZWCAD 专用实现（接口同名，平台适配）
│       ├── CadTextExtractor.cs   ← 同AutoCAD (XCLIP 过滤 +), 动态块API用try/catch保护
│       ├── CadTextUpdater.cs
│       ├── CadMenuInstaller.cs   ← 菜单命令前缀加 ^C^C
│       ├── PlotterService.cs     ← 简化纸张匹配, LA_pdf.pc5
│       ├── AcadPlotterInstaller.cs ← pc5模板替换PMP路径
│       ├── AutoloadManager.cs    ← 注册表路径: ZWSOFT\ZWCAD
│       └── TitleBlockLibraryStore.cs ← 无跨平台迁移, 路径 ZwcadBatchPlot
│
├── lib/
│   └── PianNoCN/                 ← PianNoCN 原始上游源码 (参考用，编译时使用 src/PianNoCN/)
│
├── resources/
│   ├── acad/Plotters/            ← AutoCAD PDF 打印机配置
│   │   ├── PIA3/                    ← PIA 3.0 JSON 格式 (AutoCAD 2024+)
│   │   │   ├── LA_pdf.pc3
│   │   │   └── PMP Files/LA_pdf.pmp
│   │   ├── PIA2/                    ← PIA 2.0 压缩格式 (AutoCAD 2019-2023)
│   │   │   ├── LA_pdf.pc3
│   │   │   └── PMP Files/LA_pdf.pmp
│   │   └── README.md
│   └── zwcad/Plotters/           ← ZWCAD PDF 打印机配置 (INI 格式)
│       ├── LA_pdf.pc5
│       └── PMP Files/LA_pdf.pmp
│
├── bin/                          ← ZWCAD 编译输出
├── bin-acad/                     ← AutoCAD 2019-2024 编译输出
├── bin-acad2019/                 ← AutoCAD 2019 专用编译输出
├── bin-acad-core/                ← AutoCAD 2025+ Core 编译输出
├── bin-tmp/ bin-new/            ← 临时编译输出（bin 被锁时）
│
├── release/                      ← 发布包
│
└── tests/
    └── RobustnessTests/          ← 单元测试项目
```

---

## 附录：外部依赖

| 包 | 用途 | 保留原因 |
|----|------|---------|
| `Newtonsoft.Json` 13.0.3 | JSON 序列化 | 设置、图框库、日志均依赖 |
| `PDFsharp` 1.50.5147 | PDF 合并、验证、页数检查 | 打印后自动验证和合并 |
| `SharpZipLib` 1.3.3–1.4.2 | PIA 2.0 deflate 解压/压缩 | PianNoCN 序列化依赖，用于 AutoCAD 2019-2023 PMP 修改 |
| `AutoCAD.NET` (20.0.1 / 23.0.0 / 25.0.0) | AutoCAD .NET API | 仅 AutoCAD 版本，运行时由 CAD 提供 |
| `ZwManaged.dll` / `ZwDatabaseMgd.dll` | ZWCAD .NET API | 仅 ZWCAD 版本，运行时由 ZWCAD 提供 |
| `PianNoCN` (内嵌源码) | PIA 2.0 文件格式解析 | 自定义纸张时读取/写入 PIA 2.0 压缩 PMP |

已移除的依赖: `Microsoft.Web.WebView2`（原用于 PDF 内嵌预览，PdfPreviewForm 已移除）
