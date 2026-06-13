using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;

namespace ZwcadBatchPlot;

public sealed class TemporarySequenceOverlay : IDisposable
{
    private readonly List<Entity> _entities = new();
    private readonly IntegerCollection _viewportIds = new();

    public void Show(IReadOnlyList<PlotJob> jobs)
    {
        Clear();

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
            var padding = Math.Max(minSide * 0.012, 2d);
            var textHeight = GetTextHeight(width, height);
            var color = Color.FromColorIndex(ColorMethod.ByAci, 1);

            var frame = new Polyline(4)
            {
                Closed = true,
                Color = color,
                LineWeight = GetLineWeight(minSide),
                ConstantWidth = Math.Max(minSide * 0.004, 0.8d)
            };
            frame.AddVertexAt(0, new Point2d(minX - padding, minY - padding), 0, 0, 0);
            frame.AddVertexAt(1, new Point2d(maxX + padding, minY - padding), 0, 0, 0);
            frame.AddVertexAt(2, new Point2d(maxX + padding, maxY + padding), 0, 0, 0);
            frame.AddVertexAt(3, new Point2d(minX - padding, maxY + padding), 0, 0, 0);

            var label = new DBText
            {
                TextString = (i + 1).ToString(),
                Color = color,
                Height = textHeight,
                HorizontalMode = TextHorizontalMode.TextCenter,
                VerticalMode = TextVerticalMode.TextVerticalMid,
                Position = new Point3d((minX + maxX) / 2d, (minY + maxY) / 2d, 0),
                AlignmentPoint = new Point3d((minX + maxX) / 2d, (minY + maxY) / 2d, 0)
            };

            Add(frame);
            Add(label);
        }
    }

    public void Clear()
    {
        if (_entities.Count == 0)
        {
            return;
        }

        var manager = TransientManager.CurrentTransientManager;
        foreach (var entity in _entities)
        {
            try
            {
                manager.EraseTransient(entity, _viewportIds);
            }
            catch
            {
            }

            entity.Dispose();
        }

        _entities.Clear();
    }

    public void Dispose()
    {
        Clear();
    }

    private void Add(Entity entity)
    {
        TransientManager.CurrentTransientManager.AddTransient(
            entity,
            TransientDrawingMode.DirectShortTerm,
            128,
            _viewportIds);
        _entities.Add(entity);
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
        var heightBySmallSide = minSide * 0.16;
        var heightByLongSide = maxSide * 0.035;
        return Math.Max(Math.Min(heightBySmallSide, heightByLongSide), minSide * 0.08);
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
