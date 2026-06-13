using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;

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
        var btr = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
        return btr.Name;
    }

    public static OwnerTextCache BuildOwnerTextCache(Transaction tr, BlockTableRecord owner)
    {
        var values = new List<TextCandidate>();
        foreach (ObjectId id in owner)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity
                && TryGetWorldText(entity, out var text, out var worldPoint))
            {
                AddText(values, text, worldPoint, TextSourcePriority.OwnerSpace);
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
                var local = attribute.Position.TransformBy(inverse);
                if (region.Contains(local.X, local.Y))
                {
                    AddText(values, attribute.TextString, local, TextSourcePriority.Attribute);
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
                values.Add(new TextCandidate(candidate.Text, local, TextSourcePriority.OwnerSpace));
            }
        }

        var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity entity && TryGetLocalText(entity, out var text, out var localPoint))
            {
                if (region.Contains(localPoint.X, localPoint.Y))
                {
                    AddText(values, text, localPoint, TextSourcePriority.BlockDefinition);
                }
            }
        }

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
            text = GetMTextPlainText(mText);
            point = mText.Location;
            return true;
        }

        return false;
    }

    private static bool TryGetWorldText(Entity entity, out string text, out Point3d point)
    {
        return TryGetLocalText(entity, out text, out point);
    }

    private static string GetMTextPlainText(MText mText)
    {
        // Some ZWCAD/AutoCAD APIs expose MText.Text as plain display text, while
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
            .Trim();

        value = Regex.Replace(value, @"\\[A-Za-z]+\d*(?:\.\d+)?;", "");
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
