using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

public static class CadTextExtractor
{
    public sealed class OwnerTextCache
    {
        internal OwnerTextCache(
            IReadOnlyList<TextCandidate> candidates,
            IReadOnlyList<OverlapBlockRef> overlapBlocks)
        {
            Candidates = candidates;
            OverlapBlocks = overlapBlocks;
        }

        internal IReadOnlyList<TextCandidate> Candidates { get; }

        /// <summary>布局内块参照及其包围盒，供字段提取时查找重叠图签，避免每个字段都遍历整张图。</summary>
        internal IReadOnlyList<OverlapBlockRef> OverlapBlocks { get; }
    }

    /// <summary>布局中一个块参照的包围盒快照，与建立缓存的事务共用。</summary>
    internal readonly struct OverlapBlockRef
    {
        public OverlapBlockRef(ObjectId id, Extents3d extents, BlockReference block)
        {
            Id = id;
            Extents = extents;
            Block = block;
        }

        public ObjectId Id { get; }
        public Extents3d Extents { get; }
        public BlockReference Block { get; }
    }

    /// <summary>
    /// 单个图框块参照的块内文字缓存，供多字段区域复用，避免重复递归块定义树。
    /// </summary>
    public sealed class BlockReferenceTextCache
    {
        internal BlockReferenceTextCache(
            IReadOnlyList<TextCandidate> attributeCandidates,
            IReadOnlyList<TextCandidate> definitionCandidates)
        {
            AttributeCandidates = attributeCandidates;
            DefinitionCandidates = definitionCandidates;
        }

        internal IReadOnlyList<TextCandidate> AttributeCandidates { get; }
        internal IReadOnlyList<TextCandidate> DefinitionCandidates { get; }
    }

    /// <summary>
    /// 预收集块参照属性与块定义内全部可见文字（块局部坐标）。
    /// 同一块定义树只遍历一次，多图框实例复用。
    /// </summary>
    public static BlockReferenceTextCache BuildBlockReferenceTextCache(Transaction tr, BlockReference blockRef)
    {
        var inverse = blockRef.BlockTransform.Inverse();
        var attributes = new List<TextCandidate>();
        foreach (ObjectId attributeId in blockRef.AttributeCollection)
        {
            if (!attributeId.IsValid || attributeId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute
                && IsEntityVisible(attribute)
                && TryGetText(attribute, out var text, out var worldPoint))
            {
                var local = worldPoint.TransformBy(inverse);
                AddText(attributes, text, local, TextSourcePriority.Attribute);
            }
        }

        var definitionId = blockRef.BlockTableRecord;
        var definitions = GetOrBuildDefinitionTextCache(tr, definitionId);
        return new BlockReferenceTextCache(attributes, definitions);
    }

    /// <summary>按块定义缓存可见文字，避免同一图框定义被每个实例、每个字段重复递归。</summary>
    private static IReadOnlyList<TextCandidate> GetOrBuildDefinitionTextCache(Transaction tr, ObjectId definitionId)
    {
        if (TitleBlockScanCaches.Active
            && TitleBlockScanCaches.DefinitionTexts.TryGetValue(definitionId, out var cached))
        {
            return cached;
        }

        var definitions = new List<TextCandidate>();
        if (definitionId.IsNull)
        {
            return definitions;
        }

        var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        CollectDefinitionText(
            tr,
            definition,
            Matrix3d.Identity,
            LocalRectangle.FromPoints(double.MinValue / 2, double.MinValue / 2, double.MaxValue / 2, double.MaxValue / 2),
            definitions,
            TextSourcePriority.BlockDefinition,
            new HashSet<ObjectId>(),
            0);

        if (TitleBlockScanCaches.Active)
        {
            TitleBlockScanCaches.DefinitionTexts[definitionId] = definitions;
        }

        return definitions;
    }

