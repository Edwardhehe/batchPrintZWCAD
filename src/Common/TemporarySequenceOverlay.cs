using System;
using System.Collections.Generic;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadRuntimeException = ZwSoft.ZwCAD.Runtime.Exception;
#endif

namespace ZwcadBatchPlot;

public sealed class TemporarySequenceOverlay : IDisposable
{
    private const string LayerName = "ZBP_TEMP_SEQUENCE_OVERLAY";
    private const string TextStyleName = "ZBP_TEMP_SEQUENCE_TEXT";
    private readonly Document _document;
    private readonly List<ObjectId> _entityIds = new();
    private readonly Dictionary<PlotJob, OverlayEntityGroup> _entityGroups = new();
    // 红框实体 → 打印任务的映射，用户删除红框时据此定位需要移除的 Job
    private readonly Dictionary<ObjectId, PlotJob> _frameJobs = new();
    // 本次 ERASE 命令中已被删除红框对应的 Job，命令结束时统一通知窗体
    private readonly HashSet<PlotJob> _erasedFrameJobs = new();
    // 全局红框注册表（静态，跨窗口共享）：ERASE 选择过滤时用于判定“是不是本插件的临时红框”
    private static readonly object RegisteredFramesSync = new();
    private static readonly HashSet<ObjectId> RegisteredFrameIds = new();
    private PlotJob? _highlightedJob;
    // 当前是否正处于 ERASE/DELETE 命令中（只有此期间才做选择过滤与删除监听）
    private bool _eraseCommandActive;
    // 覆盖层自身正在增删实体（如 Clear），此时忽略 ObjectErased 事件，避免误判为用户删除
    private bool _changingOverlay;
    private bool _disposed;

    // 用户在图纸中删除了某个红框时触发，参数为对应的打印任务；窗体订阅后同步移除表格行
    public event Action<PlotJob>? FrameErased;

    private sealed class OverlayEntityGroup
    {
        public ObjectId FrameId { get; set; }
        public List<ObjectId> LabelIds { get; } = new();
        public double NormalFrameWidth { get; set; }
        public double HighlightFrameWidth { get; set; }
    }

    public TemporarySequenceOverlay(Document document)
    {
        _document = document;
        // 监听 ERASE/DELETE 命令生命周期与选择集变化，实现“只允许删红框、删红框即删对应打印任务”
        _document.CommandWillStart += DocumentCommandWillStart;
        _document.CommandEnded += DocumentCommandEnded;
        _document.CommandCancelled += DocumentCommandCancelled;
        _document.CommandFailed += DocumentCommandCancelled;
        _document.Editor.SelectionAdded += EditorSelectionAdded;
        _document.Database.ObjectErased += DatabaseObjectErased;
    }

    public void Show(IReadOnlyList<PlotJob> jobs, int highlightIndex)
    {
        // 兼容矩形批量窗口的旧调用：先把过滤后下标转换成具体 Job，核心逻辑统一按对象引用高亮。
        var highlightJob = highlightIndex >= 0 && highlightIndex < jobs.Count ? jobs[highlightIndex] : null;
        Show(jobs, highlightJob);
    }

    public void Show(IReadOnlyList<PlotJob> jobs, PlotJob? highlightJob = null)
    {
        Show(jobs, highlightJob, null);
    }

