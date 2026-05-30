# BatchPlotter 改进建议与现存 Bug 分析

> 基于全部 18 个源文件的静态代码审查，按严重程度和文件组织。

---

## 🐛 Bug（运行时缺陷）

### B1. `PaperSizeSelectionForm.Clamp()` 对高度使用错误的控件范围

**文件**: `src/PaperSizeSelectionForm.cs#L152-L155`

```csharp
private decimal Clamp(double value)
{
    return (decimal)Math.Max((double)_width.Minimum, Math.Min((double)_width.Maximum, value));
}
```

无论传入的是宽度还是高度，都使用 `_width.Minimum` 和 `_width.Maximum` 做 clamp。对 `_height` 调用时如果 `_width.Maximum`（默认 5000）和 `_height.Maximum`（默认 5000）恰好相同则没问题，但如果未来两个 NumericUpDown 的 Maximum 被配置为不同值，会错误截断高度。

**影响**: 当前配置下不会触发，但代码意图与实现不一致，属于隐蔽 bug。

---

### B2. `TitleBlockLibraryManagerForm.DeleteSelected()` 删除后不保存

**文件**: `src\TitleBlockLibraryManagerForm.cs#L153-L177`

删除选中的图框行时只从内存 `_rows` 列表中移除，但**没有调用 `TitleBlockLibraryStore.Save()`** 持久化。用户必须再点"保存修改"才会写盘。

**影响**: 用户删除后如果直接关窗或点"重新读取"，删除操作丢失。与其他操作（导入、保存修改）的立即持久化行为不一致。

---

### B3. `PlotterService.FindLayoutForJob()` 未匹配到布局时静默回退到第一个布局

**文件**: `src\PlotterService.cs#L430-L456`

```csharp
if (string.Equals(owner.Name, job.SpaceName, StringComparison.OrdinalIgnoreCase))
{
    return layout;
}
// ...
if (firstLayout != null) return firstLayout;  // ← 静默回退
```

当 `job.SpaceName` 在所有布局中找不到匹配时，返回第一个布局。这意味着如果图框在"布局1"中但实际激活的是"Model"空间，打印范围会完全错误，且用户没有任何警告。

**影响**: 打印内容错位或空白，无错误提示，用户难以排查。

---

### B4. `ExecutePendingPrintLegacy()` 死代码但保留完整逻辑

**文件**: `src\BatchPlotForm.cs#L666-L730`

70 行完整实现的 `ExecutePendingPrintLegacy()` 在整个代码库中**从未被调用**。当前只使用 `ExecutePendingPrint()`（调用 `PlotterService.PlotMany`）。旧版本逐张 `Plot()` 的逻辑已经废弃。

**影响**: 维护负担，且如果未来有人误用此方法，它不会走 `PlotMany` 的分组优化路径。

---

### B5. `PaperSizeDetector.ToScaleText()` 对比例 < 1 的输出格式易混淆

**文件**: `src\PaperSizeDetector.cs#L190-L203`

```csharp
if (scale > 1) return "1:" + scale;    // 1:100 ✓
return scale + ":1";                    // 0.5:1  ← 实际意思是 "1:2"
```

当 scale=0.5（即 CAD 尺寸是纸张尺寸的 0.5 倍，图纸被放大）时，输出 `0.5:1`。CAD 行业惯例应输出 `2:1`（表示放大 2 倍），而非 `0.5:1`。

**影响**: 比例显示不符合工程图惯例，可能引起用户困惑。

---

### B6. `TitleBlockScanner.ApplyFixedPaper()` 固定尺寸但名称可能来自自动检测

**文件**: `src\TitleBlockScanner.cs#L134-L155`

```csharp
var name = string.IsNullOrWhiteSpace(definition.PaperName)
    ? detected.PaperName    // ← 使用检测出的名称
    : definition.PaperName;
```

如果图框库中的 `PaperName` 为空但 `PaperWidthMm/PaperHeightMm` 有值，纸张名称会回退到自动检测的名称。但尺寸用的是库中固定的值。两者可能不一致（例如检测为 A3 但固定尺寸为 420x297 的 A2+）。

**影响**: 信息展示矛盾，用户看到 A3 名称但实际输出 A2 尺寸。

---

## 🟡 逻辑缺陷（Design Flaws）

### D1. `TransformRegion()` 在两处重复实现且逻辑略有不同