    public static string GetBlockName(BlockReference blockRef, Transaction tr)
    {
        var definitionId = blockRef.BlockTableRecord;
        try
        {
            if (blockRef.IsDynamicBlock && !blockRef.DynamicBlockTableRecord.IsNull)
            {
                definitionId = blockRef.DynamicBlockTableRecord;
            }
        }
        catch
        {
            // Older CAD versions may not expose dynamic-block metadata reliably.
        }

        if (TitleBlockScanCaches.Active
            && TitleBlockScanCaches.BlockNames.TryGetValue(definitionId, out var cachedName))
        {
            return cachedName;
        }

        var btr = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        var name = btr.Name;
        if (TitleBlockScanCaches.Active)
        {
            TitleBlockScanCaches.BlockNames[definitionId] = name;
        }

        return name;
    }

    /// <summary>
    /// 判断动态属性名是否为可见性参数。CAD 默认名为 Visibility/可见性，
    /// 用户改名后通常仍保留“可见”或 visibility 字样。
    /// </summary>
    /// <param name="propertyName">动态块属性名。</param>
    /// <returns>属性名指向可见性参数时返回 true。</returns>
    public static bool IsVisibilityPropertyName(string? propertyName)
    {
        var name = propertyName ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.IndexOf("可见", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("visibility", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// 读取动态块当前可见性状态名。只认属性名含“可见/visibility”的参数，
    /// 避免把查寻加长列表的当前档位误当成可见性身份。
    /// </summary>
    /// <param name="blockRef">待读取的块参照。</param>
    /// <param name="visibilityName">当前可见性状态名。</param>
    /// <returns>存在可读的可见性状态时返回 true。</returns>
    public static bool TryGetVisibilityStateName(BlockReference blockRef, out string visibilityName)
    {
        visibilityName = "";
        try
        {
            if (!blockRef.IsDynamicBlock)
            {
                return false;
            }

            foreach (DynamicBlockReferenceProperty property in blockRef.DynamicBlockReferencePropertyCollection)
            {
                if (!IsVisibilityPropertyName(property.PropertyName))
                {
                    continue;
                }

                var valueText = Convert.ToString(property.Value)?.Trim() ?? "";
                if (valueText.Length == 0)
                {
                    continue;
                }

                visibilityName = valueText;
                return true;
            }
        }
        catch
        {
            // 老版本宿主无法读取动态属性时按普通块处理。
        }

        return false;
    }

    /// <summary>
    /// 图框库身份名。普通块为块名；带可见性属性的动态块为“块名+当前可见性名”，
    /// 使同一动态块的不同可见性状态能分别录入和识别。
    /// </summary>
    /// <param name="blockRef">待识别的块参照。</param>
    /// <param name="tr">当前事务。</param>
    /// <returns>用于图框库匹配的身份名。</returns>
    public static string GetLibraryIdentityName(BlockReference blockRef, Transaction tr)
    {
        var blockName = GetBlockName(blockRef, tr);
        if (TryGetVisibilityStateName(blockRef, out var visibilityName))
        {
            return blockName + "+" + visibilityName;
        }

        return blockName;
    }

    /// <summary>
    /// 取块参照当前可见的第一层嵌套块名（旧版“外层+内层块名”身份的兼容读取）。
    /// 非动态块或无可见嵌套块时返回 false。
    /// </summary>
    public static bool TryGetVisibleNestedBlockName(Transaction tr, BlockReference blockRef, out string innerBlockName)
    {
        innerBlockName = "";
        try
        {
            if (!blockRef.IsDynamicBlock)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        var definitionId = blockRef.BlockTableRecord;
        if (definitionId.IsNull)
        {
            return false;
        }

        var bestName = "";
        var bestArea = 0d;
        var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference nested)
            {
                continue;
            }

            bool visible;
            try
            {
                visible = nested.Visible;
            }
            catch
            {
                visible = true;
            }

            if (!visible)
            {
                continue;
            }

            var nestedName = GetBlockName(nested, tr);
            if (string.IsNullOrWhiteSpace(nestedName))
            {
                continue;
            }

            // 可见嵌套块通常只有一个；有多个时与录入侧一致，取面积最大的。
            var area = Math.Abs(nested.BlockTransform[0, 0] * nested.BlockTransform[1, 1]);
            if (bestName.Length == 0 || area > bestArea)
            {
                bestArea = area;
                bestName = nestedName;
            }
        }

        innerBlockName = bestName;
        return bestName.Length > 0;
    }

    /// <summary>
    /// 判断图框库/任务中的块名是否与图纸中的块参照匹配。
    /// 优先按“块名+可见性名”身份匹配；否则比对外层块名；
    /// 旧库中的“外层+内层嵌套块名”仍按当前可见内层块兼容。
    /// </summary>
    /// <param name="storedName">图框库或打印任务中保存的块名。</param>
    /// <param name="blockRef">图纸中的块参照。</param>
    /// <param name="tr">当前事务。</param>
    /// <returns>身份一致时返回 true。</returns>
    public static bool BlockNameMatches(string storedName, BlockReference blockRef, Transaction tr)
    {
        if (string.IsNullOrWhiteSpace(storedName))
        {
            return false;
        }

        var identityName = GetLibraryIdentityName(blockRef, tr);
        if (string.Equals(storedName, identityName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var outerName = GetBlockName(blockRef, tr);
        if (string.Equals(storedName, outerName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 旧版“外层+内层”复合名以第一个 '+' 为界；外层名含 '+' 时无法良定义拆分。
        var plusIndex = storedName.IndexOf('+');
        if (plusIndex <= 0 || plusIndex >= storedName.Length - 1)
        {
            return false;
        }

        var outerPart = storedName.Substring(0, plusIndex);
        var innerPart = storedName.Substring(plusIndex + 1);
        return string.Equals(outerPart, outerName, StringComparison.OrdinalIgnoreCase)
            && TryGetVisibleNestedBlockName(tr, blockRef, out var innerName)
            && string.Equals(innerPart, innerName, StringComparison.OrdinalIgnoreCase);
    }

    public static OwnerTextCache BuildOwnerTextCache(Transaction tr, BlockTableRecord owner)
    {
        return BuildOwnerTextCache(tr, owner, null);
    }

    /// <summary>
    /// 建立布局文字缓存。传入 libraryBlockNames 时仅递归遍历图框库中注册的块，
    /// 避免对图纸中无关块（家具、符号等）的定义树做无意义遍历。
    /// </summary>
    public static OwnerTextCache BuildOwnerTextCache(Transaction tr, BlockTableRecord owner, HashSet<string>? libraryBlockNames)
    {
        var values = new List<TextCandidate>();
        var overlapBlocks = new List<OverlapBlockRef>();
        foreach (ObjectId id in owner)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            if (!IsEntityVisible(entity))
            {
                continue;
            }

            if (entity is BlockReference ownerBlock)
            {
                try
                {
                    overlapBlocks.Add(new OverlapBlockRef(id, ownerBlock.GeometricExtents, ownerBlock));
                }
                catch
                {
                }

                if (!IsBlockClipped(tr, ownerBlock))
                {
                    // 若提供了库名列表，只递归遍历匹配的块，大幅减少无意义遍历
                    if (libraryBlockNames == null || libraryBlockNames.Contains(GetBlockName(ownerBlock, tr)))
                    {
                        CollectOwnerBlockTextForCache(tr, ownerBlock, values);
                    }
                }
                continue;
            }

            if (TryGetOwnerSpaceText(entity, out var text, out var worldPoint))
            {
                var priority = entity is AttributeDefinition or AttributeReference
                    ? TextSourcePriority.Attribute
                    : TextSourcePriority.OwnerSpace;
                AddText(values, text, worldPoint, priority);
            }
        }

        return new OwnerTextCache(values, overlapBlocks);
    }

    private static bool TryGetOwnerSpaceText(Entity entity, out string text, out Point3d point)
    {
        if (entity is AttributeDefinition attributeDefinition)
        {
            text = string.IsNullOrWhiteSpace(attributeDefinition.Tag)
                ? GetAttributeText(attributeDefinition)
                : attributeDefinition.Tag;
            point = attributeDefinition.Position;
            return true;
        }

        return TryGetText(entity, out text, out point);
    }

    public static string ExtractRegionText(Transaction tr, BlockReference blockRef, BlockTableRecord owner, LocalRectangle region)
    {
        return ExtractRegionText(tr, blockRef, owner, region, null);
    }

    public static string ExtractRegionText(Transaction tr, BlockReference blockRef, BlockTableRecord owner, LocalRectangle region, OwnerTextCache? ownerTextCache)
    {
        return ExtractRegionText(tr, blockRef, owner, region, ownerTextCache, null);
    }

    /// <summary>
    /// 从字段区域提取文字。传入 <paramref name="blockTextCache"/> 时复用块内属性与块定义文字，
    /// 同一图框多字段扫描时避免重复递归块定义树。
    /// </summary>
    public static string ExtractRegionText(
        Transaction tr,
        BlockReference blockRef,
        BlockTableRecord owner,
        LocalRectangle region,
        OwnerTextCache? ownerTextCache,
        BlockReferenceTextCache? blockTextCache)
    {
        var values = new List<TextCandidate>();
        var inverse = blockRef.BlockTransform.Inverse();
        var blockLocal = Matrix3d.Identity;

        if (blockTextCache != null)
        {
            AppendCachedCandidates(values, blockTextCache.AttributeCandidates, blockLocal, region);
        }
        else
        {
            foreach (ObjectId attributeId in blockRef.AttributeCollection)
            {
                if (!attributeId.IsValid || attributeId.IsErased)
                {
                    continue;
                }

                if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute)
                {
                    // 动态块被可见性状态隐藏的属性仍挂在块参照上且保留旧位置，必须跳过。
                    if (!IsEntityVisible(attribute))
                    {
                        continue;
                    }

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
        }

        if (ownerTextCache == null)
        {
            ownerTextCache = BuildOwnerTextCache(tr, owner);
        }

        foreach (var candidate in ownerTextCache.Candidates)
        {
            var local = candidate.Point.TransformBy(inverse);
            if (IsCandidateInRegion(candidate, inverse, region, local))
            {
                values.Add(new TextCandidate(candidate.Text, local, candidate.Priority));
            }
        }

        if (blockTextCache != null)
        {
            AppendCachedCandidates(values, blockTextCache.DefinitionCandidates, blockLocal, region);
        }
        else
        {
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
        }

        // 图框块和图签块可能是分开的两个块引用，此时需要检查所有者空间中
        // 其他与字段区域重叠的块引用，确保其中的文字（如日期）不被遗漏。
        CollectOverlappingBlockText(tr, blockRef, owner, region, values, ownerTextCache);

        return SelectBestRegionText(values);
    }

    private static void AppendCachedCandidates(
        ICollection<TextCandidate> values,
        IReadOnlyList<TextCandidate> cached,
        Matrix3d blockLocal,
        LocalRectangle region)
    {
        foreach (var candidate in cached)
        {
            if (IsCandidateInRegion(candidate, blockLocal, region, candidate.Point))
            {
                values.Add(new TextCandidate(candidate.Text, candidate.Point, candidate.Priority));
            }
        }
    }

    private static string SelectBestRegionText(List<TextCandidate> values)
    {
        if (values.Count == 0)
        {
            return "";
        }

        var bestPriority = values.Min(x => x.Priority);
        return string.Join(" ", values
            .Where(x => x.Priority == bestPriority)
            .OrderByDescending(x => x.Point.Y)
            .ThenBy(x => x.Point.X)
            .Select(x => x.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList()).Trim();
    }

    private static void AddText(
        ICollection<TextCandidate> values,
        string? text,
        Point3d point,
        TextSourcePriority priority,
        Entity? sourceEntity = null)
    {
        text = CleanText(text);
        if (!string.IsNullOrWhiteSpace(text))
        {
            Point3d? alignmentPoint = null;
            LocalRectangle? worldBounds = null;
            if (sourceEntity != null)
            {
                if (TryGetAlignmentPoint(sourceEntity, out var alignment))
                {
                    alignmentPoint = alignment;
                }

                if (TryGetTransformedExtents(sourceEntity, Matrix3d.Identity, out var bounds))
                {
                    worldBounds = bounds;
                }
            }

            values.Add(new TextCandidate(text, point, priority, alignmentPoint, worldBounds));
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
                && IsEntityVisible(attribute)
                && TryGetText(attribute, out var attributeText, out var attributePoint))
            {
                AddText(values, attributeText, attributePoint, TextSourcePriority.Attribute, attribute);
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

    /// <summary>
    /// 实体是否可见。动态块被可见性状态隐藏的实体 Visible=false，识别时必须跳过，
    /// 否则隐藏状态的旧位置文字/属性会污染字段区域匹配。API 异常时按可见处理（宁可多扫不丢）。
    /// </summary>
    private static bool IsEntityVisible(Entity entity)
    {
        try
        {
            return entity.Visible;
        }
        catch
        {
            return true;
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

            // 动态块匿名定义包含所有可见性状态的实体，隐藏状态的旧位置文字/嵌套块一律跳过。
            if (!IsEntityVisible(entity))
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

            if (entity is not BlockReference nestedBlock)
            {
                continue;
            }

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

        visitedDefinitions.Remove(definition.ObjectId);
    }

    /// <summary>
    /// 检查所有者空间中与字段区域重叠的其他块引用（如图框块与图签块分离的情况），
    /// 将区域变换到对方块内坐标后提取属性与块定义文字。提取逻辑不变，
    /// 仅用布局级包围盒缓存避免每个字段都遍历整张图、重复求 GeometricExtents。
    /// </summary>
    private static void CollectOverlappingBlockText(
        Transaction tr,
        BlockReference self,
        BlockTableRecord owner,
        LocalRectangle region,
        ICollection<TextCandidate> values,
        OwnerTextCache? ownerTextCache)
    {
        var blockTransform = self.BlockTransform;
        var corners = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(blockTransform),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(blockTransform),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(blockTransform),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(blockTransform)
        };
        var worldMinX = corners.Min(p => p.X);
        var worldMaxX = corners.Max(p => p.X);
        var worldMinY = corners.Min(p => p.Y);
        var worldMaxY = corners.Max(p => p.Y);

        if (ownerTextCache != null)
        {
            foreach (var other in ownerTextCache.OverlapBlocks)
            {
                if (other.Id == self.ObjectId)
                {
                    continue;
                }

                var otherExtents = other.Extents;
                if (otherExtents.MaxPoint.X < worldMinX || otherExtents.MinPoint.X > worldMaxX
                    || otherExtents.MaxPoint.Y < worldMinY || otherExtents.MinPoint.Y > worldMaxY)
                {
                    continue;
                }

                AppendOverlappingBlockText(tr, other.Block, corners, values);
            }

            return;
        }

        foreach (ObjectId id in owner)
        {
            if (id == self.ObjectId)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference otherBlock)
            {
                continue;
            }

            if (!IsEntityVisible(otherBlock))
            {
                continue;
            }

            Extents3d otherExtents;
            try
            {
                otherExtents = otherBlock.GeometricExtents;
            }
            catch
            {
                continue;
            }

            if (otherExtents.MaxPoint.X < worldMinX || otherExtents.MinPoint.X > worldMaxX
                || otherExtents.MaxPoint.Y < worldMinY || otherExtents.MinPoint.Y > worldMaxY)
            {
                continue;
            }

            AppendOverlappingBlockText(tr, otherBlock, corners, values);
        }
    }

    /// <summary>
    /// 从重叠块参照提取属性与块定义文字。块定义树按定义 ID 缓存，多图框共享同一图签定义时只扫一次。
    /// </summary>
    private static void AppendOverlappingBlockText(
        Transaction tr,
        BlockReference otherBlock,
        Point3d[] worldCorners,
        ICollection<TextCandidate> values)
    {
        var otherInverse = otherBlock.BlockTransform.Inverse();
        var localPoints = worldCorners.Select(p => p.TransformBy(otherInverse)).ToArray();
        var otherLocalRegion = LocalRectangle.FromPoints(
            localPoints.Min(p => p.X), localPoints.Min(p => p.Y),
            localPoints.Max(p => p.X), localPoints.Max(p => p.Y));

        foreach (ObjectId attrId in otherBlock.AttributeCollection)
        {
            if (!attrId.IsValid || attrId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attrId, OpenMode.ForRead, false) is AttributeReference attr
                && IsEntityVisible(attr)
                && TryGetText(attr, out var attrText, out var attrWorldPoint))
            {
                var attrLocal = attrWorldPoint.TransformBy(otherInverse);
                if (otherLocalRegion.Contains(attrLocal.X, attrLocal.Y))
                {
                    AddText(values, attrText, attrLocal, TextSourcePriority.Attribute);
                }
            }
        }

        try
        {
            var cached = GetOrBuildDefinitionTextCache(tr, otherBlock.BlockTableRecord);
            AppendCachedCandidates(values, cached, Matrix3d.Identity, otherLocalRegion);
        }
        catch
        {
        }
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

        if (TryGetTransformedExtents(entity, entityToLocal, out var extents))
        {
            // 已取得 CAD 的真实文字包围盒时，它就是最终依据；真实包围盒不相交后不能再用估算框重试，
            // 否则旋转文字的无效对齐点可能生成巨大假包围盒，把相邻单元格标题误识别进来。
            return HasMeaningfulOverlap(region, extents);
        }

        return false;
    }

    private static bool IsCandidateInRegion(
        TextCandidate candidate,
        Matrix3d worldToLocal,
        LocalRectangle region,
        Point3d fallbackPoint)
    {
        if (region.Contains(fallbackPoint.X, fallbackPoint.Y))
        {
            return true;
        }

        if (candidate.AlignmentPoint.HasValue)
        {
            var localAlignment = candidate.AlignmentPoint.Value.TransformBy(worldToLocal);
            if (region.Contains(localAlignment.X, localAlignment.Y))
            {
                return true;
            }
        }

        if (candidate.WorldBounds != null)
        {
            var bounds = candidate.WorldBounds;
            var points = new[]
            {
                new Point3d(bounds.MinX, bounds.MinY, 0).TransformBy(worldToLocal),
                new Point3d(bounds.MinX, bounds.MaxY, 0).TransformBy(worldToLocal),
                new Point3d(bounds.MaxX, bounds.MinY, 0).TransformBy(worldToLocal),
                new Point3d(bounds.MaxX, bounds.MaxY, 0).TransformBy(worldToLocal)
            };
            var localBounds = LocalRectangle.FromPoints(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y));
            if (HasMeaningfulOverlap(region, localBounds))
            {
                return true;
            }
        }

        return false;
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
        if (entity is not DBText dbText)
        {
            return false;
        }

        try
        {
            // 左对齐、基线文字只使用 Position；这类文字在 AutoCAD/ZWCAD 中经常仍返回 (0,0,0)
            // 的占位 AlignmentPoint。把占位点当成真实对齐点会令估算包围盒跨越很远距离。
            if (dbText.HorizontalMode == TextHorizontalMode.TextLeft
                && dbText.VerticalMode == TextVerticalMode.TextBase)
            {
                return false;
            }

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
        public TextCandidate(
            string text,
            Point3d point,
            TextSourcePriority priority,
            Point3d? alignmentPoint = null,
            LocalRectangle? worldBounds = null)
        {
            Text = text;
            Point = point;
            Priority = priority;
            AlignmentPoint = alignmentPoint;
            WorldBounds = worldBounds;
        }

        public string Text { get; }
        public Point3d Point { get; }
        public TextSourcePriority Priority { get; }
        public Point3d? AlignmentPoint { get; }
        public LocalRectangle? WorldBounds { get; }
    }

    internal enum TextSourcePriority
    {
        Attribute = 0,
        OwnerSpace = 1,
        BlockDefinition = 2
    }

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
}

/// <summary>
/// 一次图框扫描内的复用缓存。不改变提取结果，只避免同一块名、同一块定义、同一外框被重复计算。
/// </summary>
internal static class TitleBlockScanCaches
{
    internal static bool Active;
    internal static readonly Dictionary<ObjectId, string> BlockNames = new();
    internal static readonly Dictionary<ObjectId, List<CadTextExtractor.TextCandidate>> DefinitionTexts = new();
    internal static readonly Dictionary<ObjectId, (bool Ok, LocalRectangle Frame, BlockFrameSource Source)> Frames = new();

    /// <summary>开始一次扫描，清空上一轮缓存。</summary>
    internal static void Begin()
    {
        Active = true;
        BlockNames.Clear();
        DefinitionTexts.Clear();
        Frames.Clear();
    }

    /// <summary>结束扫描并释放缓存，避免跨图纸残留。</summary>
    internal static void End()
    {
        Active = false;
        BlockNames.Clear();
        DefinitionTexts.Clear();
        Frames.Clear();
    }
}
