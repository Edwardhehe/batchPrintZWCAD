using System;
using System.Collections.Generic;
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

    public void Show(IReadOnlyList<PlotJob> jobs)
    {
        Clear();

        using var docLock = _document.LockDocument();
        using var tr = _document.Database.TransactionManager.StartTransaction();
        var db = _document.Database;
        var layerId = EnsureLayer(tr, db);

        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            if (!TryGetBounds(job, out var minX, out var minY, out var maxX, out var maxY))
            {
                continue;
            }

            var width = maxX - minX;
            var height = maxY - minY;
            var minSide = Math.Min(width, height);
            var padding = Math.Max(minSide * 0.035, 10d);
            var textHeight = GetTextHeight(width, height);
            var color = Color.FromColorIndex(ColorMethod.ByAci, 1);
            var frameWidth = GetFrameWidth(minSide);

            var ownerId = GetJobOwnerId(tr, db, job);
            if (ownerId.IsNull)
            {
                continue;
            }

            var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForWrite);
            var frame = new Polyline(4)
            {
                Closed = true,
                Color = color,
                LayerId = layerId,
                LineWeight = GetLineWeight(minSide),
                ConstantWidth = frameWidth
            };
            frame.AddVertexAt(0, new Point2d(minX - padding, minY - padding), 0, 0, 0);
            frame.AddVertexAt(1, new Point2d(maxX + padding, minY - padding), 0, 0, 0);
            frame.AddVertexAt(2, new Point2d(maxX + padding, maxY + padding), 0, 0, 0);
            frame.AddVertexAt(3, new Point2d(minX - padding, maxY + padding), 0, 0, 0);
            AddEntity(tr, owner, frame);

            var center = new Point3d((minX + maxX) / 2d, (minY + maxY) / 2d, 0);
            AddBoldLabel(tr, owner, layerId, color, center, (i + 1).ToString(), textHeight);
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

    private void AddBoldLabel(Transaction tr, BlockTableRecord owner, ObjectId layerId, Color color, Point3d center, string text, double height)
    {
        var stroke = Math.Max(height * 0.035, 2d);
        var offsets = new[]
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
            var point = new Point3d(center.X + dx, center.Y + dy, center.Z);
            var label = new DBText
            {
                TextString = text,
                Color = color,
                LayerId = layerId,
                Height = height,
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

    private static bool TryGetBounds(PlotJob job, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = Math.Min(job.MinX, job.MaxX);
        minY = Math.Min(job.MinY, job.MaxY);
        maxX = Math.Max(job.MinX, job.MaxX);
        maxY = Math.Max(job.MinY, job.MaxY);
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
