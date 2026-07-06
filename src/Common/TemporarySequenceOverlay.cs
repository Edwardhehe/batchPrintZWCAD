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

public sealed class TemporarySequenceOverlay
{
    private const string LayerName = "ZBP_TEMP_SEQUENCE_OVERLAY";
    private const string TextStyleName = "ZBP_TEMP_SEQUENCE_TEXT";
    private readonly Document _document;
    private readonly List<ObjectId> _entityIds = new();
    private readonly Dictionary<PlotJob, OverlayEntityGroup> _entityGroups = new();
    private PlotJob? _highlightedJob;

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
    }

    public void Show(IReadOnlyList<PlotJob> jobs, int highlightIndex)
    {
        // 兼容矩形批量窗口的旧调用：先把过滤后下标转换成具体 Job，核心逻辑统一按对象引用高亮。
        var highlightJob = highlightIndex >= 0 && highlightIndex < jobs.Count ? jobs[highlightIndex] : null;
        Show(jobs, highlightJob);
    }

    public void Show(IReadOnlyList<PlotJob> jobs, PlotJob? highlightJob = null)
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

        foreach (var job in jobs)
        {
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

            var center = new Point3d((minX + maxX) / 2d, (minY + maxY) / 2d, 0);
            AddBoldLabel(tr, owner, layerId, textStyleId, color, center, job.DrawingNumber, textHeight, ucsAngle, group.LabelIds);
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
            _entityIds.Clear();
            _entityGroups.Clear();
            _highlightedJob = null;
            if (repaint)
            {
                Regen();
            }
        }
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
