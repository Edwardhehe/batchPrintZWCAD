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
    private readonly Document _document;
    private readonly List<ObjectId> _entityIds = new();

    public TemporarySequenceOverlay(Document document)
    {
        _document = document;
    }

    public void Show(IReadOnlyList<PlotJob> jobs, int highlightIndex = -1)
    {
        Clear();

        using var docLock = _document.LockDocument();
        using var tr = _document.Database.TransactionManager.StartTransaction();
        var db = _document.Database;
        var layerId = EnsureLayer(tr, db);

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
            // 高亮行：黄色 (ACI 2)、加粗边框、加粗数字；普通行：红色 (ACI 1)
            var isHighlight = i == highlightIndex;
            var color = isHighlight
                ? Color.FromColorIndex(ColorMethod.ByAci, 2)
                : Color.FromColorIndex(ColorMethod.ByAci, 1);
            var frameWidth = isHighlight
                ? GetFrameWidth(minSide) * 2.5
                : GetFrameWidth(minSide);
            var lineWeight = isHighlight
                ? BumpLineWeight(GetLineWeight(minSide))
                : GetLineWeight(minSide);

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
            AddEntity(tr, owner, frame);

            var center = new Point3d((minX + maxX) / 2d, (minY + maxY) / 2d, 0);
            AddBoldLabel(tr, owner, layerId, color, center, (i + 1).ToString(), textHeight, ucsAngle, isHighlight);
        }

        tr.Commit();
        Regen();
    }

    public void Clear()
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
            Regen();
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

    private void AddEntity(Transaction tr, BlockTableRecord owner, Entity entity)
    {
        var id = owner.AppendEntity(entity);
        tr.AddNewlyCreatedDBObject(entity, true);
        _entityIds.Add(id);
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

    private void AddBoldLabel(Transaction tr, BlockTableRecord owner, ObjectId layerId, Color color, Point3d center, string text, double height, double rotation, bool highlight = false)
    {
        var stroke = Math.Max(height * 0.035, 2d);
        // 高亮时描边加粗：偏移量翻倍 + 额外一层中间描边
        if (highlight) stroke *= 2;
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
            var label = new DBText
            {
                TextString = text,
                Color = color,
                LayerId = layerId,
                Height = height,
                Rotation = rotation,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                Position = point,
                AlignmentPoint = point
            };
            AddEntity(tr, owner, label);
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

    private static LineWeight BumpLineWeight(LineWeight w)
    {
        return w switch
        {
            LineWeight.LineWeight000 => LineWeight.LineWeight025,
            LineWeight.LineWeight025 => LineWeight.LineWeight050,
            LineWeight.LineWeight050 => LineWeight.LineWeight080,
            LineWeight.LineWeight080 => LineWeight.LineWeight100,
            LineWeight.LineWeight100 => LineWeight.LineWeight140,
            LineWeight.LineWeight140 => LineWeight.LineWeight200,
            _ => LineWeight.LineWeight211
        };
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
