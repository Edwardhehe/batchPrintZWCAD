using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 矩形框扫描器：扫描一个或多个布局中的闭合 Polyline 矩形；
/// 用户开启对应设置后，也识别由 4 个独立直线或直线型开放 PL 首尾相连组成的矩形，
/// 筛选出符合标准纸张比例的作为待打印图框。
///
/// 公共入口：
///   ScanWindow    — 扫描当前空间（框选范围）
///   ScanScope     — 按范围扫描多个布局（全部/布局/当前/模型）
///   ScanSelection — 只扫描用户选中的 ObjectId（类型过滤在选择阶段完成）
///
/// 内部流水线：CollectRectanglesFromSpace 收集 →
/// FilterAndPackageRectangles 过滤打包（窗口裁剪 → 纸张比例 →
/// 去重去嵌套 → 空框过滤 → 生成 Result）。
///
/// 支持 WCS 和 UCS（旋转视图），矩形检测使用几何算法而非轴对齐检查。
/// </summary>
public static class RectangleFrameScanner
{
    /// <summary>临时序号标注图层名，扫描时跳过此图层的实体。</summary>
    private const string TemporaryOverlayLayer = "ZBP_TEMP_SEQUENCE_OVERLAY";

    /// <summary>图层可扫描性缓存，避免每次查同一图层都打开图层表。
    /// 静态级缓存，同一次 CAD 会话内跨扫描复用。</summary>
    private static readonly Dictionary<ObjectId, bool> LayerScannableCache = new();

    /// <summary>块定义内容缓存：key=块定义 ObjectId，value=该块定义内的最大矩形（局部坐标）。
    /// 同一次扫描内同一块定义只遍历一次，后续实例直接变换缓存结果。</summary>
    private static readonly Dictionary<ObjectId, List<LocalRectangle>> BlockDefinitionCache = new();

    /// <summary>扫描结果：包含一个 PlotJob 和候选纸张列表。</summary>
    public sealed class Result
    {
        public PlotJob Job { get; set; } = new();
        public IReadOnlyList<PaperDetection> PaperOptions { get; set; } = new PaperDetection[0];
        /// <summary>矩形 4 个实际角点（WCS 坐标），格式 [x0,y0,x1,y1,x2,y2,x3,y3]。
        /// 用于 DCS 变换（4 点→DCS→取包围盒，和单张打印同理）。null 时用 Job 的包围盒。</summary>
        public double[]? CornerPoints { get; set; }
    }

    /// <summary>矩形扫描分阶段耗时（仅诊断用）。</summary>
    public sealed class ScanProfile
    {
        public long TotalMs { get; set; }
        public long CollectSpacesMs { get; set; }
        public long CollectEntitiesMs { get; set; }
        public long FourLineMatchMs { get; set; }
        public long FilterPackageMs { get; set; }
        public long PaperMatchMs { get; set; }
        public long DedupMs { get; set; }
        public long EmptyFilterMs { get; set; }
        public long EmptyBoxCollectMs { get; set; }
        public long AttributeFillMs { get; set; }
        public int LayoutCount { get; set; }
        public int TopLevelEntityVisits { get; set; }
        public int LineSegmentCount { get; set; }
        public int ClosedPolylineRectCount { get; set; }
        public int FourLineRectCount { get; set; }
        public int AfterPaperMatchCount { get; set; }
        public int AfterDedupCount { get; set; }
        public int AfterEmptyFilterCount { get; set; }
        public int ResultCount { get; set; }
        public bool RecognizeFourLines { get; set; }

        public string FormatReport()
        {
            return string.Join(Environment.NewLine, new[]
            {
                $"TotalMs={TotalMs}",
                $"CollectSpacesMs={CollectSpacesMs} (entities={CollectEntitiesMs}, fourLine={FourLineMatchMs})",
                $"FilterPackageMs={FilterPackageMs} (paper={PaperMatchMs}, dedup={DedupMs}, empty={EmptyFilterMs}[boxCollect={EmptyBoxCollectMs}], attrs={AttributeFillMs})",
                $"Layouts={LayoutCount} FourLines={RecognizeFourLines}",
                $"TopLevelEntityVisits={TopLevelEntityVisits} LineSegments={LineSegmentCount}",
                $"Rects: closedPL={ClosedPolylineRectCount} fourLine={FourLineRectCount} afterPaper={AfterPaperMatchCount} afterDedup={AfterDedupCount} afterEmpty={AfterEmptyFilterCount} results={ResultCount}"
            });
        }
    }

    /// <summary>为 true 时填充 <see cref="LastProfile"/>。</summary>
    public static bool EnableProfiling { get; set; }

    /// <summary>最近一次启用 Profiling 的扫描报告。</summary>
    public static ScanProfile? LastProfile { get; private set; }

    [ThreadStatic]
    private static ScanProfile? _activeProfile;

    [ThreadStatic]
    private static IProgress<RectangleScanProgress>? _activeProgress;

    [ThreadStatic]
    private static CancellationToken _activeCancel;

    /// <summary>本次扫描是否按图框库最多模式识别图名、图号。只在公共入口的 try/finally 内有效。</summary>
    private static bool _recognizeByDominantMode;

    private static void ReportScan(string detail, int current = 0, int total = 0, string? title = null)
    {
        _activeCancel.ThrowIfCancellationRequested();
        _activeProgress?.Report(new RectangleScanProgress
        {
            Title = string.IsNullOrWhiteSpace(title) ? "正在识别图框…" : title!,
            Detail = detail,
            Current = current,
            Total = total
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // 公共入口
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 扫描指定窗口内的矩形框（仅当前空间），每个匹配的矩形生成一个 PlotJob。
    /// </summary>
    /// <param name="document">当前 CAD 文档</param>
    /// <param name="scanWindow">扫描窗口（WCS 坐标）</param>
    public static List<Result> ScanWindow(
        Document document,
        Extents3d scanWindow,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null,
        bool recognizeTitleByDominantFrameMode = false)
    {
        return ScanWindow(
            document,
            new CadSelectionWindow
            {
                Bounds = LocalRectangle.FromPoints(
                    scanWindow.MinPoint.X,
                    scanWindow.MinPoint.Y,
                    scanWindow.MaxPoint.X,
                    scanWindow.MaxPoint.Y),
                UcsToWorld = Matrix3d.Identity,
                WorldToUcs = Matrix3d.Identity
            },
            paperMatchToleranceMm,
            recognizeFourLineRectangles,
            recognizeTitleByDominantFrameMode: recognizeTitleByDominantFrameMode);
    }

    /// <summary>
    /// 按用户当前 UCS 中的矩形窗口扫描。Bounds 保持 UCS 原始宽高，WCS 只用于数据库实体读取。
    /// </summary>
    public static List<Result> ScanWindow(
        Document document,
        CadSelectionWindow scanWindow,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null,
        IProgress<RectangleScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool recognizeTitleByDominantFrameMode = false)
    {
        var previousProgress = _activeProgress;
        var previousCancel = _activeCancel;
        var previousDominantMode = _recognizeByDominantMode;
        _activeProgress = progress;
        _activeCancel = cancellationToken;
        _recognizeByDominantMode = recognizeTitleByDominantFrameMode;
        try
        {
            LayerScannableCache.Clear();
            BlockDefinitionCache.Clear();
            var storedSettings = AppSettingsStore.Load();
            var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
            var recognizeFourLines =
                recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
            var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
                ? document.Name
                : document.Database.Filename;

            ReportScan("正在读取当前空间…");
            using var tr = document.Database.TransactionManager.StartTransaction();
            BlockTableRecord owner;
            Layout layout;
            if (document.Database.TileMode)
            {
                var blockTable = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                owner = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                // 与 ScanScope 一致：先校验 LayoutId，避免空/无效 id 触发 eNotApplicable。
                if (!owner.IsLayout || owner.LayoutId.IsNull)
                {
                    return new List<Result>();
                }

                layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            }
            else
            {
                var currentLayoutName = LayoutManager.Current.CurrentLayout;
                var layouts = (DBDictionary)tr.GetObject(document.Database.LayoutDictionaryId, OpenMode.ForRead);
                if (!layouts.Contains(currentLayoutName))
                {
                    return new List<Result>();
                }

                layout = (Layout)tr.GetObject(layouts.GetAt(currentLayoutName), OpenMode.ForRead);
                owner = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                if (!owner.IsLayout || owner.LayoutId.IsNull)
                {
                    return new List<Result>();
                }
            }

            var rectangles = CollectRectanglesFromSpace(tr, owner, recognizeFourLines, layout.LayoutName);
            var ownerId = owner.ObjectId;
            var layoutName = layout.LayoutName;
            var layoutTabOrder = layout.TabOrder;
            var isPaperSpace = !layout.ModelType;
            tr.Commit();

            return FilterAndPackageRectangles(
                document.Database,
                rectangles,
                scanWindow,
                ownerId,
                sourceFile,
                layoutName,
                isPaperSpace,
                layoutTabOrder,
                effectivePaperToleranceMm,
                storedSettings.LongPaperSnapToleranceMm,
                storedSettings.CustomScales);
        }
        finally
        {
            _recognizeByDominantMode = previousDominantMode;
            _activeProgress = previousProgress;
            _activeCancel = previousCancel;
        }
    }

    /// <summary>
    /// 按扫描范围扫描多个布局中的矩形框。
    ///
    /// 遍历所有布局，按 <paramref name="scope"/> 决定扫描哪些空间，
    /// 每个空间独立收集矩形、过滤、打包为 Result。结果按布局遍历顺序排列。
    /// </summary>
    /// <param name="document">当前 CAD 文档</param>
    /// <param name="scope">扫描范围</param>
    public static List<Result> ScanScope(
        Document document,
        TitleBlockScanScope scope,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null,
        IProgress<RectangleScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ISet<string>? allowedLayoutNames = null,
        bool recognizeTitleByDominantFrameMode = false)
    {
        var previousProgress = _activeProgress;
        var previousCancel = _activeCancel;
        var previousDominantMode = _recognizeByDominantMode;
        _activeProgress = progress;
        _activeCancel = cancellationToken;
        _recognizeByDominantMode = recognizeTitleByDominantFrameMode;

        var profile = EnableProfiling ? new ScanProfile() : null;
        _activeProfile = profile;
        var totalSw = profile != null ? Stopwatch.StartNew() : null;

        try
        {
            LayerScannableCache.Clear();
            BlockDefinitionCache.Clear();
            var storedSettings = AppSettingsStore.Load();
            var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
            var recognizeFourLines =
                recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
            if (profile != null)
            {
                profile.RecognizeFourLines = recognizeFourLines;
            }

            var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
                ? document.Name
                : document.Database.Filename;
            var currentSpaceName = GetCurrentSpaceName(document.Database);

            ReportScan("正在枚举布局…");

            // 第一阶段：在事务内遍历所有匹配布局，收集矩形
            var spaceData = new List<(List<LocalRectangle> Rectangles, ObjectId OwnerId, string LayoutName, bool IsPaperSpace, int TabOrder)>();
            var collectSpacesSw = profile != null ? Stopwatch.StartNew() : null;
            using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
                var pendingLayouts = new List<(BlockTableRecord Owner, Layout Layout)>();
                foreach (ObjectId recordId in blockTable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                    if (!owner.IsLayout || owner.LayoutId.IsNull)
                    {
                        continue;
                    }

                    var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
                    if (!ShouldScanLayout(layout, scope, currentSpaceName, allowedLayoutNames))
                    {
                        continue;
                    }

                    pendingLayouts.Add((owner, layout));
                }

                for (var layoutIndex = 0; layoutIndex < pendingLayouts.Count; layoutIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (owner, layout) = pendingLayouts[layoutIndex];
                    if (profile != null)
                    {
                        profile.LayoutCount++;
                    }

                    ReportScan(
                        $"正在收集矩形（{layout.LayoutName}）…",
                        layoutIndex + 1,
                        pendingLayouts.Count);
                    var rectangles = CollectRectanglesFromSpace(
                        tr,
                        owner,
                        recognizeFourLines,
                        layout.LayoutName);
                    spaceData.Add((rectangles, owner.ObjectId, layout.LayoutName, !layout.ModelType, layout.TabOrder));
                }

                tr.Commit();
            }

            if (collectSpacesSw != null && profile != null)
            {
                collectSpacesSw.Stop();
                profile.CollectSpacesMs = collectSpacesSw.ElapsedMilliseconds;
            }

            // 按布局 TabOrder 排序，确保模型空间在最前、图纸布局按选项卡顺序排列
            spaceData.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));

            // 第二阶段：对每个空间独立过滤打包（FilterEmptyRectangles 内会开自己的事务）
            var filterSw = profile != null ? Stopwatch.StartNew() : null;
            var allResults = new List<Result>();
            for (var i = 0; i < spaceData.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (rectangles, ownerId, layoutName, isPaperSpace, tabOrder) = spaceData[i];
                ReportScan(
                    $"正在筛选纸张与空框（{layoutName}）…",
                    i + 1,
                    spaceData.Count);
                var results = FilterAndPackageRectangles(
                    document.Database,
                    rectangles,
                    isPaperSpace
                        ? null
                        : CadCoordinateSystem.CreateModelContext(document.Editor, true),
                    ownerId,
                    sourceFile,
                    layoutName,
                    isPaperSpace,
                    tabOrder,
                    effectivePaperToleranceMm,
                    storedSettings.LongPaperSnapToleranceMm,
                    storedSettings.CustomScales);
                allResults.AddRange(results);
            }

            if (filterSw != null && profile != null)
            {
                filterSw.Stop();
                profile.FilterPackageMs = filterSw.ElapsedMilliseconds;
                profile.ResultCount = allResults.Count;
            }

            if (totalSw != null && profile != null)
            {
                totalSw.Stop();
                profile.TotalMs = totalSw.ElapsedMilliseconds;
                LastProfile = profile;
            }

            ReportScan($"识别完成，共 {allResults.Count} 个图框", allResults.Count, Math.Max(1, allResults.Count));
            return allResults;
        }
        finally
        {
            _recognizeByDominantMode = previousDominantMode;
            _activeProfile = null;
            _activeProgress = previousProgress;
            _activeCancel = previousCancel;
        }
    }