| 位置 | 实现方式 |
|---|---|
| `BatchPlotCommands.cs#L207-L222` | 显式 `Math.Min/Max` 四次比较 |
| `TitleBlockScanner.cs#L117-L132` | LINQ `Min()` / `Max()` |

功能完全相同的 4 点 → 世界坐标系转换，但分别维护。修改一处容易漏掉另一处。

---

### D2. `CadTextExtractor.TryGetWorldText()` 与 `TryGetLocalText()` 完全相同

**文件**: `src\CadTextExtractor.cs#L102-L105`

```csharp
private static bool TryGetWorldText(Entity entity, out string text, out Point3d point)
{
    return TryGetLocalText(entity, out text, out point);
}
```

该方法无任何附加逻辑，仅是别名。增加了调用链的理解负担。

---

### D3. `PlotterService.RefreshJobWindowFromOpenedDocument()` 对整个数据库重新扫描

**文件**: `src\PlotterService.cs#L267-L296`

每次打印一张图前，为了刷新窗口坐标，调用 `TitleBlockScanner.Scan(db, library, job.SourceFile)` **重新扫描整个数据库**，然后用当前 job 的 `SpaceName` + `BlockName` 筛选匹配。扫描操作遍历所有布局空间和所有块参照，对大型图纸（上百个布局/块）性能很差。

**建议**: 在 `PlotJob` 中缓存扫描时的坐标信息，或者仅对当前作业的目标空间做增量查询。

---

### D4. `PaperSizeDetector` 缺少常见 CAD 比例

**文件**: `src\PaperSizeDetector.cs#L43-L47`

```csharp
private static readonly double[] CommonScales = { 1, 2, 5, 10, 20, 25, 50, 75, 100, 125, 150, 200, 250, 500, 1000 };
```

缺失的常见比例：**30, 40, 60, 80, 15, 2000, 5000**。当图纸使用这些比例时，会降级到 `FallbackDetect`（只返回"未匹配"）。

---

### D5. `CadMenuInstaller` 大量空 catch 块隐藏异常

**文件**: `src\CadMenuInstaller.cs`

| 行号 | 上下文 |
|---|---|
| L83-L84 | `ShowMenuBar()` — 设置 MENUBAR 系统变量失败 |
| L171-L172, L184-L185, L195-L196, L207-L208 | 反射调用 COM 接口失败 |

所有反射操作失败时静默吞异常，没有任何日志。菜单安装失败时用户只看到"加载失败"但不知道具体哪个步骤出错（菜单组？工具条？按钮？）。

---

### D6. `TitleBlockLibraryStore` 和 `AppSettingsStore` 无并发保护

两个存储类都没有文件锁或互斥机制。当多个 ZWCAD 实例同时运行时：
- `Upsert()` 的 Load → Modify → Save 存在 TOCTOU 竞态
- 两个实例同时写入会互相覆盖，导致数据丢失

---

### D7. `PlotterService.PlotMany()` 整组失败时所有作业共享同一个异常

**文件**: `src\PlotterService.cs#L59-L65`

```csharp
catch (Exception ex)
{
    foreach (var job in groupJobs)
        results.Add(new PlotJobResult { Job = job, Error = ex });
}
```

如果一个组中有 10 张图，组级别的异常（如文件打开失败）会被复制 10 份。但实际只有第 1 张可能失败，其余 9 张未尝试 — 却报告为相同的失败原因。

---

### D8. `BatchPlotLogger` 无日志轮转/清理

日志文件存储在 `%APPDATA%\ZwcadBatchPlot\Logs\` 并永久累积。长期使用后磁盘占用持续增长，没有任何自动清理策略（如保留最近 N 天/最多 N 个文件）。

---

## 🔵 改进建议（Improvements）

### I1. 增加单元测试覆盖

当前整个项目 **零测试覆盖**。核心算法（`PaperSizeDetector`、`NaturalStringComparer`、`FileNameSanitizer`）是纯逻辑无外部依赖的，非常适合单元测试。

**优先测试**: `PaperSizeDetector.Detect()` 的各种边界情况。

---

### I2. UI 字符串国际化

所有中文字符串硬编码在源代码中（菜单名、按钮文字、提示信息、错误消息）。如需英文版 CAD 支持，需要改动所有文件。

**建议**: 提取到 `.resx` 资源文件，最低成本方案。

---

### I3. `UiLayout.Scale()` 每次调用创建 Graphics 对象

**文件**: `src\UiLayout.cs#L26-L30`