    public void Show(IReadOnlyList<PlotJob> jobs, PlotJob? highlightJob, Func<PlotJob, int, string>? labelProvider)
    {
        // 整批重建时 Clear 不立即刷新，避免“清空一次 + 绘制一次”造成两次 Regen。
        Clear(repaint: false);

        using var docLock = _document.LockDocument();
        using var tr = _document.Database.TransactionManager.StartTransaction();
        var db = _document.Database;
        var layerId = EnsureLayer(tr, db);
        var textStyleId = EnsureTextStyle(tr, db);

        // 单张打印和矩形框批量的 Job 坐标是 DCS，绘制到图纸前需回退到 WCS
        // DCS→WCS：绘制前回退坐标
        var dcsToWcs = Matrix3d.Identity;
        // UCS X 轴在 WCS 中的角度 — 红框和数字按此旋转，保证 UCS 视图中显示为正
        var ucsAngle = 0d;
        try
        {
            if (_document.Database.TileMode)
            {
                var view = _document.Editor.GetCurrentView();
                dcsToWcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                dcsToWcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * dcsToWcs;
                dcsToWcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * dcsToWcs;

                var ucs = _document.Editor.CurrentUserCoordinateSystem;
                ucsAngle = Math.Atan2(ucs[1, 0], ucs[0, 0]);
            }
        }
        catch
        {
        }

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (!TryGetBounds(job, dcsToWcs, out var minX, out var minY, out var maxX, out var maxY))
            {
                continue;
            }

            var width = maxX - minX;
            var height = maxY - minY;
            var minSide = Math.Min(width, height);
            var padding = Math.Max(minSide * 0.035, 10d);
            var textHeight = GetTextHeight(width, height);
            // 高亮行：黄色 (ACI 2)、加粗边框；普通行：红色 (ACI 1)
            var isHighlight = ReferenceEquals(job, highlightJob);
            var color = GetOverlayColor(isHighlight);
            // 保存普通/高亮两套宽度，后续 DataGrid 换行只改实体属性，不再整批删除重画。
            var normalFrameWidth = GetFrameWidth(minSide);
            var highlightFrameWidth = Math.Max(normalFrameWidth * 3d, minSide / 20d);
            var frameWidth = isHighlight ? highlightFrameWidth : normalFrameWidth;
            var lineWeight = GetLineWeight(minSide);

            var ownerId = GetJobOwnerId(tr, db, job);
            if (ownerId.IsNull)
            {
                continue;
            }

            var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForWrite);

            // 红框按 UCS 角度旋转后绘制到 WCS，保证用户在 UCS 视图中看是正的
            var cosA = Math.Cos(ucsAngle);
            var sinA = Math.Sin(ucsAngle);
            var cx = (minX + maxX) / 2d;
            var cy = (minY + maxY) / 2d;
            var hw = (maxX - minX) / 2d + padding;
            var hh = (maxY - minY) / 2d + padding;

            Point2d Rot(double dx, double dy) =>
                new(cx + dx * cosA - dy * sinA, cy + dx * sinA + dy * cosA);

            var frame = new Polyline(4)
            {
                Closed = true,
                Color = color,
                LayerId = layerId,
                LineWeight = lineWeight,
                ConstantWidth = frameWidth
            };
            frame.AddVertexAt(0, Rot(-hw, -hh), 0, 0, 0);
            frame.AddVertexAt(1, Rot(+hw, -hh), 0, 0, 0);
            frame.AddVertexAt(2, Rot(+hw, +hh), 0, 0, 0);
            frame.AddVertexAt(3, Rot(-hw, +hh), 0, 0, 0);

            var group = new OverlayEntityGroup
            {
                NormalFrameWidth = normalFrameWidth,
                HighlightFrameWidth = highlightFrameWidth
            };
            group.FrameId = AddEntity(tr, owner, frame);
            // 记录红框与 Job 的对应关系，并登记到全局注册表，供 ERASE 过滤和删除回调使用
            _frameJobs[group.FrameId] = job;
            RegisterFrame(group.FrameId);