    /// <summary>
    /// 只扫描给定 ObjectId 集合中的实体（块参照、多段线，以及开启四线识别时的直线）。
    /// 空或 null 返回空列表；无法打开的 id 会被忽略。结果形状与 <see cref="ScanWindow"/> / <see cref="ScanScope"/> 相同。
    /// </summary>
    public static List<Result> ScanSelection(
        Document document,
        IEnumerable<ObjectId>? selectedIds,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null,
        IProgress<RectangleScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool recognizeTitleByDominantFrameMode = false)
    {
        var previousProgress = _activeProgress;
        var previousCancel = _activeCancel;
        var previousDominantMode = _recognizeByDominantMode;
        _activeProgress = progress;
        _activeCancel = cancellationToken;
        _recognizeByDominantMode = recognizeTitleByDominantFrameMode;
        try
        {
            if (selectedIds == null)
            {
                return new List<Result>();
            }

            var idList = new List<ObjectId>();
            foreach (var id in selectedIds)
            {
                if (!id.IsNull)
                {
                    idList.Add(id);
                }
            }

            if (idList.Count == 0)
            {
                return new List<Result>();
            }

            LayerScannableCache.Clear();
            BlockDefinitionCache.Clear();
            var storedSettings = AppSettingsStore.Load();
            var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
            var recognizeFourLines =
                recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
            var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
                ? document.Name
                : document.Database.Filename;

            ReportScan("正在读取选中对象…");

            var spaceData = new List<(List<LocalRectangle> Rectangles, ObjectId OwnerId, string LayoutName, bool IsPaperSpace, int TabOrder)>();
            using (var tr = document.Database.TransactionManager.StartTransaction())
            {
                var groups = new Dictionary<ObjectId, List<ObjectId>>();
                var owners = new Dictionary<ObjectId, (BlockTableRecord Owner, Layout Layout)>();
                foreach (var id in idList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Entity? entity;
                    try
                    {
                        entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch
                    {
                        continue;
                    }

                    if (entity == null)
                    {
                        continue;
                    }

                    var ownerId = entity.OwnerId;
                    if (ownerId.IsNull)
                    {
                        continue;
                    }

                    if (!owners.TryGetValue(ownerId, out var ownerInfo))
                    {
                        BlockTableRecord? owner;
                        Layout? layout;
                        try
                        {
                            owner = tr.GetObject(ownerId, OpenMode.ForRead, false) as BlockTableRecord;
                            if (owner == null || !owner.IsLayout || owner.LayoutId.IsNull)
                            {
                                continue;
                            }

                            layout = tr.GetObject(owner.LayoutId, OpenMode.ForRead, false) as Layout;
                            if (layout == null)
                            {
                                continue;
                            }
                        }
                        catch
                        {
                            continue;
                        }

                        ownerInfo = (owner, layout);
                        owners[ownerId] = ownerInfo;
                        groups[ownerId] = new List<ObjectId>();
                    }

                    groups[ownerId].Add(id);
                }

                foreach (var pair in groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (owner, layout) = owners[pair.Key];
                    ReportScan($"正在收集矩形（{layout.LayoutName}）…");
                    var rectangles = CollectRectanglesFromEntities(
                        tr,
                        pair.Value,
                        recognizeFourLines,
                        layout.LayoutName);
                    spaceData.Add((rectangles, owner.ObjectId, layout.LayoutName, !layout.ModelType, layout.TabOrder));
                }

                tr.Commit();
            }

            spaceData.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));

            var allResults = new List<Result>();
            for (var i = 0; i < spaceData.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (rectangles, ownerId, layoutName, isPaperSpace, tabOrder) = spaceData[i];
                ReportScan(
                    $"正在筛选纸张与空框（{layoutName}）…",
                    i + 1,
                    spaceData.Count);
                var results = FilterAndPackageRectangles(
                    document.Database,
                    rectangles,
                    isPaperSpace
                        ? null
                        : CadCoordinateSystem.CreateModelContext(document.Editor, true),
                    ownerId,
                    sourceFile,
                    layoutName,
                    isPaperSpace,
                    tabOrder,
                    effectivePaperToleranceMm,
                    storedSettings.LongPaperSnapToleranceMm,
                    storedSettings.CustomScales);
                allResults.AddRange(results);
            }

            ReportScan($"识别完成，共 {allResults.Count} 个图框", allResults.Count, Math.Max(1, allResults.Count));
            return allResults;
        }
        finally
        {
            _recognizeByDominantMode = previousDominantMode;
            _activeProgress = previousProgress;
            _activeCancel = previousCancel;
        }
    }

    /// <summary>
    /// 侧载扫描：用已打开的 <see cref="Database"/>（通常来自 ReadDwgFile）按范围/布局名过滤扫描矩形框。
    /// 不依赖 Document/Editor；模型空间坐标保持 WCS，不向当前图绘制 overlay。
    /// </summary>
    public static List<Result> ScanDatabase(
        Database db,
        string sourceFile,
        TitleBlockScanScope scope,
        ISet<string>? allowedLayoutNames = null,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null,
        IProgress<RectangleScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool recognizeTitleByDominantFrameMode = false)
    {
        var previousProgress = _activeProgress;
        var previousCancel = _activeCancel;
        var previousDominantMode = _recognizeByDominantMode;
        _activeProgress = progress;
        _activeCancel = cancellationToken;
        _recognizeByDominantMode = recognizeTitleByDominantFrameMode;

        var profile = EnableProfiling ? new ScanProfile() : null;
        _activeProfile = profile;
        var totalSw = profile != null ? Stopwatch.StartNew() : null;

        try
        {
            LayerScannableCache.Clear();
            BlockDefinitionCache.Clear();
            var storedSettings = AppSettingsStore.Load();
            var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
            var recognizeFourLines =
                recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
            if (profile != null)
            {
                profile.RecognizeFourLines = recognizeFourLines;
            }

            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                sourceFile = db.Filename ?? "";
            }

            ReportScan("正在枚举布局…");

            var spaceData = new List<(List<LocalRectangle> Rectangles, ObjectId OwnerId, string LayoutName, bool IsPaperSpace, int TabOrder)>();
            var collectSpacesSw = profile != null ? Stopwatch.StartNew() : null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var pendingLayouts = new List<(BlockTableRecord Owner, Layout Layout)>();
                foreach (ObjectId recordId in blockTable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                    if (!owner.IsLayout || owner.LayoutId.IsNull)
                    {
                        continue;
                    }

                    var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
                    // 侧载无当前空间概念；CurrentSpace 视为全部空间后再按 allowedLayoutNames 过滤。
                    var effectiveScope = scope == TitleBlockScanScope.CurrentSpace
                        ? TitleBlockScanScope.AllSpaces
                        : scope;
                    if (!ShouldScanLayout(layout, effectiveScope, currentSpaceName: null, allowedLayoutNames))
                    {
                        continue;
                    }

                    pendingLayouts.Add((owner, layout));
                }

                for (var layoutIndex = 0; layoutIndex < pendingLayouts.Count; layoutIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (owner, layout) = pendingLayouts[layoutIndex];
                    if (profile != null)
                    {
                        profile.LayoutCount++;
                    }

                    ReportScan(
                        $"正在收集矩形（{layout.LayoutName}）…",
                        layoutIndex + 1,
                        pendingLayouts.Count);
                    var rectangles = CollectRectanglesFromSpace(
                        tr,
                        owner,
                        recognizeFourLines,
                        layout.LayoutName);
                    spaceData.Add((rectangles, owner.ObjectId, layout.LayoutName, !layout.ModelType, layout.TabOrder));
                }

                tr.Commit();
            }

            if (collectSpacesSw != null && profile != null)
            {
                collectSpacesSw.Stop();
                profile.CollectSpacesMs = collectSpacesSw.ElapsedMilliseconds;
            }

            spaceData.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));

            var filterSw = profile != null ? Stopwatch.StartNew() : null;
            var allResults = new List<Result>();
            for (var i = 0; i < spaceData.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (rectangles, ownerId, layoutName, isPaperSpace, tabOrder) = spaceData[i];
                ReportScan(
                    $"正在筛选纸张与空框（{layoutName}）…",
                    i + 1,
                    spaceData.Count);
                // 外图无 Editor：模型空间保持 WCS；打印路径后续按 SourceFile 打开处理。
                var results = FilterAndPackageRectangles(
                    db,
                    rectangles,
                    coordinateContext: null,
                    ownerId,
                    sourceFile,
                    layoutName,
                    isPaperSpace,
                    tabOrder,
                    effectivePaperToleranceMm,
                    storedSettings.LongPaperSnapToleranceMm,
                    storedSettings.CustomScales);
                foreach (var result in results)
                {
                    result.Job.IsDcsWindow = false;
                }

                allResults.AddRange(results);
            }

            if (filterSw != null && profile != null)
            {
                filterSw.Stop();
                profile.FilterPackageMs = filterSw.ElapsedMilliseconds;
                profile.ResultCount = allResults.Count;
            }

            if (totalSw != null && profile != null)
            {
                totalSw.Stop();
                profile.TotalMs = totalSw.ElapsedMilliseconds;
                LastProfile = profile;
            }

            ReportScan($"识别完成，共 {allResults.Count} 个图框", allResults.Count, Math.Max(1, allResults.Count));
            return allResults;
        }
        finally
        {
            _recognizeByDominantMode = previousDominantMode;
            _activeProfile = null;
            _activeProgress = previousProgress;
            _activeCancel = previousCancel;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 内部：单空间收集 & 过滤打包
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 遍历单个空间内的闭合 PL 矩形；开关开启时，同时收集顶层独立直线/直线型 PL，
    /// 经四叉树找出四边闭环后转换为同一个 LocalRectangle 流程。
    /// 先按 <see cref="ScanCandidateFilter"/> 过滤候选 ObjectId，再识别。
    /// </summary>
    private static List<LocalRectangle> CollectRectanglesFromSpace(
        Transaction tr,
        BlockTableRecord owner,
        bool recognizeFourLineRectangles,
        string layoutName = "")
    {
        var profile = _activeProfile;
        var candidateIds = new List<ObjectId>();
        var topLevelVisits = 0;
        const int progressEvery = 2500;
        foreach (ObjectId id in owner)
        {
            if ((topLevelVisits % progressEvery) == 0)
            {
                _activeCancel.ThrowIfCancellationRequested();
                if (topLevelVisits == 0)
                {
                    ReportScan(
                        string.IsNullOrEmpty(layoutName)
                            ? "正在过滤候选对象…"
                            : $"正在过滤候选对象（{layoutName}）…");
                }
                else
                {
                    ReportScan(
                        string.IsNullOrEmpty(layoutName)
                            ? $"正在过滤候选对象… 已处理 {topLevelVisits:N0}"
                            : $"正在过滤候选对象（{layoutName}）… 已处理 {topLevelVisits:N0}",
                        topLevelVisits,
                        0);
                }
            }

            topLevelVisits++;
            DBObject? obj;
            try
            {
                obj = tr.GetObject(id, OpenMode.ForRead, false);
            }
            catch
            {
                continue;
            }

            if (!ScanCandidateFilter.IsRectangleFrameCandidate(obj, recognizeFourLineRectangles))
            {
                continue;
            }

            candidateIds.Add(id);
        }

        if (profile != null)
        {
            profile.TopLevelEntityVisits += topLevelVisits;
        }

        return CollectRectanglesFromEntities(tr, candidateIds, recognizeFourLineRectangles, layoutName);
    }

    /// <summary>
    /// 只遍历给定实体（对象选择路径），其余矩形检测与四线拼合规则与空间扫描一致。
    /// </summary>
    private static List<LocalRectangle> CollectRectanglesFromEntities(
        Transaction tr,
        IEnumerable<ObjectId> entityIds,
        bool recognizeFourLineRectangles,
        string layoutName = "")
    {
        var profile = _activeProfile;
        var rectangles = new List<LocalRectangle>();
        var segments = recognizeFourLineRectangles ? new List<FourLineRectangleFinder.Segment>() : null;
        var entitySw = profile != null ? Stopwatch.StartNew() : null;
        var topLevelVisits = 0;
        foreach (var id in entityIds)
        {
            Entity? entity;
            try
            {
                if (id.IsNull)
                {
                    continue;
                }

                entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch
            {
                continue;
            }

            if (entity == null
                || !ScanCandidateFilter.IsRectangleFrameCandidate(entity, recognizeFourLineRectangles))
            {
                continue;
            }

            topLevelVisits++;
            CollectEntityRectangles(
                tr,
                entity,
                Matrix3d.Identity,
                rectangles,
                segments,
                new HashSet<ObjectId>(),
                0,
                recognizeFourLineRectangles);
        }

        if (entitySw != null && profile != null)
        {
            entitySw.Stop();
            profile.CollectEntitiesMs += entitySw.ElapsedMilliseconds;
            profile.TopLevelEntityVisits += topLevelVisits;
            profile.ClosedPolylineRectCount += rectangles.Count;
            if (segments != null)
            {
                profile.LineSegmentCount += segments.Count;
            }
        }

        if (segments != null && segments.Count >= 4)
        {
            _activeCancel.ThrowIfCancellationRequested();
            ReportScan(
                string.IsNullOrEmpty(layoutName)
                    ? $"正在拼合四线矩形（线段 {segments.Count:N0}）…"
                    : $"正在拼合四线矩形（{layoutName}，线段 {segments.Count:N0}）…",
                0,
                0);
            var fourSw = profile != null ? Stopwatch.StartNew() : null;
            var before = rectangles.Count;
            rectangles.AddRange(FourLineRectangleFinder.Find(segments, _activeCancel, (msg, cur, tot) => ReportScan(msg, cur, tot)));
            if (fourSw != null && profile != null)
            {
                fourSw.Stop();
                profile.FourLineMatchMs += fourSw.ElapsedMilliseconds;
                profile.FourLineRectCount += rectangles.Count - before;
            }
        }

        return rectangles;
    }

    /// <summary>
    /// 对一个空间内收集到的矩形做完整的过滤流水线并打包为 Result 列表。
    ///
    /// 流水线：窗口裁剪（可选）→ 纸张比例过滤 → 去重去嵌套 → 空框过滤 → 生成 Result。
    /// </summary>
    private static List<Result> FilterAndPackageRectangles(
        Database database,
        List<LocalRectangle> rectangles,
        CadSelectionWindow? coordinateContext,
        ObjectId ownerId,
        string sourceFile,
        string layoutName,
        bool isPaperSpace,
        int layoutTabOrder,
        double paperMatchToleranceMm,
        double longPaperSnapToleranceMm,
        IReadOnlyList<double>? customScales = null)
    {
        // 3a. 窗口裁剪（可选）
        List<LocalRectangle> inWindow;
        if (coordinateContext != null && coordinateContext.Bounds.HasArea())
        {
            inWindow = rectangles
                .Where(rectangle => coordinateContext.IntersectsWorldPoints(GetWorldPoints(rectangle)))
                .ToList();
        }
        else
        {
            inWindow = rectangles.ToList();
        }

        // 3b. 纸张标准比例过滤
        var profile = _activeProfile;
        var paperSw = profile != null ? Stopwatch.StartNew() : null;
        var stem = Path.GetFileNameWithoutExtension(sourceFile);
        var paperMatched = new List<LocalRectangle>();
        var paperOptionsByRect = new Dictionary<LocalRectangle, IReadOnlyList<PaperDetection>>();
        var coordinateBoundsByRect = new Dictionary<LocalRectangle, LocalRectangle>();
        var paperTotal = inWindow.Count;
        for (var paperIndex = 0; paperIndex < inWindow.Count; paperIndex++)
        {
            if ((paperIndex % 50) == 0)
            {
                _activeCancel.ThrowIfCancellationRequested();
                ReportScan(
                    $"正在匹配纸张比例… {paperIndex:N0}/{paperTotal:N0}",
                    paperIndex,
                    Math.Max(1, paperTotal));
            }

            var rectangle = inWindow[paperIndex];
            var coordinateBounds = !isPaperSpace
                                   && coordinateContext != null
                                   && !coordinateContext.IsWorldCoordinateSystem
                ? coordinateContext.TransformWorldPointsToBounds(GetWorldPoints(rectangle))
                : null;
            var width = coordinateBounds != null
                ? coordinateBounds.MaxX - coordinateBounds.MinX
                : rectangle.ActualWidth > 0 ? rectangle.ActualWidth : rectangle.MaxX - rectangle.MinX;
            var height = coordinateBounds != null
                ? coordinateBounds.MaxY - coordinateBounds.MinY
                : rectangle.ActualHeight > 0 ? rectangle.ActualHeight : rectangle.MaxY - rectangle.MinY;
            // 比例库短边毫米匹配；失败则长宽比任意比例，并展开 A0~A4 供改纸。
            var detectionOptions = PaperSizeDetector.CreateRectangleBatchOptions(paperMatchToleranceMm, isPaperSpace, longPaperSnapToleranceMm, customScales);
            var options = PaperSizeDetector.DetectRectangleBatchCandidates(width, height, detectionOptions);
            if (options.Count == 0)
            {
                continue;
            }

            paperMatched.Add(rectangle);
            paperOptionsByRect[rectangle] = options;
            if (coordinateBounds != null)
            {
                coordinateBoundsByRect[rectangle] = coordinateBounds;
            }
        }

        if (paperSw != null && profile != null)
        {
            paperSw.Stop();
            profile.PaperMatchMs += paperSw.ElapsedMilliseconds;
            profile.AfterPaperMatchCount += paperMatched.Count;
        }

        // 3c. 去重去嵌套
        var dedupSw = profile != null ? Stopwatch.StartNew() : null;
        var unique = FilterRectangles(paperMatched);
        if (dedupSw != null && profile != null)
        {
            dedupSw.Stop();
            profile.DedupMs += dedupSw.ElapsedMilliseconds;
            profile.AfterDedupCount += unique.Count;
        }

        // 3d. 空框过滤
        ReportScan("正在检查空框…");
        var emptySw = profile != null ? Stopwatch.StartNew() : null;
        var withContent = FilterEmptyRectangles(database, ownerId, unique);
        if (emptySw != null && profile != null)
        {
            emptySw.Stop();
            profile.EmptyFilterMs += emptySw.ElapsedMilliseconds;
            profile.AfterEmptyFilterCount += withContent.Count;
        }

        // 3e. 生成结果
        var results = new List<Result>();
        var packedRectangles = new List<LocalRectangle>(withContent.Count);
        foreach (var rectangle in withContent)
        {
            var options = paperOptionsByRect[rectangle];
            var paper = options.First();
            coordinateBoundsByRect.TryGetValue(rectangle, out var coordinateBounds);
            var width = coordinateBounds != null
                ? coordinateBounds.MaxX - coordinateBounds.MinX
                : rectangle.ActualWidth > 0 ? rectangle.ActualWidth : rectangle.MaxX - rectangle.MinX;
            var height = coordinateBounds != null
                ? coordinateBounds.MaxY - coordinateBounds.MinY
                : rectangle.ActualHeight > 0 ? rectangle.ActualHeight : rectangle.MaxY - rectangle.MinY;
            var index = results.Count;
            var result = new Result
            {
                CornerPoints = rectangle.CornerPoints,
                PaperOptions = options,
                Job = new PlotJob
                {
                    IsManualWindow = true,
                    SourceFile = sourceFile,
                    SpaceName = layoutName,
                    IsPaperSpace = isPaperSpace,
                    LayoutTabOrder = layoutTabOrder,
                    DrawingNumber = (index + 1).ToString("D2"),
                    Title = stem,
                    PaperName = paper.PaperName,
                    ScaleText = paper.ScaleText,
                    SizeText = $"{width:0.##} x {height:0.##}",
                    PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                    DetectionNote = "通用型批量打印",
                    PaperWidthMm = paper.PaperWidthMm,
                    PaperHeightMm = paper.PaperHeightMm,
                    DetectedRequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                    RequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                    MinX = rectangle.MinX,
                    MinY = rectangle.MinY,
                    MaxX = rectangle.MaxX,
                    MaxY = rectangle.MaxY,
                    // 打印窗口后续会转换为 DCS；DWG 拆图仍需保留原始 WCS 四角点。
                    CornerPoints = rectangle.CornerPoints == null
                        ? null
                        : (double[])rectangle.CornerPoints.Clone()
                }
            };
            if (!isPaperSpace && coordinateContext != null && coordinateBounds != null)
            {
                coordinateContext.ApplyToJob(result.Job, coordinateBounds);
            }

            results.Add(result);
            packedRectangles.Add(rectangle);
        }

        // 3f. 勾选时先按图框库最多模式，在本空间里取图名/图号；没取到的再退回属性 Tag。
        if (_recognizeByDominantMode)
        {
            ReportScan("正在按图框模式识别图名/图号…");
            FillDominantModeIdentities(database, ownerId, results, packedRectangles);
        }

        ReportScan("正在识别图号/图名属性…");
        var attrSw = _activeProfile != null ? Stopwatch.StartNew() : null;
        FillAttributeIdentities(database, ownerId, results, packedRectangles);
        if (attrSw != null && _activeProfile != null)
        {
            attrSw.Stop();
            _activeProfile.AttributeFillMs += attrSw.ElapsedMilliseconds;
        }

        return results;
    }

    /// <summary>
    /// 用图框库中数量最多的模式，把图名、图号矩形放到每张图的右下角，并在同一空间取字。
    /// 纸面毫米按这张图外框短边和纸张短边的实际比例放大。
    /// </summary>
    private static void FillDominantModeIdentities(
        Database database,
        ObjectId ownerId,
        IReadOnlyList<Result> results,
        IReadOnlyList<LocalRectangle> rectangles)
    {
        if (results.Count == 0 || results.Count != rectangles.Count || !TryGetDominantMode(out var titleBox, out var numberBox))
        {
            return;
        }

        var blockFrames = ScanTitleBlockFrames(database, TryGetLayoutName(database, ownerId));
        try
        {
            using var tr = database.TransactionManager.StartTransaction();
            var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
            var cache = CadTextExtractor.BuildOwnerTextCacheWithoutBlockNames(tr, owner);
            for (var i = 0; i < results.Count; i++)
            {
                var job = results[i].Job;
                if (!TryCreateFrameAxes(
                        rectangles[i],
                        job.PaperWidthMm,
                        job.PaperHeightMm,
                        FindBlockFrameCorners(rectangles[i], blockFrames),
                        out var axes))
                {
                    continue;
                }

                var title = ExtractCellText(cache, axes, titleBox);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    job.CadTitle = title;
                    job.Title = title;
                }

                var number = ExtractCellText(cache, axes, numberBox);
                if (!string.IsNullOrWhiteSpace(number))
                {
                    job.CadDrawingNumber = number;
                    job.DrawingNumber = number;
                }
            }

            tr.Commit();
        }
        catch
        {
            // 模式取字失败时留给属性 Tag 回退，不中断整次扫描。
        }
    }

    /// <summary>
    /// 用户改纸后，按新纸张短边用图框库最多模式重取图名、图号。
    /// 角点沿用扫描时的 WCS 或图纸空间四角。新窗口取到字就覆盖；取不到则清空，再按属性 Tag 回退。
    /// </summary>
    public static void RefillDominantModeIdentities(Database database, IReadOnlyList<PlotJob> jobs)
    {
        if (database == null || jobs == null || jobs.Count == 0 || !TryGetDominantMode(out var titleBox, out var numberBox))
        {
            return;
        }

        var ready = new List<(PlotJob Job, LocalRectangle Rectangle)>();
        foreach (var job in jobs)
        {
            if (job != null && TryCreateIdentityRectangle(job, out var rectangle))
            {
                ready.Add((job, rectangle));
            }
        }

        if (ready.Count == 0)
        {
            return;
        }

        var attributePass = new List<(ObjectId OwnerId, List<Result> Results, List<LocalRectangle> Rectangles)>();
        var blockFramesBySpace = new Dictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (var spaceName in ready.Select(item => item.Job.SpaceName ?? "").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            blockFramesBySpace[spaceName] = ScanTitleBlockFrames(database, spaceName);
        }

        try
        {
            using var tr = database.TransactionManager.StartTransaction();
            foreach (var spaceGroup in ready.GroupBy(item => item.Job.SpaceName ?? "", StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(spaceGroup.Key)
                    || !TryGetSpaceOwner(tr, database, spaceGroup.Key, out var ownerId))
                {
                    continue;
                }

                var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
                var spaceItems = spaceGroup.ToList();
                var blockFrames = blockFramesBySpace.TryGetValue(spaceGroup.Key, out var frames)
                    ? frames
                    : new List<double[]>();
                var cache = CadTextExtractor.BuildOwnerTextCacheWithoutBlockNames(tr, owner);
                var results = new List<Result>(spaceItems.Count);
                var rectangles = new List<LocalRectangle>(spaceItems.Count);
                foreach (var item in spaceItems)
                {
                    try
                    {
                        var title = "";
                        var number = "";
                        if (TryCreateFrameAxes(
                                item.Rectangle,
                                item.Job.PaperWidthMm,
                                item.Job.PaperHeightMm,
                                FindBlockFrameCorners(item.Rectangle, blockFrames),
                                out var axes))
                        {
                            title = ExtractCellText(cache, axes, titleBox);
                            number = ExtractCellText(cache, axes, numberBox);
                        }

                        item.Job.CadTitle = title ?? "";
                        item.Job.Title = title ?? "";
                        item.Job.CadDrawingNumber = number ?? "";
                        item.Job.DrawingNumber = number ?? "";
                    }
                    catch
                    {
                        // 这一张取字失败时保留改纸前的图名图号。
                    }

                    results.Add(new Result { Job = item.Job, CornerPoints = item.Rectangle.CornerPoints });
                    rectangles.Add(item.Rectangle);
                }

                attributePass.Add((ownerId, results, rectangles));
            }

            tr.Commit();
        }
        catch
        {
            return;
        }

        foreach (var pass in attributePass)
        {
            FillAttributeIdentities(database, pass.OwnerId, pass.Results, pass.Rectangles);
        }
    }

    /// <summary>用作业上保留的扫描角点还原取字矩形。Min/Max 已转成 DCS 时不能拿来套文字。</summary>
    private static bool TryCreateIdentityRectangle(PlotJob job, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        if (job.CornerPoints is not { Length: >= 8 } points)
        {
            return false;
        }

        var minX = points[0];
        var minY = points[1];
        var maxX = points[0];
        var maxY = points[1];
        for (var i = 0; i < 4; i++)
        {
            var x = points[i * 2];
            var y = points[i * 2 + 1];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        rectangle.MinX = minX;
        rectangle.MinY = minY;
        rectangle.MaxX = maxX;
        rectangle.MaxY = maxY;
        rectangle.CornerPoints = points;
        return true;
    }

    /// <summary>按布局名打开该空间的块表记录。</summary>
    private static bool TryGetSpaceOwner(Transaction tr, Database database, string spaceName, out ObjectId ownerId)
    {
        ownerId = ObjectId.Null;
        var layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        if (!layouts.Contains(spaceName))
        {
            return false;
        }

        if (tr.GetObject(layouts.GetAt(spaceName), OpenMode.ForRead, false) is not Layout layout
            || layout.BlockTableRecordId.IsNull)
        {
            return false;
        }

        ownerId = layout.BlockTableRecordId;
        return true;
    }

    /// <summary>图框库聚类后成员最多的那一组图名、图号纸面矩形。</summary>
    private static bool TryGetDominantMode(out PaperCornerBox title, out PaperCornerBox number)
    {
        title = default;
        number = default;
        var modes = TitleBlockCornerModeGrouper.Group(TitleBlockLibraryStore.Load().Blocks).Modes;
        if (modes.Count == 0)
        {
            return false;
        }

        title = modes[0].Title;
        number = modes[0].Number;
        return true;
    }

    /// <summary>右下角为原点，底边朝左、右边朝上。长度是这条边的绘图单位。</summary>
    private readonly struct FrameAxes
    {
        public FrameAxes(Point3d origin, Vector3d left, Vector3d up, double cadPerMm)
        {
            Origin = origin;
            Left = left;
            Up = up;
            CadPerMm = cadPerMm;
        }

        public Point3d Origin { get; }
        public Vector3d Left { get; }
        public Vector3d Up { get; }
        public double CadPerMm { get; }
    }

    /// <summary>
    /// 图框块扫描给出的角点顺序是块内左下、右下、右上、左上，右下角才是图名图号基点。
    /// 对得上图框块时用这个右下角；对不上再退回世界坐标最右最下。
    /// </summary>
    private static bool TryCreateFrameAxes(
        LocalRectangle rectangle,
        double paperWidthMm,
        double paperHeightMm,
        double[]? blockCorners,
        out FrameAxes axes)
    {
        if (blockCorners != null
            && TryCreateAxesFromBlockBottomRight(blockCorners, paperWidthMm, paperHeightMm, out axes))
        {
            return true;
        }

        return TryCreateFrameAxes(rectangle, paperWidthMm, paperHeightMm, out axes);
    }

    /// <summary>
    /// 右下角取四个角里最靠右、再最靠下的那个。两条邻边里更朝左的是底边，另一条是右边向上。
    /// </summary>
    private static bool TryCreateFrameAxes(LocalRectangle rectangle, double paperWidthMm, double paperHeightMm, out FrameAxes axes)
    {
        axes = default;
        var paperShort = Math.Min(Math.Abs(paperWidthMm), Math.Abs(paperHeightMm));
        if (paperShort < 1e-6)
        {
            return false;
        }

        var points = GetWorldPoints(rectangle);
        if (points.Length < 4)
        {
            return false;
        }

        var bottomRight = 0;
        for (var i = 1; i < 4; i++)
        {
            if (points[i].X > points[bottomRight].X + 1e-6
                || (Math.Abs(points[i].X - points[bottomRight].X) <= 1e-6 && points[i].Y < points[bottomRight].Y))
            {
                bottomRight = i;
            }
        }

        var origin = points[bottomRight];
        var previous = points[(bottomRight + 3) % 4] - origin;
        var next = points[(bottomRight + 1) % 4] - origin;
        if (previous.Length < 1e-6 || next.Length < 1e-6)
        {
            return false;
        }

        // 更朝左（X 更小，X 相近时 Y 更小）的邻边沿底边离开右下角。
        var previousIsBottom = previous.X < next.X - 1e-6
            || (Math.Abs(previous.X - next.X) <= 1e-6 && previous.Y <= next.Y);
        var alongBottom = previousIsBottom ? previous : next;
        var alongRight = previousIsBottom ? next : previous;
        var cadPerMm = Math.Min(alongBottom.Length, alongRight.Length) / paperShort;
        if (cadPerMm < 1e-9)
        {
            return false;
        }

        axes = new FrameAxes(origin, alongBottom.GetNormal(), alongRight.GetNormal(), cadPerMm);
        return true;
    }

    /// <summary>
    /// 用图框块扫描的世界角点建坐标系。角点顺序与块内一致：左下、右下、右上、左上。
    /// 原点是块内右下角，底边指向左下，右边指向上。
    /// </summary>
    private static bool TryCreateAxesFromBlockBottomRight(
        double[] corners,
        double paperWidthMm,
        double paperHeightMm,
        out FrameAxes axes)
    {
        axes = default;
        var paperShort = Math.Min(Math.Abs(paperWidthMm), Math.Abs(paperHeightMm));
        if (paperShort < 1e-6 || corners.Length < 8)
        {
            return false;
        }

        var origin = new Point3d(corners[2], corners[3], 0);
        var alongBottom = new Point3d(corners[0], corners[1], 0) - origin;
        var alongRight = new Point3d(corners[4], corners[5], 0) - origin;
        if (alongBottom.Length < 1e-6 || alongRight.Length < 1e-6)
        {
            return false;
        }

        var cadPerMm = Math.Min(alongBottom.Length, alongRight.Length) / paperShort;
        if (cadPerMm < 1e-9)
        {
            return false;
        }

        axes = new FrameAxes(origin, alongBottom.GetNormal(), alongRight.GetNormal(), cadPerMm);
        return true;
    }

    /// <summary>只扫描指定布局里的图框块，取出已经按块旋转算好的四角。</summary>
    private static List<double[]> ScanTitleBlockFrames(Database database, string layoutName)
    {
        var frames = new List<double[]>();
        if (database == null || string.IsNullOrWhiteSpace(layoutName))
        {
            return frames;
        }

        try
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { layoutName };
            var jobs = TitleBlockScanner.Scan(
                database,
                TitleBlockLibraryStore.Load(),
                database.Filename ?? "",
                null,
                TitleBlockScanScope.AllSpaces,
                null,
                null,
                null,
                allowed);
            foreach (var job in jobs)
            {
                if (job?.CornerPoints is { Length: >= 8 } corners)
                {
                    frames.Add(corners);
                }
            }
        }
        catch
        {
            // 图框块扫描失败时退回世界坐标右下角，不中断通用型扫描。
        }

        return frames;
    }

    /// <summary>按块表记录反查布局名，供图框块扫描限定空间。</summary>
    private static string TryGetLayoutName(Database database, ObjectId ownerId)
    {
        try
        {
            using var tr = database.TransactionManager.StartTransaction();
            if (tr.GetObject(ownerId, OpenMode.ForRead, false) is not BlockTableRecord owner
                || !owner.IsLayout
                || owner.LayoutId.IsNull
                || tr.GetObject(owner.LayoutId, OpenMode.ForRead, false) is not Layout layout)
            {
                return "";
            }

            var name = layout.LayoutName ?? "";
            tr.Commit();
            return name;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 矩形外框与图框块打印边界互相包含中心时，采用该图框块的四角。
    /// 多张重叠时取中心更近的一张。
    /// </summary>
    private static double[]? FindBlockFrameCorners(LocalRectangle rectangle, IReadOnlyList<double[]> frames)
    {
        if (frames == null || frames.Count == 0)
        {
            return null;
        }

        var centerX = (rectangle.MinX + rectangle.MaxX) * 0.5;
        var centerY = (rectangle.MinY + rectangle.MaxY) * 0.5;
        double[]? best = null;
        var bestDistance = double.MaxValue;
        foreach (var corners in frames)
        {
            if (!TryGetCornerBounds(corners, out var minX, out var minY, out var maxX, out var maxY))
            {
                continue;
            }

            var blockCenterX = (minX + maxX) * 0.5;
            var blockCenterY = (minY + maxY) * 0.5;
            var rectangleContainsBlock = ContainsPoint(rectangle.MinX, rectangle.MinY, rectangle.MaxX, rectangle.MaxY, blockCenterX, blockCenterY);
            var blockContainsRectangle = ContainsPoint(minX, minY, maxX, maxY, centerX, centerY);
            if (!rectangleContainsBlock || !blockContainsRectangle)
            {
                continue;
            }

            var dx = blockCenterX - centerX;
            var dy = blockCenterY - centerY;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = corners;
            }
        }

        return best;
    }

    /// <summary>四角的轴对齐范围。</summary>
    private static bool TryGetCornerBounds(
        double[] corners,
        out double minX,
        out double minY,
        out double maxX,
        out double maxY)
    {
        minX = 0;
        minY = 0;
        maxX = 0;
        maxY = 0;
        if (corners == null || corners.Length < 8)
        {
            return false;
        }

        minX = maxX = corners[0];
        minY = maxY = corners[1];
        for (var i = 1; i < 4; i++)
        {
            var x = corners[i * 2];
            var y = corners[i * 2 + 1];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return maxX - minX > 1e-6 && maxY - minY > 1e-6;
    }

    /// <summary>点是否落在矩形内，边界放宽 1 个绘图单位。</summary>
    private static bool ContainsPoint(double minX, double minY, double maxX, double maxY, double x, double y)
    {
        const double tolerance = 1d;
        return x >= minX - tolerance
            && x <= maxX + tolerance
            && y >= minY - tolerance
            && y <= maxY + tolerance;
    }

    /// <summary>
    /// 在这张图的右下角坐标系里取纸面矩形内的文字。局部 X 为距右边的纸面毫米（沿底边朝左），
    /// 局部 Y 为距下边的纸面毫米（沿右边朝上），图框旋转时按格子自身方向判定命中。
    /// </summary>
    private static string ExtractCellText(CadTextExtractor.OwnerTextCache cache, FrameAxes axes, PaperCornerBox box)
    {
        return CadTextExtractor.ExtractFrameCellText(
            cache,
            axes.Origin,
            axes.Left * axes.CadPerMm,
            axes.Up * axes.CadPerMm,
            LocalRectangle.FromPoints(box.RightMm, box.BottomMm, box.LeftMm, box.TopMm));
    }

    /// <summary>属性 Tag「图号」的严格匹配名。</summary>
    private const string DrawingNumberAttributeTag = "图号";

    /// <summary>属性 Tag「图名」的严格匹配名。</summary>
    private const string TitleAttributeTag = "图名";

    /// <summary>
    /// 在当前布局空间一次性枚举顶层块参照属性，把框内非空「图号」「图名」填入作业。
    /// 同一框多个命中时取离该框右下角（MaxX, MinY）最近的；不深入嵌套块。
    /// </summary>
    private static void FillAttributeIdentities(
        Database database,
        ObjectId ownerId,
        IReadOnlyList<Result> results,
        IReadOnlyList<LocalRectangle> rectangles)
    {
        if (results.Count == 0 || results.Count != rectangles.Count)
        {
            return;
        }

        var numberCandidates = new List<(string Text, double X, double Y)>();
        var titleCandidates = new List<(string Text, double X, double Y)>();
        using (var tr = database.TransactionManager.StartTransaction())
        {
            var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
            foreach (ObjectId id in owner)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef)
                {
                    continue;
                }

                if (!IsEntityVisible(blockRef)
                    || string.Equals(blockRef.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase)
                    || !IsEntityLayerScannable(tr, blockRef)
                    || blockRef.AttributeCollection == null
                    || blockRef.AttributeCollection.Count == 0)
                {
                    continue;
                }

                foreach (ObjectId attributeId in blockRef.AttributeCollection)
                {
                    if (tr.GetObject(attributeId, OpenMode.ForRead, false) is not AttributeReference attribute
                        || !IsEntityVisible(attribute))
                    {
                        continue;
                    }

                    var tag = (attribute.Tag ?? "").Trim();
                    var text = GetAttributeDisplayText(attribute).Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var point = attribute.Position;
                    if (string.Equals(tag, DrawingNumberAttributeTag, StringComparison.Ordinal))
                    {
                        numberCandidates.Add((text, point.X, point.Y));
                    }
                    else if (string.Equals(tag, TitleAttributeTag, StringComparison.Ordinal))
                    {
                        titleCandidates.Add((text, point.X, point.Y));
                    }
                }
            }

            tr.Commit();
        }

        if (numberCandidates.Count == 0 && titleCandidates.Count == 0)
        {
            return;
        }

        for (var i = 0; i < results.Count; i++)
        {
            var rectangle = rectangles[i];
            var job = results[i].Job;
            if (string.IsNullOrWhiteSpace(job.CadDrawingNumber)
                && TryPickClosestAttribute(numberCandidates, rectangle, out var drawingNumber))
            {
                job.CadDrawingNumber = drawingNumber;
                job.DrawingNumber = drawingNumber;
            }

            if (string.IsNullOrWhiteSpace(job.CadTitle)
                && TryPickClosestAttribute(titleCandidates, rectangle, out var title))
            {
                job.CadTitle = title;
                job.Title = title;
            }
        }
    }

    /// <summary>
    /// 在矩形范围内的候选中选取离右下角最近的属性值。
    /// </summary>
    private static bool TryPickClosestAttribute(
        IReadOnlyList<(string Text, double X, double Y)> candidates,
        LocalRectangle rectangle,
        out string text)
    {
        text = "";
        var bestDistance = double.MaxValue;
        string? best = null;
        var cornerX = rectangle.MaxX;
        var cornerY = rectangle.MinY;
        foreach (var candidate in candidates)
        {
            if (!IsPointInsideRectangle(rectangle, candidate.X, candidate.Y))
            {
                continue;
            }

            var dx = candidate.X - cornerX;
            var dy = candidate.Y - cornerY;
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = candidate.Text;
        }

        if (best == null)
        {
            return false;
        }

        text = best;
        return true;
    }

    /// <summary>
    /// 判断点是否在打印矩形内。有实际角点时用多边形判定，避免旋转框 AABB 误收框外点。
    /// </summary>
    private static bool IsPointInsideRectangle(LocalRectangle rectangle, double x, double y)
    {
        if (rectangle.CornerPoints is { Length: >= 8 } points)
        {
            var polygon = new[]
            {
                new Point3d(points[0], points[1], 0),
                new Point3d(points[2], points[3], 0),
                new Point3d(points[4], points[5], 0),
                new Point3d(points[6], points[7], 0)
            };
            return IsPointInsidePolygon(new Point3d(x, y, 0), polygon);
        }

        return rectangle.Contains(x, y);
    }

    /// <summary>射线法判断点是否在多边形内（含边界）。</summary>
    private static bool IsPointInsidePolygon(Point3d point, Point3d[] polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Length - 1;
             current < polygon.Length;
             previous = current++)
        {
            var start = polygon[previous];
            var end = polygon[current];
            var onSegment =
                Math.Abs((end.Y - start.Y) * (point.X - start.X) - (end.X - start.X) * (point.Y - start.Y)) <= 1e-9
                && point.X >= Math.Min(start.X, end.X) - 1e-9
                && point.X <= Math.Max(start.X, end.X) + 1e-9
                && point.Y >= Math.Min(start.Y, end.Y) - 1e-9
                && point.Y <= Math.Max(start.Y, end.Y) + 1e-9;
            if (onSegment)
            {
                return true;
            }

            var crossesScanLine = (start.Y > point.Y) != (end.Y > point.Y);
            if (crossesScanLine
                && point.X < (end.X - start.X) * (point.Y - start.Y) / (end.Y - start.Y + 1e-30) + start.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>读取属性显示文字。</summary>
    private static string GetAttributeDisplayText(AttributeReference attribute)
    {
        return attribute.TextString ?? "";
    }

    private static Point3d[] GetWorldPoints(LocalRectangle rectangle)
    {
        if (rectangle.CornerPoints is { Length: >= 8 } points)
        {
            return new[]
            {
                new Point3d(points[0], points[1], 0),
                new Point3d(points[2], points[3], 0),
                new Point3d(points[4], points[5], 0),
                new Point3d(points[6], points[7], 0)
            };
        }

        return CadSelectionWindow.GetCorners(rectangle);
    }

    // ═══════════════════════════════════════════════════════════════
    // 布局范围判断（与 TitleBlockScanner 一致）
    // ═══════════════════════════════════════════════════════════════

    private static bool ShouldScanLayout(
        Layout layout,
        TitleBlockScanScope scope,
        string? currentSpaceName,
        ISet<string>? allowedLayoutNames = null)
    {
        if (allowedLayoutNames != null && allowedLayoutNames.Count > 0)
        {
            var name = layout.LayoutName ?? "";
            if (!allowedLayoutNames.Contains(name))
            {
                return false;
            }
        }

        switch (scope)
        {
            case TitleBlockScanScope.PaperLayouts:
                return !layout.ModelType;
            case TitleBlockScanScope.ModelSpace:
                return layout.ModelType;
            case TitleBlockScanScope.CurrentSpace:
                return IsCurrentSpace(layout, currentSpaceName);
            case TitleBlockScanScope.AllSpaces:
                return true;
            default:
                return false;
        }
    }

    private static bool IsCurrentSpace(Layout layout, string? currentSpaceName)
    {
        if (string.IsNullOrWhiteSpace(currentSpaceName))
        {
            return false;
        }

        return string.Equals(layout.LayoutName, currentSpaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCurrentSpaceName(Database db)
    {
        try
        {
            return LayoutManager.Current.CurrentLayout;
        }
        catch
        {
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var csId = db.CurrentSpaceId;
                if (!csId.IsNull && tr.GetObject(csId, OpenMode.ForRead) is BlockTableRecord btr)
                {
                    var layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);
                    return layout.LayoutName;
                }
            }
            catch
            {
                // 没有打开的文档或数据库不可用
            }

            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 递归实体遍历
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 递归遍历实体及其子实体（块定义内部），检测矩形 Polyline 并收集 Line/Polyline 线段。
    ///
    /// 对每个实体：
    ///   1. 如果是 Polyline → 尝试检测矩形 → 加入结果
    ///   2. 如果是 Line → 提取线段加入 segments
    ///   3. 如果是开放 2 点 Polyline（无圆弧）→ 提取线段加入 segments
    ///   4. 如果是 BlockReference → 进入块定义递归
    ///
    /// 过滤规则：
    ///   - 跳过临时序号标注图层
    ///   - 跳过不可打印图层的实体
    ///   - 跳过 CAD 判定为不可见的实体（动态块隐藏状态等）
    ///   - 跳过被 XCLIP 裁切过的块参照
    ///   - 防循环：同一个块定义只处理一次（visitedDefinitions）
    ///   - 防过深：递归深度上限 12 层
    /// </summary>
    /// <param name="tr">事务</param>
    /// <param name="entity">当前实体</param>
    /// <param name="transform">从当前实体坐标系到 WCS 的累积变换矩阵</param>
    /// <param name="rectangles">收集到的矩形列表</param>
    /// <param name="segments">收集到的线段列表（Line 和开放 Polyline）</param>
    /// <param name="visitedDefinitions">已访问的块定义 ID，防循环</param>
    /// <param name="depth">当前递归深度</param>
    /// <param name="recognizeFourLineRectangles">是否启用四个独立边实体组成矩形的识别</param>
    private static void CollectEntityRectangles(
        Transaction tr,
        Entity entity,
        Matrix3d transform,
        ICollection<LocalRectangle> rectangles,
        ICollection<FourLineRectangleFinder.Segment>? segments,
        ISet<ObjectId> visitedDefinitions,
        int depth,
        bool recognizeFourLineRectangles)
    {
        // 跳过标注图层——避免把自己的标注当矩形扫进去
        if (string.Equals(entity.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 用户在 CAD 中看不到的实体不能成为打印边界。这里必须在所有类型分支之前统一拦截，
        // 不仅覆盖闭合 Polyline/Polyline2d/Polyline3d，也覆盖隐藏的父块参照；否则父块不可见时，
        // 递归进入块定义仍可能把内部矩形识别成图框。图框允许位于 Defpoints 等不打印图层，
        // 因此这里只检查实体自身 Visible 以及图层开启/未冻结，不检查 IsPlottable。
        if (!IsEntityVisible(entity) || !IsFrameBoundaryLayerScannable(tr, entity))
        {
            return;
        }

        // ── 分支 0：Line → 收集线段（仅块内部，最多 3 层）──
        if (segments != null
            && depth <= 3
            && entity is Line line)
        {
            FourLineRectangleFinder.TryAddLine(line, transform, segments);
        }

        // ── 分支 0.5：开放直线型 PL → 每个实体只提取首尾端点作为一条矩形边 ──
        if (segments != null
            && depth <= 3
            && entity is Polyline plSegment
            && FourLineRectangleFinder.TryGetStraightOpenPolylineSegment(plSegment, transform, out var polylineSegment))
        {
            segments.Add(polylineSegment);
        }

        // 老式开放 POLYLINE 也按一个独立实体处理；仅允许全部顶点共线且无圆弧。
        if (segments != null
            && depth <= 3
            && entity is Polyline2d pl2dSegment
            && FourLineRectangleFinder.TryGetStraightOpenPolyline2dSegment(
                tr,
                pl2dSegment,
                transform,
                out var legacyPolylineSegment))
        {
            segments.Add(legacyPolylineSegment);
        }

        // ── 分支 1：Polyline（轻量线）→ 矩形检测 ──
        if (entity is Polyline polyline
            // 图框 PL 可能专门放在 Defpoints 或其他”不打印”辅助层；
            // 它只提供打印边界，不代表该层图素会进入打印内容，因此这里只要求图层开启且未冻结。
            && IsFrameBoundaryLayerScannable(tr, entity)
            && RectangleGeometry.TryGetRectangle(
                polyline,
                transform,
                requireClosed: true,
                out var rectangle))
        {
            // 先全部收集，不去重——不同实例的同一定义各自独立，去重放在后续 FilterRectangles
            rectangles.Add(rectangle);
        }

        // ── 分支 1b：Polyline2d（老式 POLYLINE+VERTEX）→ 矩形检测 ──
        // 旧版 CAD 绘制的图框常用老式多段线，LIST 命令输出 “POLYLINE/VERTEX”，
        // .NET API 类型为 Polyline2d，与轻量 Polyline 不同，需单独处理。
        if (entity is Polyline2d polyline2d
            && IsFrameBoundaryLayerScannable(tr, entity)
            && RectangleGeometry.TryGetRectangleFrom2d(
                tr,
                polyline2d,
                transform,
                requireClosed: false,
                out var rectangle2d))
        {
            rectangles.Add(rectangle2d);
        }

        // ── 分支 1c：Polyline3d（3DPOLY）→ 矩形检测 ──
        // 三维多段线在 XY 平面上也可构成矩形图框，顶点类型为 Vertex3d。
        if (entity is Polyline3d polyline3d
            && IsFrameBoundaryLayerScannable(tr, entity)
            && RectangleGeometry.TryGetRectangleFrom3d(
                tr,
                polyline3d,
                transform,
                requireClosed: false,
                out var rectangle3d))
        {
            rectangles.Add(rectangle3d);
        }

        // ── 分支 2：BlockReference → 缓存 + 递归进入 ──
        if (entity is not BlockReference blockReference || depth >= 12)
        {
            return;
        }

        // XCLIP 裁切过的块不参与扫描
        if (IsBlockClipped(tr, blockReference))
        {
            return;
        }

        var definitionId = blockReference.BlockTableRecord;
        var instanceXform = blockReference.BlockTransform * transform;

        // 块定义缓存：同一块定义只遍历一次，后续实例直接变换缓存的局部坐标结果。
        if (BlockDefinitionCache.TryGetValue(definitionId, out var cachedRects))
        {
            if (cachedRects.Count > 0)
            {
                rectangles.Add(RectangleGeometry.TransformRectangle(cachedRects[0], instanceXform));
            }
            return;
        }

        // 防循环：同一个块定义在一条递归路径上只进入一次
        if (!visitedDefinitions.Add(definitionId))
        {
            return;
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);

            // 用单位矩阵遍历获取块局部坐标，存入缓存供其他实例复用。
            // 同一块定义内只保留最大的矩形框。
            // 四线段拼合在块内独立处理；深度 ≤ 3 层。
            // 四叉树负责限制邻域搜索成本，因此不再用固定 200 条上限截断复杂块。
            var localRects = new List<LocalRectangle>();
            var blockSegments = recognizeFourLineRectangles && depth < 3
                ? new List<FourLineRectangleFinder.Segment>()
                : null;
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity nested)
                {
                    continue;
                }

                // 递归阶段不能按“是否打印”提前截断，否则块内不可打印层上的图框 PL
                // 永远到不了下面的矩形检测分支。图框边实体只要求图层可见，内容输出仍严格检查可打印性。
                if (!IsEntityLayerVisibleForScanning(tr, nested))
                {
                    continue;
                }

                if (!IsEntityVisible(nested))
                {
                    continue;
                }

                // 用单位矩阵递归 → 子实体结果均在当前块定义局部坐标下
                CollectEntityRectangles(
                    tr,
                    nested,
                    Matrix3d.Identity,
                    localRects,
                    blockSegments,
                    visitedDefinitions,
                    depth + 1,
                    recognizeFourLineRectangles);
            }

            if (blockSegments != null && blockSegments.Count >= 4)
            {
                var segRects = FourLineRectangleFinder.Find(blockSegments, _activeCancel, (msg, cur, tot) => ReportScan(msg, cur, tot));
                localRects.AddRange(segRects);
            }

            // 缓存局部坐标下的最大矩形，并变换到当前实例的世界坐标
            List<LocalRectangle> cacheEntry;
            if (localRects.Count > 0)
            {
                var largest = localRects.OrderByDescending(Area).First();
                cacheEntry = new List<LocalRectangle> { largest };
                rectangles.Add(RectangleGeometry.TransformRectangle(largest, instanceXform));
            }
            else
            {
                cacheEntry = new List<LocalRectangle>();
            }

            BlockDefinitionCache[definitionId] = cacheEntry;
        }
        catch
        {
            // 个别块定义可能损坏或无权限访问，跳过不影响整体扫描
        }
        finally
        {
            // 离开时移除，允许其他路径再次进入同一定义（不同父级下可重复）
            visitedDefinitions.Remove(definitionId);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 可见性 & 图层过滤
    // ═══════════════════════════════════════════════════════════════

    /// <summary>查询实体在 CAD 引擎中的可见性。动态块隐藏状态自动为 false。</summary>
    private static bool IsEntityVisible(Entity entity)
    {
        try
        {
            return entity.Visible;
        }
        catch
        {
            // 老版本 API 可能无此属性，宁可多扫不丢
            return true;
        }
    }

    /// <summary>
    /// 图层是否可扫描：必须在开启、未冻结、可打印的图层上。
    /// 关闭或冻结的图层通常对应动态块的隐藏状态。
    /// 结果缓存在 LayerScannableCache 中，同一图层只需查一次。
    /// </summary>
    private static bool IsEntityLayerScannable(Transaction tr, Entity entity)
    {
        try
        {
            if (entity.LayerId.IsNull)
            {
                return false;
            }

            if (LayerScannableCache.TryGetValue(entity.LayerId, out var cached))
            {
                return cached;
            }

            if (tr.GetObject(entity.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer)
            {
                LayerScannableCache[entity.LayerId] = false;
                return false;
            }

            var result = !layer.IsOff      // 图层未关闭
                && !layer.IsFrozen         // 图层未冻结
                && layer.IsPlottable;      // 图层可打印
            LayerScannableCache[entity.LayerId] = result;
            return result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 图框边界的图层过滤规则：图层必须开启且未冻结，但允许设置为“不打印”。
    /// 不打印属性只决定图素是否输出，不应阻止闭合 PL 或四个独立边实体作为打印边界。
    /// </summary>
    private static bool IsFrameBoundaryLayerScannable(Transaction tr, Entity entity)
    {
        return IsEntityLayerVisibleForScanning(tr, entity);
    }

    /// <summary>
    /// 判断图层在当前图形中是否可见，仅检查关闭/冻结状态，不检查 IsPlottable。
    /// 此规则只用于查找图框边界和递归进入块定义；内容过滤仍调用严格的
    /// <see cref="IsEntityLayerScannable"/>，不会把不打印层误当成有效图纸内容。
    /// </summary>
    private static bool IsEntityLayerVisibleForScanning(Transaction tr, Entity entity)
    {
        try
        {
            if (entity.LayerId.IsNull
                || tr.GetObject(entity.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer)
            {
                return false;
            }

            return !layer.IsOff && !layer.IsFrozen;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查块参照是否被 XCLIP 命令裁切过。
    /// XCLIP 通过扩展字典中的 "ACAD_FILTER" 条目存储裁切边界，
    /// 裁切后的块参照显示不全，内部矩形框不应参与扫描。
    /// </summary>
    private static bool IsBlockClipped(Transaction tr, BlockReference blockRef)
    {
        try
        {
            if (blockRef.ExtensionDictionary == ObjectId.Null)
            {
                return false;
            }

            var extDict = (DBDictionary)tr.GetObject(blockRef.ExtensionDictionary, OpenMode.ForRead);
            return extDict.Contains("ACAD_FILTER");
        }
        catch
        {
            return false;
        }
    }

    // 多段线矩形判定与角点变换统一由 RectangleGeometry 提供，扫描器仅保留扫描策略。
    // ═══════════════════════════════════════════════════════════════
    // 矩形过滤：去重、去嵌套
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 过滤矩形列表：去重 + 去嵌套。
    ///
    /// 算法：
    ///   1. 按面积降序排列（先处理大的）
    ///   2. 去重：两个矩形边界相同 或 高度重叠（>90%），只保留第一个
    ///   3. 去嵌套：小矩形完全被大矩形包含时移除小的，避免同一区域重复打印
    /// </summary>
    private static List<LocalRectangle> FilterRectangles(IEnumerable<LocalRectangle> source)
    {
        var unique = new List<LocalRectangle>();

        // 按面积降序遍历，确保大的先进入 unique 列表
        foreach (var rectangle in source.OrderByDescending(Area))
        {
            var tolerance = Math.Max(rectangle.MaxX - rectangle.MinX, rectangle.MaxY - rectangle.MinY) * 0.002;
            // 和已保留的矩形比较：边界相同或高度重叠 → 视为重复，跳过
            if (unique.Any(existing =>
                    SameBounds(existing, rectangle, tolerance)
                    || HasDuplicateOverlap(existing, rectangle)))
            {
                continue;
            }

            unique.Add(rectangle);
        }

        // 去嵌套：任何候选打印框只要被另一个候选框完整包含，就舍弃内部框。
        // 外框已经覆盖该打印区域，不能再让内框生成第二个打印任务。
        return unique
            .Where(candidate => !unique.Any(container =>
                !ReferenceEquals(container, candidate)
                && Area(container) > Area(candidate)
                && Contains(container, candidate)))
            .ToList();
    }

    /// <summary>两个矩形包围盒是否相同（四个边界都在容差内）。</summary>
    private static bool SameBounds(LocalRectangle a, LocalRectangle b, double tolerance)
    {
        return Math.Abs(a.MinX - b.MinX) <= tolerance
            && Math.Abs(a.MinY - b.MinY) <= tolerance
            && Math.Abs(a.MaxX - b.MaxX) <= tolerance
            && Math.Abs(a.MaxY - b.MaxY) <= tolerance;
    }

    /// <summary>
    /// 两个矩形是否高度重叠（视为同一图纸的多重描边）。
    ///
    /// 判断标准：
    ///   - 重叠面积 ≥ 较小矩形面积的 90%（绝大部分重合）
    ///   - 重叠面积 ≥ 较大矩形面积的 82%（不是小矩形套在大矩形角落）
    ///   - 宽高相似度均 ≥ 90%（尺寸几乎一样）
    /// </summary>
    private static bool HasDuplicateOverlap(LocalRectangle a, LocalRectangle b)
    {
        var overlapWidth = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        var overlapHeight = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        var overlapArea = overlapWidth * overlapHeight;
        if (overlapArea <= 0)
        {
            return false;
        }

        var areaA = Area(a);
        var areaB = Area(b);
        if (areaA <= 0 || areaB <= 0)
        {
            return false;
        }

        // 重叠占小矩形的比例
        var smallerCoverage = overlapArea / Math.Min(areaA, areaB);
        // 重叠占大矩形的比例
        var largerCoverage = overlapArea / Math.Max(areaA, areaB);
        // 宽高相似度：排除同心嵌套（如 A2 外框 + A3 内框）
        var widthSimilarity = Math.Min(a.MaxX - a.MinX, b.MaxX - b.MinX)
            / Math.Max(a.MaxX - a.MinX, b.MaxX - b.MinX);
        var heightSimilarity = Math.Min(a.MaxY - a.MinY, b.MaxY - b.MinY)
            / Math.Max(a.MaxY - a.MinY, b.MaxY - b.MinY);

        return smallerCoverage >= 0.90
            && largerCoverage >= 0.82
            && widthSimilarity >= 0.90
            && heightSimilarity >= 0.90;
    }

    // ═══════════════════════════════════════════════════════════════
    // 几何工具
    // ═══════════════════════════════════════════════════════════════

    /// <summary>outer 是否完全包含 inner（含 0.3% 容差）。</summary>
    private static bool Contains(LocalRectangle outer, LocalRectangle inner)
    {
        var tolerance = Math.Max(outer.MaxX - outer.MinX, outer.MaxY - outer.MinY) * 0.003;
        return inner.MinX >= outer.MinX - tolerance
            && inner.MinY >= outer.MinY - tolerance
            && inner.MaxX <= outer.MaxX + tolerance
            && inner.MaxY <= outer.MaxY + tolerance;
    }

    /// <summary>矩形包围盒面积（>=0）。</summary>
    private static double Area(LocalRectangle rectangle)
    {
        return Math.Max(0, rectangle.MaxX - rectangle.MinX)
            * Math.Max(0, rectangle.MaxY - rectangle.MinY);
    }

    /// <summary>两个轴对齐矩形是否相交。</summary>
    private static bool Intersects(LocalRectangle rectangle, LocalRectangle window)
    {
        return rectangle.MaxX >= window.MinX
            && rectangle.MinX <= window.MaxX
            && rectangle.MaxY >= window.MinY
            && rectangle.MinY <= window.MaxY;
    }

    // ═══════════════════════════════════════════════════════════════
    // 空框过滤：检查矩形内是否存在实际的绘图内容（图素）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 过滤掉没有任何可见可打印图素的空矩形框。
    ///
    /// 对每个候选矩形，递归遍历布局内的所有实体，检查是否有至少一个
    /// 可见、可打印、非矩形框自身的实体落入矩形范围内。
    /// </summary>
    /// <param name="document">当前 CAD 文档</param>
    /// <param name="ownerId">布局 BlockTableRecord 的 ObjectId</param>
    /// <param name="candidates">待检查的候选矩形列表</param>
    /// <returns>包含实际图素的矩形列表</returns>
	    /// <summary>
	    /// 空框过滤：先预扫描一次收集所有实体的世界坐标外包盒（含嵌套块），
	    /// 再对每个候选矩形做内存级包围盒相交判断。
	    /// 避免对每个矩形都遍历 CAD 数据库——O(R×E) → O(E + R×B)。
	    /// </summary>
	    private static List<LocalRectangle> FilterEmptyRectangles(
	        Database database, ObjectId ownerId, List<LocalRectangle> candidates)
	    {
	        using var tr = database.TransactionManager.StartTransaction();
	        var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);

	        // 预扫描：一次性收集所有实体的世界坐标外包盒（含嵌套块）
	        var entityBoxes = new List<LocalRectangle>();
	        var boxSw = _activeProfile != null ? Stopwatch.StartNew() : null;
	        CollectEntityBoxesForContentCheck(tr, owner, Matrix3d.Identity, entityBoxes,
	            new HashSet<ObjectId>(), 0);
	        if (boxSw != null && _activeProfile != null)
	        {
	            boxSw.Stop();
	            _activeProfile.EmptyBoxCollectMs += boxSw.ElapsedMilliseconds;
	        }

	        // 内存级矩形相交判断，不再访问 CAD 数据库
	        var result = new List<LocalRectangle>();
	        foreach (var rect in candidates)
	        {
	            if (AnyIntersects(entityBoxes, rect))
	            {
	                result.Add(rect);
	            }
	        }

	        return result;
	    }

	    /// <summary>
	    /// 递归收集空间内所有实体的世界坐标外包盒，过滤规则与旧 CheckEntityContent 一致：
	    /// 跳过临时标注图层、不可打印图层、不可见实体；块参照递归进入（防循环、防过深）。
	    /// visitedDefinitions 的 Add/Remove 模式允许同一块定义从不同父路径重新进入。
	    /// </summary>
	    private static void CollectEntityBoxesForContentCheck(
	        Transaction tr,
	        BlockTableRecord owner,
	        Matrix3d transform,
	        List<LocalRectangle> boxes,
	        HashSet<ObjectId> visitedDefinitions,
	        int depth)
	    {
	        foreach (ObjectId id in owner)
	        {
	            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
	            {
	                continue;
	            }

	            // 跳过临时标注图层
	            if (string.Equals(entity.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase))
	            {
	                continue;
	            }

	            // 跳过不可打印图层的实体
	            if (!IsEntityLayerScannable(tr, entity))
	            {
	                continue;
	            }

	            // 跳过不可见实体
	            if (!IsEntityVisible(entity))
	            {
	                continue;
	            }

	            // 收集当前实体的世界坐标外包盒
	            try
	            {
	                var ext = entity.GeometricExtents;
	                var extMin = ext.MinPoint.TransformBy(transform);
	                var extMax = ext.MaxPoint.TransformBy(transform);
	                boxes.Add(LocalRectangle.FromPoints(
	                    Math.Min(extMin.X, extMax.X), Math.Min(extMin.Y, extMax.Y),
	                    Math.Max(extMin.X, extMax.X), Math.Max(extMin.Y, extMax.Y)));
	            }
	            catch
	            {
	                // 部分实体（如空块参照）可能抛出异常，忽略
	            }

	            // ── 递归进入块参照 ──
	            if (entity is not BlockReference blockRef || depth >= 5)
	            {
	                continue;
	            }

	            if (IsBlockClipped(tr, blockRef))
	            {
	                continue;
	            }

	            var definitionId = blockRef.BlockTableRecord;
	            if (!visitedDefinitions.Add(definitionId))
	            {
	                continue;
	            }

	            try
	            {
	                var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
	                var nestedTransform = blockRef.BlockTransform * transform;
	                CollectEntityBoxesForContentCheck(tr, definition, nestedTransform, boxes,
	                    visitedDefinitions, depth + 1);
	            }
	            catch
	            {
	                // 损坏的块定义无法读取，跳过
	            }

	            visitedDefinitions.Remove(definitionId);
	        }
	    }

	    /// <summary>
	    /// 检查预收集的实体外包盒列表中是否有任意一个与目标矩形相交。
	    /// 纯内存操作，短路退出。
	    /// </summary>
	    private static bool AnyIntersects(List<LocalRectangle> boxes, LocalRectangle target)
	    {
	        foreach (var box in boxes)
	        {
	            if (Intersects(box, target))
	            {
	                return true;
	            }
	        }

	        return false;
	    }
	}
