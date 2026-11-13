using System;
using System.Collections.Generic;
using System.Text;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>图签候选文字：世界坐标 + 内容 + 字高（0 表示未知）。</summary>
internal sealed class ThreePointTextSample
{
    public string Text { get; set; } = "";
    public Point3d Point { get; set; }
    public double Height { get; set; }

    /// <summary>
    /// 文字的另一个候选位置。<c>DBText.Position</c> 在非左下对齐时只是残留值，
    /// 真正的显示位置在 <c>AlignmentPoint</c>；图签里居中对齐很常见，
    /// 两边都记下来，判区域时任意一个落进去就算命中。
    /// </summary>
    public Point3d? AltPoint { get; set; }

    /// <summary>
    /// 是否可作锚点：单行文字（DBText / 属性 / 天正单行）与多行文字（MText / 天正多行）均可。
    /// <para>
    /// 多行文字的 Location（插入点）随 Attachment 不同可能落在左上/中心等位置，但入库与识别
    /// 都取同一个实体的同一个 Location，三点之间的相对关系稳定，仿射的平移分量会吸收这个偏移；
    /// \P 换行在 StripMText 里压成空格，锚点文字内容单行化，两边匹配一致。
    /// </para>
    /// </summary>
    public bool CanAnchor { get; set; }

    /// <summary>
    /// 文字几何范围上的代表点（WCS）：中心、上下边中点、左右边中点。
    /// <para>
    /// 大字号的图名经常「插入点压在字段格线外一点点，但文字本体明显在格子里」：
    /// 实测 v1.15.8.8 用户图纸里「防爆波活门门框墙配筋表」字高 500（图签其它字 150），
    /// 左下插入点距图名格子边界 9.59 单位，而容差被压到 2.5，于是图名一直空着。
    /// 这些点只用于「字段空了之后的就近补位」，不参与第一轮严格归属，
    /// 避免同一段文字同时落进相邻格子时被判给错误的字段。
    /// </para>
    /// </summary>
    public Point3d[]? ExtraPoints { get; set; }
}

/// <summary>
/// 收集指定空间内的全部文字，含块参照内部文字（递归展开块定义）与属性。
/// 三点图签允许锚点位于块内，因此这里不能只扫顶层实体。
/// </summary>
internal static class ThreePointTextCollector
{
    private const int MaxDepth = 8;

    public static List<ThreePointTextSample> CollectCurrentSpace(Document document, Transaction tr)
    {
        return CollectCurrentSpace(document.Database, tr);
    }

    /// <summary>
    /// 按数据库收当前空间文字。多文档批打时其它 DWG 是侧载打开的（没有 Document），
    /// 三分识别要按文件各收一份文字，所以这里以 Database 为入口。
    /// </summary>
    public static List<ThreePointTextSample> CollectCurrentSpace(Database database, Transaction tr)
    {
        var result = new List<ThreePointTextSample>();
        var spaceId = database.CurrentSpaceId;
        // 侧载数据库的 CurrentSpaceId 未必可用，退回模型空间，避免整张图判空。
        if (spaceId.IsNull || spaceId.IsErased)
        {
            var table = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
            spaceId = table[BlockTableRecord.ModelSpace];
        }

        var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
        Collect(tr, space, Matrix3d.Identity, 0, result);
        return result;
    }

    /// <summary>
    /// 按布局名收集该空间内的文字。名字为空或找不到布局时退回当前空间。
    /// 「直接识别清单里已有的图纸」时清单可能跨模型空间和多个布局，只收当前空间会让整批图框匹配不上。
    /// </summary>
    public static List<ThreePointTextSample> CollectSpace(Document document, Transaction tr, string? spaceName)
    {
        return CollectSpace(document.Database, tr, spaceName);
    }

