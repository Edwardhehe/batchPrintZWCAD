using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

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
        var values = new List<TextCandidate>();
        var inverse = blockRef.BlockTransform.Inverse();

        foreach (ObjectId attributeId in blockRef.AttributeCollection)
        {
            if (!attributeId.IsValid || attributeId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute)
            {
                if (TryGetText(attribute, out var text, out var worldPoint))
                {
                    var local = worldPoint.TransformBy(inverse);
                    if (IsInRegion(attribute, inverse, region, local))
                    {
                        AddText(values, text, local, TextSourcePriority.Attribute);
                    }
                }
            }
        }

        foreach (ObjectId id in owner)
        {
            if (id == blockRef.ObjectId)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity && TryGetText(entity, out var text, out var worldPoint))
            {
                var local = worldPoint.TransformBy(inverse);
                if (IsInRegion(entity, inverse, region, local))
                {
                    AddText(values, text, local, TextSourcePriority.OwnerSpace);
                }
            }
        }

        var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
        CollectDefinitionText(tr, definition, Matrix3d.Identity, region, values);

        if (values.Count == 0)
        {
            return "";
        }

        // Attribute and owner-space text represent the current drawing instance.
        // Block definition text is only a fallback for title blocks made of static text.
        var bestPriority = values.Min(x => x.Priority);
        return string.Join(" ", values
            .Where(x => x.Priority == bestPriority)
            .OrderByDescending(x => x.Point.Y)
            .ThenBy(x => x.Point.X)
            .Select(x => x.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList()).Trim();
    }

    private static void AddText(ICollection<TextCandidate> values, string? text, Point3d point, TextSourcePriority priority)
    {
        text = CleanText(text);
        if (!string.IsNullOrWhiteSpace(text))
        {
            values.Add(new TextCandidate(text, point, priority));
        }
    }

    private static bool TryGetText(Entity entity, out string text, out Point3d point)
    {
        text = "";
        point = Point3d.Origin;

        if (entity is DBText dbText)
        {
            text = dbText.TextString;
            point = dbText.Position;
            return true;
        }

        if (entity is AttributeDefinition attributeDefinition)
        {
            text = attributeDefinition.TextString;
            point = attributeDefinition.Position;
            return true;
        }

        if (entity is AttributeReference attributeReference)
        {
            text = attributeReference.TextString;
            point = attributeReference.Position;
            return true;
        }

        if (entity is MText mText)
        {
            text = GetMTextPlainText(mText);
            point = mText.Location;
            return true;
        }

        return false;
    }

    private static void CollectDefinitionText(
        Transaction tr,
        BlockTableRecord definition,
        Matrix3d entityToRoot,
        LocalRectangle region,
        ICollection<TextCandidate> values)
    {
        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            if (TryGetText(entity, out var text, out var entityPoint))
            {
                var localPoint = entityPoint.TransformBy(entityToRoot);
                if (IsInRegion(entity, entityToRoot, region, localPoint))
                {
                    AddText(values, text, localPoint, TextSourcePriority.BlockDefinition);
                }
            }

            if (entity is BlockReference nestedBlock)
            {
                var nestedToRoot = nestedBlock.BlockTransform * entityToRoot;
                foreach (ObjectId attributeId in nestedBlock.AttributeCollection)
                {
                    if (!attributeId.IsValid || attributeId.IsErased)
                    {
                        continue;
                    }

                    if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute
                        && TryGetText(attribute, out var attributeText, out var attributePoint))
                    {
                        var localPoint = attributePoint.TransformBy(entityToRoot);
                        if (IsInRegion(attribute, entityToRoot, region, localPoint))
                        {
                            AddText(values, attributeText, localPoint, TextSourcePriority.BlockDefinition);
                        }
                    }
                }

                try
                {
                    var nestedDefinition = (BlockTableRecord)tr.GetObject(nestedBlock.BlockTableRecord, OpenMode.ForRead);
                    CollectDefinitionText(tr, nestedDefinition, nestedToRoot, region, values);
                }
                catch
                {
                }
            }
        }
    }

    private static bool IsInRegion(Entity entity, Matrix3d entityToLocal, LocalRectangle region, Point3d fallbackPoint)
    {
        if (region.Contains(fallbackPoint.X, fallbackPoint.Y))
        {
            return true;
        }

        if (TryGetTransformedExtents(entity, entityToLocal, out var extents))
        {
            return HasMeaningfulOverlap(region, extents);
        }

        return false;
    }

    private static bool TryGetTransformedExtents(Entity entity, Matrix3d transform, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        try
        {
            var extents = entity.GeometricExtents;
            var points = new[]
            {
                new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
                new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform),
                new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
                new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform)
            };

            rectangle = LocalRectangle.FromPoints(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasMeaningfulOverlap(LocalRectangle region, LocalRectangle textBounds)
    {
        var overlapWidth = Math.Max(0, Math.Min(region.MaxX, textBounds.MaxX) - Math.Max(region.MinX, textBounds.MinX));
        var overlapHeight = Math.Max(0, Math.Min(region.MaxY, textBounds.MaxY) - Math.Max(region.MinY, textBounds.MinY));
        var overlapArea = overlapWidth * overlapHeight;
        if (overlapArea <= 0)
        {
            return false;
        }

        var textArea = RectangleArea(textBounds);
        var regionArea = RectangleArea(region);
        if (textArea <= 0 || regionArea <= 0)
        {
            return false;
        }

        var textCenterX = (textBounds.MinX + textBounds.MaxX) / 2d;
        var textCenterY = (textBounds.MinY + textBounds.MaxY) / 2d;
        if (region.Contains(textCenterX, textCenterY))
        {
            return true;
        }

        var overlapTextRatio = overlapArea / textArea;
        return overlapTextRatio >= 0.55;
    }

    private static double RectangleArea(LocalRectangle rectangle)
    {
        return Math.Max(0, rectangle.MaxX - rectangle.MinX)
            * Math.Max(0, rectangle.MaxY - rectangle.MinY);
    }

    private static string GetMTextPlainText(MText mText)
    {
        // Some CAD APIs expose MText.Text as plain display text, while
        // Contents keeps inline format codes such as {\W0.75;...}.
        var textProperty = typeof(MText).GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
        if (textProperty?.GetValue(mText, null) is string plain && !string.IsNullOrWhiteSpace(plain))
        {
            return plain;
        }

        return mText.Contents;
    }

    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var value = (text ?? "").Replace("\\P", " ")
            .Replace("\\p", " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace("%%U", "")
            .Replace("%%u", "")
            .Replace("%%O", "")
            .Replace("%%o", "")
            .Replace("%%C", "Φ")
            .Replace("%%c", "Φ")
            .Replace("%%D", "°")
            .Replace("%%d", "°")
            .Replace("%%P", "±")
            .Replace("%%p", "±")
            .Trim();

        value = Regex.Replace(value, @"\\[A-Za-z]+[^;{}\\]*;", "");
        value = Regex.Replace(value, @"\\[A-Za-z]+", "");
        value = value.Replace("{", "").Replace("}", "");
        value = Regex.Replace(value, @"\s+", " ").Trim();

        return value;
    }

    private sealed class TextCandidate
    {
        public TextCandidate(string text, Point3d point, TextSourcePriority priority)
        {
            Text = text;
            Point = point;
            Priority = priority;
        }

        public string Text { get; }
        public Point3d Point { get; }
        public TextSourcePriority Priority { get; }
    }

    private enum TextSourcePriority
    {
        Attribute = 0,
        OwnerSpace = 1,
        BlockDefinition = 2
    }
}