```csharp
public static int Scale(int value)
{
    using var graphics = Graphics.FromHwnd(IntPtr.Zero);
    return Math.Max(1, (int)Math.Round(value * graphics.DpiX / 96F));
}
```

每次调用都 `FromHwnd(IntPtr.Zero)` 创建并销毁 Graphics。在 UI 构建阶段被调用上百次（按钮宽度、布局间距等），不必要的开销。可以缓存 DPI 值并在 DPI 变更时刷新。

---

### I4. `PlotterService.SelectMedia()` 的多余循环

`SelectMedia` 先收集 `media` 列表（`ToList()`），然后传给 `FindByPhysicalSize` 做第二次 `Select().Where().ToList()`。两次具体化可以合并为一次遍历。

---

### I5. `CsvExporter` 不处理单元格内换行

如果图名/图号包含换行符（多行 MTEXT 常见），导出的 CSV 会被截断或错位。标准 CSV 处理方式是保留引号内的换行。当前 `Csv()` 没有对 `\r\n` 做转义。

---

### I6. `FileNameSanitizer.MakeUnique()` 不检查 Windows MAX_PATH

**文件**: `src\FileNameSanitizer.cs#L27-L41`

生成的文件路径没有检查是否超过 260 字符的 Windows 路径限制。长图号 + 长图名 + 深目录嵌套可能导致 `PathTooLongException`。

---

### I7. `PaperSizeDetector` 评分公式中的魔法数字缺少注释

```csharp
var score = anchorError * 0.72 + otherError * 0.28;   // 加权系数来源？
if (anchorError < 0.025 && otherError < 0.16)          // 阈值来源？
    score *= 0.45;                                      // 奖励系数来源？
```

这些数字是根据实际数据调参的结果，但代码中没有任何注释说明其含义和取值依据。后续维护者无法安全修改。

---

### I8. `PlotJob.MatchIndex` 语义脆弱

`MatchIndex` 是在扫描时按全局递增序号分配的。在 `RefreshJobWindowFromOpenedDocument` 中作为第三级回退匹配（先精确匹配图号+图名，再索引匹配）。如果扫描结果列表的排序发生变化（例如新增了图框定义），`MatchIndex` 会完全错位。

**建议**: 使用 `SpaceName + BlockName` 的组合键作为稳定匹配，而非依赖序号。

---

### I9. 四个 `bin-*` 构建变体缺少构建脚本或文档

目录中存在 `bin-commandprint`、`bin-filebatch`、`bin-fixedpaper`、`bin-plotfix` 四个 DLL 变体，但 `.csproj` 中没有对应配置，也没有任何构建脚本说明如何生成这些变体。这暗示存在外部构建步骤或在开发过程中手动编译了不同分支。

---

### I10. 导出/导入图框库缺少版本兼容检查

`TitleBlockLibrary.Version` 字段已定义但从未被校验。如果未来图框库的 JSON schema 发生变化，旧版本文件会被无警告地加载，可能导致反序列化丢字段或崩溃。

---

### I11. `DateTime.Now` 应使用 `DateTime.UtcNow`（或本地时间明确标注）

`Models.cs#L16-L17` 和 `BatchPlotLogger.cs#L16,L23` 使用 `DateTime.Now`（本地时间）。对于跨时区协作或日志分析场景，UTC 时间更可靠。至少应在日志文件名中包含时区信息。

---

## 📊 影响汇总

| 编号 | 类型 | 严重程度 | 影响 |
|---|---|---|---|
| B1 | Bug | 低 | 潜在截断，当前不触发 |
| B2 | Bug | 中 | 删除操作丢失 |
| B3 | Bug | 中 | 静默打印错误布局 |
| B4 | 死代码 | 低 | 维护负担 |
| B5 | Bug | 低 | 比例显示不符合惯例 |
| B6 | Bug | 中 | 用户看到矛盾的纸张信息 |
| D1 | 重复代码 | 低 | 维护风险 |
| D2 | 冗余抽象 | 低 | 可读性 |
| D3 | 性能 | 中 | 大图纸打印变慢 |
| D4 | 功能缺失 | 中 | 部分比例无法识别 |
| D5 | 错误处理 | 中 | 排查困难 |
| D6 | 并发 | 高 | 多实例数据丢失 |
| D7 | 错误报告 | 中 | 误导用户 |
| D8 | 运维 | 低 | 磁盘占用增长 |
| I1-I11 | 改进 | - | 长期质量 |