    /// <summary>同上，按数据库收：多文档批打时每个 DWG 各自一份文字池。</summary>
    public static List<ThreePointTextSample> CollectSpace(Database database, Transaction tr, string? spaceName)
    {
        if (string.IsNullOrWhiteSpace(spaceName))
        {
            return CollectCurrentSpace(database, tr);
        }

        var result = new List<ThreePointTextSample>();
        var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId recordId in blockTable)
        {
            BlockTableRecord? record;
            try
            {
                record = tr.GetObject(recordId, OpenMode.ForRead, false) as BlockTableRecord;
            }
            catch
            {
                continue;
            }

            if (record == null || !record.IsLayout || record.LayoutId.IsNull)
            {
                continue;
            }

            Layout? layout;
            try
            {
                layout = tr.GetObject(record.LayoutId, OpenMode.ForRead, false) as Layout;
            }
            catch
            {
                continue;
            }

            if (layout == null
                || !string.Equals(layout.LayoutName, spaceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Collect(tr, record, Matrix3d.Identity, 0, result);
            return result;
        }

        return CollectCurrentSpace(database, tr);
    }

    private static void Collect(
        Transaction tr,
        BlockTableRecord record,
        Matrix3d transform,
        int depth,
        List<ThreePointTextSample> output)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        foreach (ObjectId id in record)
        {
            Entity? entity = null;
            try
            {
                entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
            }
            catch
            {
                continue;
            }

            if (entity == null || !IsVisible(entity))
            {
                continue;
            }

            switch (entity)
            {
                // AttributeReference 继承自 DBText，必须排在 DBText 之前，否则永远匹配不到属性。
                case AttributeReference attribute:
                    AddDbTextSample(output, attribute.TextString, attribute, transform, Math.Abs(attribute.Height) * ScaleOf(transform));
                    break;

                case DBText text:
                    AddDbTextSample(output, text.TextString, text, transform, Math.Abs(text.Height) * ScaleOf(transform));
                    break;

                case MText mText:
                    // 多行文字也可作三点锚点：入库与识别都取同一实体的同一 Location，
                    // 三点相对关系稳定；\P 换行由 StripMText 压成空格，两边文字匹配一致。
                    // Location 随 Attachment 可能偏离文字本体，额外记下几何代表点，
                    // 让用户在文字上任意位置点取都能命中（见 FindNearestAnchorText）。
                    AddSample(
                        output,
                        StripMText(mText.Contents),
                        mText.Location.TransformBy(transform),
                        Math.Abs(mText.TextHeight) * ScaleOf(transform),
                        true,
                        null,
                        RepresentativePoints(mText, transform));
                    break;

                case BlockReference blockRef:
                    CollectAttributes(tr, blockRef, transform, output);
                    CollectBlockDefinition(tr, blockRef, transform, depth, output);
                    break;

                default:
                    // 天正单行/多行不是 DBText/MText，走代理实体的组码读取。
                    if (IsProxyLike(entity)
                        && TianzhengTextReader.TryGetText(entity, out var tianzhengText, out var tianzhengPoint))
                    {
                        var height = 0.0;
                        try
                        {
                            height = Math.Abs(entity.GeometricExtents.MaxPoint.Y - entity.GeometricExtents.MinPoint.Y) * ScaleOf(transform);
                        }
                        catch
                        {
                        }

                        AddSample(
                            output,
                            tianzhengText,
                            tianzhengPoint.TransformBy(transform),
                            height,
                            true,
                            null,
                            RepresentativePoints(entity, transform));
                    }

                    break;
            }
        }
    }

