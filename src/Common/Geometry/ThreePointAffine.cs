using System;
#if ZWCAD
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 三点锚定的仿射求解。
/// 三个不共线的点唯一确定一个仿射变换（平移 2 + 线性 4 = 6 个自由度），
/// 因此平移、旋转、等比/不等比缩放、镜像都能覆盖。
/// 同时用三点算一个“相似性误差”，用于判断目标图签是否与模板同族：
/// 真实图签只会等比缩放 + 旋转，误差大说明是另一个图签凑巧对上了两个点。
/// <para>
/// 三点共线时完整仿射欠定（只剩 4 个约束），此时降级为**相似变换**求解，
/// 见 <see cref="TryFitCollinear"/>：等比缩放 + 旋转 + 平移仍然唯一可解，
/// 只是不再有剪切与非等比能力。
/// </para>
/// </summary>
public static class ThreePointAffine
{
    /// <summary>
    /// |sin θ| 低于此值就认为三点“本意共线”：完整仿射只被约束了 4 个自由度，
    /// 法线方向完全没人管，解出来的矩阵条件数会大到无法用于字段定位。
    /// <para>
    /// 0.05 约合 2.9°。录入时的常规三角形锚点夹角远大于这个值
    /// （录入校验要求三角形的高 / 最长边 &gt; 0.08），不会把正常定义误判成共线。
    /// </para>
    /// </summary>
    public const double CollinearSineThreshold = 0.05;

    public sealed class FitResult
    {
        /// <summary>
        /// 保留的矩阵形式，仅供需要 Matrix3d 的外部代码使用。
        /// <para>
        /// 注意：字段区域映射**不要**走这个矩阵。Matrix3d 的 16 元素数组构造顺序与
        /// “行向量右乘”的直觉并不一致，把平移量放错位置会让纯平移的图框算出接近 0 的坐标
        /// （日志里表现为「区域 0,0-0,0」）。映射一律用下面的 M00/M01/M10/M11/Tx/Ty 自己算。
        /// </para>
        /// </summary>
        public Matrix3d Transform { get; set; } = Matrix3d.Identity;
        public double SimilarityError { get; set; }
        public double Scale { get; set; } = 1.0;
        public double RotationRadians { get; set; }
        public bool Mirrored { get; set; }

        /// <summary>
        /// 三点共线，按相似变换（平移 + 旋转 + 等比缩放）求解。
        /// 此时第三个点只做一致性校验，真正定标的是距离最远的那一对；
        /// 不支持剪切、非等比缩放，也无法分辨镜像。
        /// </summary>
        public bool Collinear { get; set; }

        /// <summary>共线时第三点映射后与目标点的相对残差（按目标跨度归一化，0 表示三点严格同族）。</summary>
        public double CollinearResidual { get; set; }

        /// <summary>x' = M00*x + M01*y + Tx</summary>
        public double M00 { get; set; } = 1.0;
        public double M01 { get; set; }
        /// <summary>y' = M10*x + M11*y + Ty</summary>
        public double M10 { get; set; }
        public double M11 { get; set; } = 1.0;
        public double Tx { get; set; }
        public double Ty { get; set; }

        /// <summary>把模板坐标映射到目标图纸（纯 2D 仿射，不依赖 Matrix3d）。</summary>
        public Point3d Map(double x, double y)
        {
            return new Point3d(M00 * x + M01 * y + Tx, M10 * x + M11 * y + Ty, 0);
        }

        public Point3d Map(Point3d point)
        {
            return Map(point.X, point.Y);
        }
    }

