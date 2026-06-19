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
    public sealed class OwnerTextCache
    {
        internal OwnerTextCache(IReadOnlyList<TextCandidate> candidates)
        {
            Candidates = candidates;
        }

        internal IReadOnlyList<TextCandidate> Candidates { get; }
    }

    public static string GetBlockName(BlockReference blockRef, Transaction tr)
    {
        var definitionId = blockRef.BlockTableRecord;
        if (blockRef.IsDynamicBlock && !blockRef.DynamicBlockTableRecord.IsNull)
        {
            definitionId = blockRef.DynamicBlockTableRecord;
        }

        var btr = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        return btr.Name;
    }

    public static OwnerTextCache BuildOwnerTextCache(Transaction tr, BlockTableRecord owner)
    {
        var values = new List<TextCandidate>();
        foreach (ObjectId id in owner)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            if (entity is BlockReference ownerBlock)
            {
                CollectOwnerBlockTextForCache(tr, ownerBlock, values);
                continue;
            }

            if (TryGetText(entity, out var text, out var worldPoint))
            {
                var priority = entity is AttributeDefinition or AttributeReference
                    ? TextSourcePriority.Attribute
                    : TextSourcePriority.OwnerSpace;
                AddText(values, text, worldPoint, priority);
            }
        }

        return new OwnerTextCache(values);
    }

    public static string ExtractRegionText(Transaction tr, BlockReference blockRef, BlockTableRecord owner, LocalRectangle region)
    {
        return ExtractRegionText(tr, blockRef, owner, region, null);
    }

    public static string ExtractRegionText(Transaction tr, BlockReference blockRef, BlockTableRecord owner, LocalRectangle region, OwnerTextCache? ownerTextCache)
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

        if (ownerTextCache == null)
        {
            ownerTextCache = BuildOwnerTextCache(tr, owner);
        }

        foreach (var candidate in ownerTextCache.Candidates)
        {
            var local = candidate.Point.TransformBy(inverse);
            if (region.Contains(local.X, local.Y))
            {
                values.Add(new TextCandidate(candidate.Text, local, candidate.Priority));
            }
        }

        var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
        CollectDefinitionText(
            tr,
            definition,
            Matrix3d.Identity,
            region,
            values,
            TextSourcePriority.BlockDefinition,
            new HashSet<ObjectId>(),
            0);

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

    private static void CollectOwnerBlockText(
        Transaction tr,
        BlockReference ownerBlock,
        Matrix3d ownerToFrameLocal,
        LocalRectangle region,
        ICollection<TextCandidate> values)
    {
        foreach (ObjectId attributeId in ownerBlock.AttributeCollection)
        {
            if (!attributeId.IsValid || attributeId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute
                && TryGetText(attribute, out var attributeText, out var attributePoint))
            {
                var localPoint = attributePoint.TransformBy(ownerToFrameLocal);
                if (IsInRegion(attribute, ownerToFrameLocal, region, localPoint))
                {
                    AddText(values, attributeText, localPoint, TextSourcePriority.OwnerSpace);
                }
            }
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(ownerBlock.BlockTableRecord, OpenMode.ForRead);
            var blockToFrameLocal = ownerBlock.BlockTransform * ownerToFrameLocal;
            CollectDefinitionText(
                tr,
                definition,
                blockToFrameLocal,
                region,
                values,
                TextSourcePriority.OwnerSpace,
                new HashSet<ObjectId>(),
                0);
        }
        catch
        {
        }

        if (TryGetOwnerBlockName(ownerBlock, tr, out var blockName)
            && TryGetTransformedExtents(ownerBlock, ownerToFrameLocal, out var bounds)
            && HasMeaningfulOverlap(region, bounds))
        {
            var point = ownerBlock.Position.TransformBy(ownerToFrameLocal);
            AddText(values, blockName, point, TextSourcePriority.OwnerSpace);
        }
    }

    private static void CollectOwnerBlockTextForCache(
        Transaction tr,
        BlockReference ownerBlock,
        ICollection<TextCandidate> values)
    {
        foreach (ObjectId attributeId in ownerBlock.AttributeCollection)
        {
            if (!attributeId.IsValid || attributeId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute
                && TryGetText(attribute, out var attributeText, out var attributePoint))
            {
                AddText(values, attributeText, attributePoint, TextSourcePriority.Attribute);
            }
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(ownerBlock.BlockTableRecord, OpenMode.ForRead);
            CollectDefinitionText(
                tr,
                definition,
                ownerBlock.BlockTransform,
                LocalRectangle.FromPoints(double.MinValue / 2, double.MinValue / 2, double.MaxValue / 2, double.MaxValue / 2),
                values,
                TextSourcePriority.OwnerSpace,
                new HashSet<ObjectId>(),
                0);
        }
        catch
        {
        }

        if (TryGetOwnerBlockName(ownerBlock, tr, out var blockName))
        {
            AddText(values, blockName, ownerBlock.Position, TextSourcePriority.OwnerSpace);
        }
    }

    private static bool TryGetOwnerBlockName(BlockReference blockRef, Transaction tr, out string blockName)
    {
        blockName = GetBlockName(blockRef, tr);
        if (string.IsNullOrWhiteSpace(blockName))
        {
            return false;
        }

        var trimmed = blockName.Trim();
        if (trimmed.StartsWith("*", StringComparison.Ordinal)
            || trimmed.StartsWith("A$C", StringComparison.OrdinalIgnoreCase)
            || trimmed.IndexOf("$0$", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        return true;
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
            text = GetAttributeText(attributeDefinition);
            point = attributeDefinition.Position;
            return true;
        }

        if (entity is AttributeReference attributeReference)
        {
            text = GetAttributeText(attributeReference);
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
        ICollection<TextCandidate> values,
        TextSourcePriority priority,
        ISet<ObjectId> visitedDefinitions,
        int depth)
    {
        if (depth > 12 || !visitedDefinitions.Add(definition.ObjectId))
        {
            return;
        }

        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            var isNonConstantAttributeDefinition = entity is AttributeDefinition attributeDefinition
                && !attributeDefinition.Constant;
            if (!isNonConstantAttributeDefinition
                && TryGetText(entity, out var text, out var entityPoint))
            {
                var localPoint = entityPoint.TransformBy(entityToRoot);
                if (IsInRegion(entity, entityToRoot, region, localPoint))
                {
                    AddText(values, text, localPoint, priority);
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
                            AddText(values, attributeText, localPoint, TextSourcePriority.Attribute);
                        }
                    }
                }

                try
                {
                    var nestedDefinition = (BlockTableRecord)tr.GetObject(nestedBlock.BlockTableRecord, OpenMode.ForRead);
                    CollectDefinitionText(tr, nestedDefinition, nestedToRoot, region, values, priority, visitedDefinitions, depth + 1);
                }
                catch
                {
                }
            }
        }

        visitedDefinitions.Remove(definition.ObjectId);
    }

    private static bool IsInRegion(Entity entity, Matrix3d entityToLocal, LocalRectangle region, Point3d fallbackPoint)
    {
        if (region.Contains(fallbackPoint.X, fallbackPoint.Y))
        {
            return true;
        }

        if (TryGetAlignmentPoint(entity, out var alignmentPoint))
        {
            var localAlignment = alignmentPoint.TransformBy(entityToLocal);
            if (region.Contains(localAlignment.X, localAlignment.Y))
            {
                return true;
            }
        }

        if (TryGetTransformedExtents(entity, entityToLocal, out var extents)
            && HasMeaningfulOverlap(region, extents))
        {
            return true;
        }

        return entity is DBText dbText
            && TryGetEstimatedTextExtents(dbText, entityToLocal, out var estimated)
            && HasMeaningfulOverlap(region, estimated);
    }

    private static string GetAttributeText(Entity attribute)
    {
        try
        {
            var isMTextProperty = attribute.GetType().GetProperty("IsMTextAttribute", BindingFlags.Instance | BindingFlags.Public);
            if (isMTextProperty?.GetValue(attribute, null) is bool isMText && isMText)
            {
                var mTextProperty = attribute.GetType().GetProperty("MTextAttribute", BindingFlags.Instance | BindingFlags.Public);
                if (mTextProperty?.GetValue(attribute, null) is MText mText)
                {
                    var value = GetMTextPlainText(mText);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }
        }
        catch
        {
        }

        return attribute switch
        {
            AttributeReference reference => reference.TextString,
            AttributeDefinition definition => definition.TextString,
            _ => ""
        };
    }

    private static bool TryGetAlignmentPoint(Entity entity, out Point3d point)
    {
        point = Point3d.Origin;
        if (entity is not DBText)
        {
            return false;
        }

        try
        {
            var property = entity.GetType().GetProperty("AlignmentPoint", BindingFlags.Instance | BindingFlags.Public)
                ?? typeof(DBText).GetProperty("AlignmentPoint", BindingFlags.Instance | BindingFlags.Public);
            if (property?.GetValue(entity, null) is Point3d alignment
                && IsFinite(alignment))
            {
                point = alignment;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsFinite(Point3d point)
    {
        return !double.IsNaN(point.X) && !double.IsInfinity(point.X)
            && !double.IsNaN(point.Y) && !double.IsInfinity(point.Y)
            && !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);
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
        }

        return entity is DBText dbText
            && TryGetEstimatedTextExtents(dbText, transform, out rectangle);
    }

    private static bool TryGetEstimatedTextExtents(DBText text, Matrix3d transform, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        try
        {
            var height = Math.Abs(text.Height);
            if (height <= 1e-9 || !IsFinite(text.Position))
            {
                return false;
            }

            var content = CleanText(text.TextString);
            var characterCount = Math.Max(1, content.Length);
            var widthFactor = Math.Abs(text.WidthFactor);
            if (widthFactor <= 1e-9)
            {
                widthFactor = 1d;
            }

            // AutoCAD can throw eInvalidExtents for otherwise visible DBText and
            // AttributeReference objects. Estimate a conservative text box from
            // their insertion/alignment points so a visually overlapping region
            // still recognizes the text.
            var halfWidth = Math.Max(height * 0.6d, characterCount * height * widthFactor * 0.45d);
            var halfHeight = height * 0.9d;
            var center = text.Position;
            if (TryGetAlignmentPoint(text, out var alignment)
                && alignment.DistanceTo(text.Position) > 1e-9)
            {
                center = new Point3d(
                    (text.Position.X + alignment.X) / 2d,
                    (text.Position.Y + alignment.Y) / 2d,
                    (text.Position.Z + alignment.Z) / 2d);
                halfWidth = Math.Max(halfWidth, text.Position.DistanceTo(alignment) / 2d + height * 0.25d);
            }

            var cos = Math.Cos(text.Rotation);
            var sin = Math.Sin(text.Rotation);
            var xAxis = new Vector3d(cos, sin, 0);
            var yAxis = new Vector3d(-sin, cos, 0);
            var points = new[]
            {
                (center - xAxis * halfWidth - yAxis * halfHeight).TransformBy(transform),
                (center - xAxis * halfWidth + yAxis * halfHeight).TransformBy(transform),
                (center + xAxis * halfWidth - yAxis * halfHeight).TransformBy(transform),
                (center + xAxis * halfWidth + yAxis * halfHeight).TransformBy(transform)
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

    internal sealed class TextCandidate
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

    internal enum TextSourcePriority
    {
        Attribute = 0,
        OwnerSpace = 1,
        BlockDefinition = 2
    }
}