    private static void CollectAttributes(
        Transaction tr,
        BlockReference blockRef,
        Matrix3d transform,
        List<ThreePointTextSample> output)
    {
        if (blockRef.AttributeCollection == null)
        {
            return;
        }

        foreach (ObjectId attributeId in blockRef.AttributeCollection)
        {
            try
            {
                if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute
                    && IsVisible(attribute))
                {
                    // 块属性同样继承 DBText：非左下对齐时 Position 只是残留值，
                    // 显示位置在 AlignmentPoint。图签大多用居中对齐的属性，
                    // 只记 Position 会让字段区域判空（区域位置看着对，却一个字都取不到）。
                    AddDbTextSample(output, attribute.TextString, attribute, transform, Math.Abs(attribute.Height) * ScaleOf(transform));
                }
            }
            catch
            {
            }
        }
    }

    private static void CollectBlockDefinition(
        Transaction tr,
        BlockReference blockRef,
        Matrix3d transform,
        int depth,
        List<ThreePointTextSample> output)
    {
        BlockTableRecord? definition = null;
        try
        {
            definition = tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead, false) as BlockTableRecord;
        }
        catch
        {
        }

        if (definition == null || definition.IsAnonymous && definition.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Matrix3d combined;
        try
        {
            // 列向量约定：TransformBy(A * B) 先应用 B 再应用 A。
            // 内层块定义里的点要先经「内层 BlockTransform」映射到外层定义坐标，
            // 再经外层 transform 映射到 WCS，因此是 transform * BlockTransform。
            // 旧写法顺序相反：纯平移时两者恰好相等（所以顶层块一直正常），
            // 但带旋转/缩放的嵌套块文字会整体错位，点在图面上却选不中。
            combined = transform * blockRef.BlockTransform;
        }
        catch
        {
            return;
        }

        Collect(tr, definition, combined, depth + 1, output);
    }

    /// <summary>
    /// 单行文字的位置：非左下对齐时 <c>Position</c> 只是残留值，显示位置在 <c>AlignmentPoint</c>。
    /// 图签里“居中 / 右对齐”非常常见，只记 Position 会让字段区域判空
    /// （日志里表现为区域位置正常、但一个字都取不到）。两个位置都留着。
    /// </summary>
    private static void AddDbTextSample(
        List<ThreePointTextSample> output,
        string? rawText,
        DBText text,
        Matrix3d transform,
        double height)
    {
        var position = text.Position.TransformBy(transform);
        var representative = RepresentativePoints(text, transform);
        Point3d? alignment = null;
        try
        {
            var justified = text.Justify != AttachmentPoint.BaseLeft
                || text.HorizontalMode != TextHorizontalMode.TextLeft
                || text.VerticalMode != TextVerticalMode.TextBase;
            if (justified)
            {
                var ap = text.AlignmentPoint;
                if (Math.Abs(ap.X) > 1e-9 || Math.Abs(ap.Y) > 1e-9)
                {
                    alignment = ap.TransformBy(transform);
                }
            }
        }
        catch
        {
            // 某些代理实体读 AlignmentPoint 会抛异常，退回 Position。
        }

        if (alignment.HasValue)
        {
            AddSample(output, rawText, alignment.Value, height, true, position, representative);
        }
        else
        {
            AddSample(output, rawText, position, height, true, null, representative);
        }
    }

    private static void AddSample(List<ThreePointTextSample> output, string? rawText, Point3d point, double height, bool canAnchor)
    {
        AddSample(output, rawText, point, height, canAnchor, null, null);
    }

    private static void AddSample(
        List<ThreePointTextSample> output,
        string? rawText,
        Point3d point,
        double height,
        bool canAnchor,
        Point3d? altPoint,
        Point3d[]? extraPoints)
    {
        var text = Normalize(rawText);
        if (text.Length == 0)
        {
            return;
        }

        output.Add(new ThreePointTextSample
        {
            Text = text,
            Point = point,
            AltPoint = altPoint,
            ExtraPoints = extraPoints,
            Height = height < 0 ? 0 : height,
            CanAnchor = canAnchor
        });
    }

    /// <summary>
    /// 取文字几何范围上的代表点。块内实体的 <c>GeometricExtents</c> 是块坐标，
    /// 必须经父块变换映射到 WCS；范围退化或读不到时返回 null（不影响既有插入点判定）。
    /// </summary>
    private static Point3d[]? RepresentativePoints(Entity entity, Matrix3d transform)
    {
        try
        {
            var extents = entity.GeometricExtents;
            var min = extents.MinPoint;
            var max = extents.MaxPoint;
            if (Math.Abs(max.X - min.X) + Math.Abs(max.Y - min.Y) < 1e-6)
            {
                return null;
            }

            var cx = (min.X + max.X) * 0.5;
            var cy = (min.Y + max.Y) * 0.5;
            return new[]
            {
                new Point3d(cx, cy, 0).TransformBy(transform),
                new Point3d(cx, min.Y, 0).TransformBy(transform),
                new Point3d(cx, max.Y, 0).TransformBy(transform),
                new Point3d(min.X, cy, 0).TransformBy(transform),
                new Point3d(max.X, cy, 0).TransformBy(transform)
            };
        }
        catch
        {
            return null;
        }
    }

    public static string Normalize(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return "";
        }

        var builder = new StringBuilder(rawText!.Length);
        foreach (var ch in rawText)
        {
            if (ch == '\u3000')
            {
                builder.Append(' ');
                continue;
            }

            if (char.IsControl(ch))
            {
                continue;
            }

            builder.Append(ch);
        }

        var text = builder.ToString().Trim();
        while (text.Contains("  "))
        {
            text = text.Replace("  ", " ");
        }

        return text;
    }

    /// <summary>
    /// 去掉 MText 的格式控制序列，只留可读内容。
    /// <para>
    /// 统一走 <see cref="CadTextSanitizer"/>：早先这里只是「逐个字符丢弃反斜杠」，
    /// 于是 <c>\W0.7;</c> 会剩下 <c>W0.7;</c> 粘在图名前，
    /// 识别出来的图名/图号带着这段垃圾一路进清单、图纸目录和 CSV。
    /// </para>
    /// </summary>
    private static string StripMText(string? contents)
    {
        return CadTextSanitizer.StripFormatting(contents);
    }

    private static bool IsVisible(Entity entity)
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

    private static bool IsProxyLike(Entity entity)
    {
        var name = entity.GetType().Name;
        return name.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("TCH", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static double ScaleOf(Matrix3d transform)
    {
        var sx = Math.Sqrt(transform[0, 0] * transform[0, 0] + transform[0, 1] * transform[0, 1]);
        var sy = Math.Sqrt(transform[1, 0] * transform[1, 0] + transform[1, 1] * transform[1, 1]);
        var scale = Math.Max(sx, sy);
        return scale > 1e-9 ? scale : 1.0;
    }
}