    public static bool TryFit(
        Point3d templateA,
        Point3d templateB,
        Point3d templateC,
        Point3d targetA,
        Point3d targetB,
        Point3d targetC,
        out FitResult result)
    {
        result = new FitResult();

        var ux = templateB.X - templateA.X;
        var uy = templateB.Y - templateA.Y;
        var vx = templateC.X - templateA.X;
        var vy = templateC.Y - templateA.Y;

        var Ux = targetB.X - targetA.X;
        var Uy = targetB.Y - targetA.Y;
        var Vx = targetC.X - targetA.X;
        var Vy = targetC.Y - targetA.Y;

        var lenu = Math.Sqrt(ux * ux + uy * uy);
        var lenv = Math.Sqrt(vx * vx + vy * vy);
        var lenU = Math.Sqrt(Ux * Ux + Uy * Uy);
        var lenV = Math.Sqrt(Vx * Vx + Vy * Vy);
        // 有重合的两个点就定不出方向，无论走哪条路径都没有意义。
        if (lenu < 1e-9 || lenv < 1e-9 || lenU < 1e-9 || lenV < 1e-9)
        {
            return false;
        }

        var det = ux * vy - uy * vx;
        // |det| / (|u|·|v|) 就是两组向量夹角的正弦，与尺度无关，可跨图纸比较。
        var sinTemplate = Math.Abs(det) / (lenu * lenv);
        var sinTarget = Math.Abs(Ux * Vy - Uy * Vx) / (lenU * lenV);

        // 任一侧三点共线（或几乎共线）：完整仿射欠定，法线方向没有被约束，
        // 硬解出来的矩阵会把微小的拾取误差放大成几十上百倍，字段格子直接飞掉。
        // 这里改解相似变换——它只要两个点就能唯一确定，第三个点正好用来校验。
        if (sinTemplate < CollinearSineThreshold || sinTarget < CollinearSineThreshold)
        {
            return TryFitCollinear(templateA, templateB, templateC, targetA, targetB, targetC, out result);
        }

        var a = (Ux * vy - Vx * uy) / det;
        var b = (ux * Vx - vx * Ux) / det;
        var c = (Uy * vy - Vy * uy) / det;
        var d = (ux * Vy - vx * Uy) / det;

        var tx = targetA.X - (a * templateA.X + b * templateA.Y);
        var ty = targetA.Y - (c * templateA.X + d * templateA.Y);

        var scaleX = lenU / lenu;
        var scaleY = lenV / lenv;
        var scaleError = Math.Abs(scaleX - scaleY) / Math.Max(scaleX, scaleY);

        var cosTemplate = (ux * vx + uy * vy) / (lenu * lenv);
        var cosTarget = (Ux * Vx + Uy * Vy) / (lenU * lenV);
        var angleError = Math.Abs(cosTemplate - cosTarget);

        var linear = new Matrix3d(new[]
        {
            a, c, 0, 0,
            b, d, 0, 0,
            0, 0, 1, 0,
            tx, ty, 0, 1
        });

        result = new FitResult
        {
            Transform = linear,
            M00 = a,
            M01 = b,
            M10 = c,
            M11 = d,
            Tx = tx,
            Ty = ty,
            SimilarityError = scaleError + angleError * 2.0,
            Scale = Math.Sqrt(Math.Abs(a * d - b * c)),
            RotationRadians = Math.Atan2(c, a),
            Mirrored = a * d - b * c < 0
        };
        return true;
    }

