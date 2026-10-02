using System;

namespace ZwcadBatchPlot;

/// <summary>
/// 字段区域取字的纯几何判定，不依赖 CAD 类型，便于单元测试。
/// 图框块字段区域（CadTextExtractor 原有路径）与通用型“按图框右下角识别图名图号”共用同一阈值：
/// 文字包围盒中心在区域内，或文字包围盒落在区域内的面积不少于文字包围盒面积的 55%。
/// </summary>
internal static class RegionTextHitTest
{
    /// <summary>文字中心不在区域内时，判为命中所需的最小“重叠面积 / 文字包围盒面积”。</summary>
    internal const double MinTextOverlapRatio = 0.55;

    /// <summary>
    /// 区域与文字包围盒（同一坐标系下的轴对齐矩形）是否有效重叠：
    /// 必须有正面积重叠，且文字包围盒中心在区域内，或重叠面积不少于文字包围盒面积的 55%。
    /// 只压在边线上（重叠面积为 0）不算命中。
    /// </summary>
    internal static bool HasMeaningfulOverlap(LocalRectangle region, LocalRectangle textBounds)
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
        return overlapTextRatio >= MinTextOverlapRatio;
    }

    /// <summary>
    /// 文字是否命中区域。区域是 <paramref name="axes"/> 局部坐标系下的轴对齐矩形，可随图框任意旋转、镜像。
    /// 有文字包围盒（世界坐标轴对齐）时，把包围盒四角变换到局部坐标后只按 <see cref="HasMeaningfulOverlap"/> 判定，
    /// 插入点、对齐点或包围盒角点压在边线上不再单独算命中；
    /// 没有包围盒时与图框块原有路径一致，插入点或对齐点落在区域内（含边线容差）即命中。
    /// </summary>
    /// <param name="axes">世界坐标到区域局部坐标的变换。</param>
    /// <param name="region">局部坐标下的区域。</param>
    /// <param name="worldBounds">文字世界坐标包围盒；无则为 null。</param>
    /// <param name="insertionX">文字插入点世界 X。</param>
    /// <param name="insertionY">文字插入点世界 Y。</param>
    /// <param name="hasAlignmentPoint">是否有对齐点。</param>
    /// <param name="alignmentX">对齐点世界 X。</param>
    /// <param name="alignmentY">对齐点世界 Y。</param>
    /// <returns>命中时返回 true。</returns>
    internal static bool IsTextHit(
        RegionAxes axes,
        LocalRectangle region,
        LocalRectangle? worldBounds,
        double insertionX,
        double insertionY,
        bool hasAlignmentPoint = false,
        double alignmentX = 0,
        double alignmentY = 0)
    {
        if (region == null || !axes.IsValid)
        {
            return false;
        }

        if (worldBounds != null)
        {
            return HasMeaningfulOverlap(region, axes.ToLocalBounds(worldBounds));
        }

        axes.ToLocal(insertionX, insertionY, out var localX, out var localY);
        if (region.Contains(localX, localY))
        {
            return true;
        }

        if (hasAlignmentPoint)
        {
            axes.ToLocal(alignmentX, alignmentY, out var localAlignmentX, out var localAlignmentY);
            if (region.Contains(localAlignmentX, localAlignmentY))
            {
                return true;
            }
        }

        return false;
    }

    private static double RectangleArea(LocalRectangle rectangle)
    {
        return Math.Max(0, rectangle.MaxX - rectangle.MinX)
            * Math.Max(0, rectangle.MaxY - rectangle.MinY);
    }

    /// <summary>
    /// 平面仿射坐标系：局部点 (u, v) 对应世界点 Origin + u·XAxis + v·YAxis。
    /// 轴向量可带长度（每个局部单位对应的世界长度），允许旋转、镜像，不要求正交。
    /// </summary>
    internal readonly struct RegionAxes
    {
        public RegionAxes(double originX, double originY, double xAxisX, double xAxisY, double yAxisX, double yAxisY)
        {
            OriginX = originX;
            OriginY = originY;
            XAxisX = xAxisX;
            XAxisY = xAxisY;
            YAxisX = yAxisX;
            YAxisY = yAxisY;
        }

        public double OriginX { get; }
        public double OriginY { get; }
        public double XAxisX { get; }
        public double XAxisY { get; }
        public double YAxisX { get; }
        public double YAxisY { get; }

        /// <summary>两轴张成的有向面积。</summary>
        private double Determinant => XAxisX * YAxisY - XAxisY * YAxisX;

        /// <summary>坐标有限、两轴非零且不共线时可用。</summary>
        public bool IsValid
        {
            get
            {
                if (!IsFinite(OriginX) || !IsFinite(OriginY)
                    || !IsFinite(XAxisX) || !IsFinite(XAxisY)
                    || !IsFinite(YAxisX) || !IsFinite(YAxisY))
                {
                    return false;
                }

                var xLength = Math.Sqrt(XAxisX * XAxisX + XAxisY * XAxisY);
                var yLength = Math.Sqrt(YAxisX * YAxisX + YAxisY * YAxisY);
                return xLength > 0
                    && yLength > 0
                    && Math.Abs(Determinant) > 1e-9 * xLength * yLength;
            }
        }

        /// <summary>世界点变换到局部坐标（解 Origin + u·XAxis + v·YAxis = 世界点）。</summary>
        public void ToLocal(double worldX, double worldY, out double localX, out double localY)
        {
            var dx = worldX - OriginX;
            var dy = worldY - OriginY;
            var determinant = Determinant;
            localX = (dx * YAxisY - dy * YAxisX) / determinant;
            localY = (dy * XAxisX - dx * XAxisY) / determinant;
        }

        /// <summary>世界轴对齐包围盒四角变换到局部坐标后的外包矩形，与 CadTextExtractor 原有做法一致。</summary>
        public LocalRectangle ToLocalBounds(LocalRectangle worldBounds)
        {
            ToLocal(worldBounds.MinX, worldBounds.MinY, out var x1, out var y1);
            ToLocal(worldBounds.MinX, worldBounds.MaxY, out var x2, out var y2);
            ToLocal(worldBounds.MaxX, worldBounds.MinY, out var x3, out var y3);
            ToLocal(worldBounds.MaxX, worldBounds.MaxY, out var x4, out var y4);
            return LocalRectangle.FromPoints(
                Math.Min(Math.Min(x1, x2), Math.Min(x3, x4)),
                Math.Min(Math.Min(y1, y2), Math.Min(y3, y4)),
                Math.Max(Math.Max(x1, x2), Math.Max(x3, x4)),
                Math.Max(Math.Max(y1, y2), Math.Max(y3, y4)));
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