            var center = new Point3d((minX + maxX) / 2d, (minY + maxY) / 2d, 0);
            // 默认临时标注显示打印顺序；图号重排预览时可临时显示预计写入的新图号。
            var labelText = labelProvider?.Invoke(job, i) ?? (i + 1).ToString();
            AddBoldLabel(tr, owner, layerId, textStyleId, color, center, labelText, textHeight, ucsAngle, group.LabelIds);
            _entityGroups[job] = group;
        }

        _highlightedJob = highlightJob;
        tr.Commit();
        Regen();
    }

    public void Clear(bool repaint = true)
    {
        if (_entityIds.Count == 0)
        {
            return;
        }

        // 标记覆盖层正在自我清理，期间的 ObjectErased 事件不算“用户删除红框”
        _changingOverlay = true;
        try
        {
            using var docLock = _document.LockDocument();
            using var tr = _document.Database.TransactionManager.StartTransaction();
            foreach (var id in _entityIds)
            {
                if (id.IsNull || id.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                {
                    entity.Erase();
                }
            }

            tr.Commit();
        }
        catch
        {
        }
        finally
        {
            // 从全局注册表注销全部红框，避免残留 ObjectId 干扰后续 ERASE 过滤
            UnregisterFrames(_frameJobs.Keys);
            _entityIds.Clear();
            _entityGroups.Clear();
            _frameJobs.Clear();
            _highlightedJob = null;
            _changingOverlay = false;
            if (repaint)
            {
                Regen();
            }
        }
    }

    // ERASE/DELETE 命令即将开始：进入监听状态，并过滤命令前已选中的对象（PICKFIRST 选择集）
    private void DocumentCommandWillStart(object sender, CommandEventArgs e)
    {
        if (_disposed || !IsEraseCommand(e.GlobalCommandName))
        {
            return;
        }

        _eraseCommandActive = true;
        _erasedFrameJobs.Clear();
        FilterImpliedSelection();
    }

    // ERASE 命令正常结束：此时删除已成为事实，统一通知窗体移除对应打印任务行
    private void DocumentCommandEnded(object sender, CommandEventArgs e)
    {
        if (!_eraseCommandActive || !IsEraseCommand(e.GlobalCommandName))
        {
            return;
        }

        _eraseCommandActive = false;
        var erasedJobs = _erasedFrameJobs.ToList();
        _erasedFrameJobs.Clear();
        foreach (var job in erasedJobs)
        {
            FrameErased?.Invoke(job);
        }
    }

    // ERASE 命令被取消或失败：删除已被 CAD 回滚，丢弃暂存的删除记录，不通知窗体
    private void DocumentCommandCancelled(object sender, CommandEventArgs e)
    {
        if (!_eraseCommandActive || !IsEraseCommand(e.GlobalCommandName))
        {
            return;
        }

        _eraseCommandActive = false;
        _erasedFrameJobs.Clear();
    }

    private void EditorSelectionAdded(object sender, SelectionAddedEventArgs e)
    {
        if (!_eraseCommandActive)
        {
            return;
        }

        // 批量打印窗口打开并执行 ERASE/删除图素时，只允许临时红框进入选择集。
        // 倒序移除，避免删除一项后后续下标发生变化。
        for (var i = e.AddedObjects.Count - 1; i >= 0; i--)
        {
            if (!IsRegisteredFrame(e.AddedObjects[i].ObjectId))
            {
                e.Remove(i);
            }
        }
    }

    // 数据库对象被删除：仅在 ERASE 命令期间、且非覆盖层自身清理时，记录被删红框对应的 Job
    private void DatabaseObjectErased(object sender, ObjectErasedEventArgs e)
    {
        if (!_eraseCommandActive || _changingOverlay || !e.Erased)
        {
            return;
        }

        if (_frameJobs.TryGetValue(e.DBObject.ObjectId, out var job))
        {
            _erasedFrameJobs.Add(job);
        }
    }

    // 处理“先选后删”场景：把 ERASE 开始前已存在的 PICKFIRST 选择集过滤为只剩红框
    private void FilterImpliedSelection()
    {
        try
        {
            var implied = _document.Editor.SelectImplied();
            if (implied.Status != PromptStatus.OK || implied.Value == null)
            {
                return;
            }

            var frames = implied.Value.GetObjectIds().Where(IsRegisteredFrame).ToArray();
            _document.Editor.SetImpliedSelection(frames);
        }
        catch
        {
            // 不同 CAD 版本对 PICKFIRST 的事件时机略有差异；后续 SelectionAdded 仍会继续过滤。
        }
    }

    // 判断是否为删除类命令；命令名可能带 "."/"_"/"-" 等前缀（如 _ERASE），先去掉再比较
    private static bool IsEraseCommand(string? globalCommandName)
    {
        var name = (globalCommandName ?? "").Trim().TrimStart('.', '_', '-').ToUpperInvariant();
        return name == "ERASE" || name == "DELETE";
    }

    // 以下三个方法维护全局红框注册表；加锁保护，防止多文档/多窗口并发读写
    private static void RegisterFrame(ObjectId id)
    {
        lock (RegisteredFramesSync)
        {
            RegisteredFrameIds.Add(id);
        }
    }

    private static void UnregisterFrames(IEnumerable<ObjectId> ids)
    {
        lock (RegisteredFramesSync)
        {
            foreach (var id in ids)
            {
                RegisteredFrameIds.Remove(id);
            }
        }
    }

    private static bool IsRegisteredFrame(ObjectId id)
    {
        lock (RegisteredFramesSync)
        {
            return RegisteredFrameIds.Contains(id);
        }
    }

    // 窗体关闭时调用：退订全部 CAD 事件并清除残留红框，防止事件泄漏导致后续命令仍被过滤
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _document.CommandWillStart -= DocumentCommandWillStart;
        _document.CommandEnded -= DocumentCommandEnded;
        _document.CommandCancelled -= DocumentCommandCancelled;
        _document.CommandFailed -= DocumentCommandCancelled;
        _document.Editor.SelectionAdded -= EditorSelectionAdded;
        _document.Database.ObjectErased -= DatabaseObjectErased;
        Clear();
    }

    private static ObjectId EnsureLayer(Transaction tr, Database db)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (table.Has(LayerName))
        {
            var existing = (LayerTableRecord)tr.GetObject(table[LayerName], OpenMode.ForWrite);
            existing.IsOff = false;
            existing.IsFrozen = false;
            existing.IsLocked = false;
            existing.IsPlottable = false;
            existing.Color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            return existing.ObjectId;
        }

        table.UpgradeOpen();
        var record = new LayerTableRecord
        {
            Name = LayerName,
            Color = Color.FromColorIndex(ColorMethod.ByAci, 1),
            IsPlottable = false
        };
        var id = table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
        return id;
    }

    private static ObjectId EnsureTextStyle(Transaction tr, Database db)
    {
        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (table.Has(TextStyleName))
        {
            var existing = (TextStyleTableRecord)tr.GetObject(table[TextStyleName], OpenMode.ForWrite);
            existing.FileName = "simsun.ttc";
            existing.XScale = 1.0;
            existing.ObliquingAngle = 0;
            return existing.ObjectId;
        }

        table.UpgradeOpen();
        var record = new TextStyleTableRecord
        {
            Name = TextStyleName,
            FileName = "simsun.ttc",
            XScale = 1.0,
            ObliquingAngle = 0
        };
        var id = table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
        return id;
    }

    private ObjectId AddEntity(Transaction tr, BlockTableRecord owner, Entity entity)
    {
        var id = owner.AppendEntity(entity);
        tr.AddNewlyCreatedDBObject(entity, true);
        _entityIds.Add(id);
        return id;
    }

    public void SetHighlight(PlotJob? highlightJob)
    {
        if (ReferenceEquals(_highlightedJob, highlightJob))
        {
            return;
        }

        try
        {
            using var docLock = _document.LockDocument();
            using var tr = _document.Database.TransactionManager.StartTransaction();

            // DataGrid 换行时只恢复上一行、点亮当前行，避免删除/重画整批 CAD 临时实体导致 ZWCAD 卡顿。
            ApplyHighlight(tr, _highlightedJob, false);
            ApplyHighlight(tr, highlightJob, true);

            tr.Commit();
            _highlightedJob = highlightJob;
            UpdateScreenOnly();
        }
        catch
        {
            // 高亮切换失败不能影响批量打印主流程，下一次整批 Show 会重新同步状态。
        }
    }

    private void ApplyHighlight(Transaction tr, PlotJob? job, bool highlight)
    {
        if (job == null || !_entityGroups.TryGetValue(job, out var group))
        {
            return;
        }

        var color = GetOverlayColor(highlight);
        if (!group.FrameId.IsNull && !group.FrameId.IsErased
            && tr.GetObject(group.FrameId, OpenMode.ForWrite, false) is Polyline frame
            && !frame.IsErased)
        {
            frame.Color = color;
            frame.ConstantWidth = highlight ? group.HighlightFrameWidth : group.NormalFrameWidth;
        }

        foreach (var id in group.LabelIds)
        {
            if (id.IsNull || id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity label && !label.IsErased)
            {
                label.Color = color;
            }
        }
    }

    private static ObjectId GetJobOwnerId(Transaction tr, Database db, PlotJob job)
    {
        try
        {
            if (job.IsPaperSpace && !string.IsNullOrWhiteSpace(job.SpaceName))
            {
                var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                if (layouts.Contains(job.SpaceName))
                {
                    var layout = (Layout)tr.GetObject(layouts.GetAt(job.SpaceName), OpenMode.ForRead);
                    return layout.BlockTableRecordId;
                }
            }

            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return blockTable[BlockTableRecord.ModelSpace];
        }
        catch
        {
            return ObjectId.Null;
        }
    }

    private void AddBoldLabel(Transaction tr, BlockTableRecord owner, ObjectId layerId, ObjectId textStyleId, Color color, Point3d center, string text, double height, double rotation, List<ObjectId> labelIds)
    {
        var stroke = Math.Max(height * 0.035, 2d);
        // 文字始终只创建一套描边实体；换行高亮时只改颜色，避免为加粗效果重建文字。
        var cosR = Math.Cos(rotation);
        var sinR = Math.Sin(rotation);
        var offsets = new (double X, double Y)[]
        {
            (0d, 0d),
            (-stroke, 0d),
            (stroke, 0d),
            (0d, -stroke),
            (0d, stroke),
            (-stroke * 0.7, -stroke * 0.7),
            (-stroke * 0.7, stroke * 0.7),
            (stroke * 0.7, -stroke * 0.7),
            (stroke * 0.7, stroke * 0.7)
        };

        foreach (var (dx, dy) in offsets)
        {
            // 描边偏移按 UCS 角度旋转，和红框方向一致
            var rx = dx * cosR - dy * sinR;
            var ry = dx * sinR + dy * cosR;
            var point = new Point3d(center.X + rx, center.Y + ry, center.Z);
            var label = new DBText();
            label.SetDatabaseDefaults(_document.Database);
            label.TextString = text;
            label.Color = color;
            label.LayerId = layerId;
            label.TextStyleId = textStyleId;
            label.Height = height;
            label.Rotation = rotation;
            label.HorizontalMode = TextHorizontalMode.TextCenter;
            label.VerticalMode = TextVerticalMode.TextVerticalMid;
            label.Position = point;
            label.AlignmentPoint = point;
            var id = AddEntity(tr, owner, label);
            labelIds.Add(id);
            try
            {
                label.AdjustAlignment(_document.Database);
            }
            catch
            {
            }
        }
    }

    private void Regen()
    {
        try
        {
            _document.Editor.UpdateScreen();
            _document.Editor.Regen();
        }
        catch (CadRuntimeException)
        {
        }
    }

    private void UpdateScreenOnly()
    {
        try
        {
            // 换行高亮只是属性变化，UpdateScreen 足够；避免频繁 Regen 拖慢 ZWCAD。
            _document.Editor.UpdateScreen();
        }
        catch (CadRuntimeException)
        {
        }
    }

    private static Color GetOverlayColor(bool highlight)
    {
        return Color.FromColorIndex(ColorMethod.ByAci, highlight ? (short)2 : (short)1);
    }

    private static bool TryGetBounds(PlotJob job, Matrix3d dcsToWcs, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = Math.Min(job.MinX, job.MaxX);
        minY = Math.Min(job.MinY, job.MaxY);
        maxX = Math.Max(job.MinX, job.MaxX);
        maxY = Math.Max(job.MinY, job.MaxY);

        // IsDcsWindow：坐标是 DCS，只转中心点到 WCS，尺寸直接使用（DCS 尺寸 = 实际尺寸）
        // 不能四个角转 WCS 再取包围盒——那会二次放大
        if (job.IsDcsWindow)
        {
            var halfW = (maxX - minX) / 2d;
            var halfH = (maxY - minY) / 2d;
            var dcsCenter = new Point3d((minX + maxX) / 2d, (minY + maxY) / 2d, 0);
            var wcsCenter = dcsCenter.TransformBy(dcsToWcs);
            minX = wcsCenter.X - halfW;
            minY = wcsCenter.Y - halfH;
            maxX = wcsCenter.X + halfW;
            maxY = wcsCenter.Y + halfH;
        }

        return maxX - minX > 1e-6 && maxY - minY > 1e-6;
    }

    private static double GetTextHeight(double width, double height)
    {
        var minSide = Math.Min(width, height);
        var maxSide = Math.Max(width, height);
        var heightBySmallSide = minSide * 0.55;
        var heightByLongSide = maxSide * 0.16;
        return Math.Max(Math.Min(heightBySmallSide, heightByLongSide), minSide * 0.35);
    }

    private static double GetFrameWidth(double minSide)
    {
        return Math.Min(Math.Max(minSide * 0.08, 20d), minSide * 0.18);
    }

    private static LineWeight GetLineWeight(double minSide)
    {
        if (minSide >= 1000)
        {
            return LineWeight.LineWeight200;
        }

        if (minSide >= 500)
        {
            return LineWeight.LineWeight140;
        }

        if (minSide >= 200)
        {
            return LineWeight.LineWeight100;
        }

        return LineWeight.LineWeight050;
    }
}