    /// <summary>
    /// 三点共线时的降级求解。
    /// <para>
    /// 共线三点只给出 4 个独立约束，定不出 6 自由度的完整仿射；但 4 自由度的相似变换
    /// （平移 2 + 旋转 1 + 等比缩放 1）只要两个点就唯一确定，第三个点正好拿来做一致性校验。
    /// 这里枚举三对点各解一次，取「第三点映射残差最小」的那一对；残差按目标三点最大跨度归一化，
    /// 因此与不共线时的 <see cref="FitResult.SimilarityError"/> 同量级，可直接套用同一条容差。
    /// </para>
    /// <para>
    /// 限制：一条直线上的点集对「绕线翻转」是等价的，共线三点分不出镜像，这里恒按非镜像处理；
    /// 也不支持非等比缩放与剪切。图签本身做了镜像时，共线锚点会匹配不上，需改用三角形锚点。
    /// </para>
    /// </summary>
    private static bool TryFitCollinear(
        Point3d templateA,
        Point3d templateB,
        Point3d templateC,
        Point3d targetA,
        Point3d targetB,
        Point3d targetC,
        out FitResult result)
    {
        result = new FitResult();

        var template = new[] { templateA, templateB, templateC };
        var target = new[] { targetA, targetB, targetC };

        // 目标三点的最大跨度，用作残差归一化的尺度。
        var span = Math.Max(
            Distance(targetA, targetB),
            Math.Max(Distance(targetA, targetC), Distance(targetB, targetC)));
        if (span < 1e-9)
        {
            return false;
        }

        var found = false;
        var bestResidual = double.MaxValue;
        var bestBase = 0.0;
        var bestRe = 0.0;
        var bestIm = 0.0;
        var bestTx = 0.0;
        var bestTy = 0.0;

        for (var i = 0; i < 3; i++)
        {
            for (var j = i + 1; j < 3; j++)
            {
                var k = 3 - i - j;

                var ux = template[j].X - template[i].X;
                var uy = template[j].Y - template[i].Y;
                var uu = ux * ux + uy * uy;
                if (uu < 1e-18)
                {
                    continue;
                }

                var Ux = target[j].X - target[i].X;
                var Uy = target[j].Y - target[i].Y;

                // 复数除法 s = U / u = U·conj(u) / |u|²：实部即缩放，虚部即旋转角，
                // 一次同时定出这两项，再回代求平移。s 对应矩阵 [[re, -im], [im, re]]。
                var re = (Ux * ux + Uy * uy) / uu;
                var im = (Uy * ux - Ux * uy) / uu;
                if (Math.Abs(re) < 1e-9 && Math.Abs(im) < 1e-9)
                {
                    continue;
                }

                var tx = target[i].X - (re * template[i].X - im * template[i].Y);
                var ty = target[i].Y - (im * template[i].X + re * template[i].Y);

                // 第三个点只做校验：映射后离它自己的目标点有多远。选错文字时这里立刻爆掉。
                var mx = re * template[k].X - im * template[k].Y + tx;
                var my = im * template[k].X + re * template[k].Y + ty;
                var dx = mx - target[k].X;
                var dy = my - target[k].Y;
                var residual = Math.Sqrt(dx * dx + dy * dy) / span;

                // 残差相同时优先用跨度更大的那对定标，数值更稳。
                var baseLength = Math.Sqrt(uu);
                var better = !found
                    || residual < bestResidual - 1e-12
                    || Math.Abs(residual - bestResidual) <= 1e-12 && baseLength > bestBase;
                if (!better)
                {
                    continue;
                }

                found = true;
                bestResidual = residual;
                bestBase = baseLength;
                bestRe = re;
                bestIm = im;
                bestTx = tx;
                bestTy = ty;
            }
        }

        if (!found)
        {
            return false;
        }

        var scale = Math.Sqrt(bestRe * bestRe + bestIm * bestIm);
        if (scale < 1e-9)
        {
            return false;
        }

        result = new FitResult
        {
            // 与上面仿射分支同样的列主序填法：先 (M00, M10)，再 (M01, M11)。
            Transform = new Matrix3d(new[]
            {
                bestRe, bestIm, 0, 0,
                -bestIm, bestRe, 0, 0,
                0, 0, 1, 0,
                bestTx, bestTy, 0, 1
            }),
            M00 = bestRe,
            M01 = -bestIm,
            M10 = bestIm,
            M11 = bestRe,
            Tx = bestTx,
            Ty = bestTy,
            SimilarityError = bestResidual,
            Scale = scale,
            RotationRadians = Math.Atan2(bestIm, bestRe),
            Mirrored = false,
            Collinear = true,
            CollinearResidual = bestResidual
        };
        return true;
    }

