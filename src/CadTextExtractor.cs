using System;
using System.Collections.Generic;
using System.Linq;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;

namespace ZwcadBatchPlot;

public static class CadTextExtractor
{
    public static string GetBlockName(BlockReference blockRef, Transaction tr)
    {
        var btr = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
        return btr.Name;
    }

    public static string ExtractRegionText(Transaction tr, BlockReference blockRef, BlockTableRecord owner, LocalRectangle region)
    {
        var values = new List<string>();
        var inverse = blockRef.BlockTransform.Inverse();

        foreach (ObjectId attributeId in blockRef.AttributeCollection)
        {
            if (!attributeId.IsValid || attributeId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute)
            {
                var local = attribute.Position.TransformBy(inverse);
                if (region.Contains(local.X, local.Y))
                {
                    AddText(values, attribute.TextString);
                }
            }
        }

        var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity && TryGetLocalText(entity, out var text, out var localPoint))
            {
                if (region.Contains(localPoint.X, localPoint.Y))
                {
                    AddText(values, text);
                }
            }
        }

        foreach (ObjectId id in owner)
        {
            if (id == blockRef.ObjectId)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity && TryGetWorldText(entity, out var text, out var worldPoint))
            {
                var local = worldPoint.TransformBy(inverse);
                if (region.Contains(local.X, local.Y))
                {
                    AddText(values, text);
                }
            }
        }

        return string.Join(" ", values.Distinct()).Trim();
    }

    private static void AddText(ICollection<string> values, string? text)
    {
        text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            values.Add(text);
        }
    }

    private static bool TryGetLocalText(Entity entity, out string text, out Point3d point)
    {
        text = "";
        point = Point3d.Origin;

        if (entity is DBText dbText)
        {
            text = dbText.TextString;
            point = dbText.Position;
            return true;
        }

        if (entity is MText mText)
        {
            text = mText.Contents;
            point = mText.Location;
            return true;
        }

        return false;
    }

    private static bool TryGetWorldText(Entity entity, out string text, out Point3d point)
    {
        return TryGetLocalText(entity, out text, out point);
    }
}