    /// <summary>把矩形（按 4 个角点）变换到目标图纸，返回轴对齐包围盒。</summary>
    public static LocalRectangle TransformRect(Matrix3d transform, LocalRectangle rectangle)
    {
        var corners = TransformCorners(transform, rectangle);
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;
        foreach (var corner in corners)
        {
            if (corner.X < minX) minX = corner.X;
            if (corner.Y < minY) minY = corner.Y;
            if (corner.X > maxX) maxX = corner.X;
            if (corner.Y > maxY) maxY = corner.Y;
        }

        return new LocalRectangle
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            CornerPoints = new[]
            {
                corners[0].X, corners[0].Y,
                corners[1].X, corners[1].Y,
                corners[2].X, corners[2].Y,
                corners[3].X, corners[3].Y
            }
        };
    }

    public static Point3d[] TransformCorners(Matrix3d transform, LocalRectangle rectangle)
    {
        var z = 0.0;
        return new[]
        {
            new Point3d(rectangle.MinX, rectangle.MinY, z).TransformBy(transform),
            new Point3d(rectangle.MaxX, rectangle.MinY, z).TransformBy(transform),
            new Point3d(rectangle.MaxX, rectangle.MaxY, z).TransformBy(transform),
            new Point3d(rectangle.MinX, rectangle.MaxY, z).TransformBy(transform)
        };
    }

    /// <summary>
    /// 用求解出来的 2D 仿射把矩形的 4 个角点映射到目标图纸。
    /// 与 <see cref="TransformCorners(Matrix3d, LocalRectangle)"/> 等价，但不经过 Matrix3d，
    /// 数值行为和 TryFit 的 6 个系数严格一致（见 FitResult.Transform 的说明）。
    /// <para>
    /// 录入时 UCS 与 WCS 不一致的图纸，字段范围在 WCS 里是斜矩形（<see cref="LocalRectangle.CornerPoints"/>）。
    /// 这种区域必须按真实四角映射；退回 Min/Max 的轴对齐四角会把区域放大，
    /// 相邻格子的文字被一起框进来，表现就是图名/图号串位。
    /// </para>
    /// </summary>
    public static Point3d[] TransformCorners(FitResult fit, LocalRectangle rectangle)
    {
        if (rectangle.CornerPoints is { Length: >= 8 } corners)
        {
            return new[]
            {
                fit.Map(corners[0], corners[1]),
                fit.Map(corners[2], corners[3]),
                fit.Map(corners[4], corners[5]),
                fit.Map(corners[6], corners[7])
            };
        }

        return new[]
        {
            fit.Map(rectangle.MinX, rectangle.MinY),
            fit.Map(rectangle.MaxX, rectangle.MinY),
            fit.Map(rectangle.MaxX, rectangle.MaxY),
            fit.Map(rectangle.MinX, rectangle.MaxY)
        };
    }

    /// <summary>点是否落在（可能旋转的）四边形内；tolerance 用于放宽边界。</summary>
    public static bool PolygonContains(Point3d[] polygon, Point3d point, double tolerance)
    {
        if (polygon == null || polygon.Length < 3)
        {
            return false;
        }

        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var pi = polygon[i];
            var pj = polygon[j];
            if (((pi.Y > point.Y) != (pj.Y > point.Y))
                && (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
            {
                inside = !inside;
            }
        }

        if (inside)
        {
            return true;
        }

        if (tolerance <= 0)
        {
            return false;
        }

        for (var i = 0; i < polygon.Length; i++)
        {
            var p1 = polygon[i];
            var p2 = polygon[(i + 1) % polygon.Length];
            if (DistanceToSegment(point, p1, p2) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static double DistanceToSegment(Point3d point, Point3d start, Point3d end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared < 1e-12)
        {
            return Distance(point, start);
        }

        var t = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        t = Math.Max(0, Math.Min(1, t));
        var px = start.X + t * dx;
        var py = start.Y + t * dy;
        return Math.Sqrt((point.X - px) * (point.X - px) + (point.Y - py) * (point.Y - py));
    }

    private static double Distance(Point3d a, Point3d b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
