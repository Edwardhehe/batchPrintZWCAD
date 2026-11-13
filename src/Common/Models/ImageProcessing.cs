using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// CadUsings.cs 的全局 using 同时引入了 CAD 的 DatabaseServices，那里也有一个 Image
// （栅格图像实体）。本文件只处理 GDI+ 位图，显式指到 System.Drawing。
using Image = System.Drawing.Image;

namespace ZwcadBatchPlot;

/// <summary>
/// 签名 / 印章图片的处理参数：去底色、修剪四周空白、重采样尺寸。
/// <para>
/// 参数本身会存进签名库（<see cref="SignatureItem.ImageProcess"/>），所以再打开「图片处理」时
/// 能接着上次的滑块位置继续调，而不用从零试。真正的处理永远从原图备份重算
/// （见 <see cref="SignatureLibraryStore.GetProcessingBasePath"/>），反复调参不会层层劣化。
/// </para>
/// </summary>
public sealed class ImageProcessSettings
{
    /// <summary>是否把接近纯白的底色变成透明。</summary>
    public bool RemoveBackground { get; set; }

    /// <summary>去底亮度阈值：亮度 ≥ 该值全透明，往下 <see cref="ImageProcessor.SoftRange"/> 内线性过渡。</summary>
    public int BackgroundThreshold { get; set; } = ImageProcessor.DefaultThreshold;

    /// <summary>是否自动剪掉四周空白（按 alpha 判定）。</summary>
    public bool Trim { get; set; }

    /// <summary>修剪后保留的空白边距（像素），避免笔画贴边被裁掉一半的抗锯齿。</summary>
    public int TrimPaddingPx { get; set; }

    /// <summary>重采样目标宽度（像素）；0 = 保持原始像素尺寸。</summary>
    public int TargetWidthPx { get; set; }

    /// <summary>
    /// 重采样目标高度（像素）；0 = 由宽度按图片纵横比推出。
    /// <para>只有关掉「约束比例」时才会填非零值（允许用户主动改成非等比，代价是图会变形）。</para>
    /// </summary>
    public int TargetHeightPx { get; set; }

    /// <summary>
    /// 约束比例（默认开）：只按宽度缩放、高度自动推导，图片不会变形。
    /// 关掉后宽度与高度各按各的填，允许压扁 / 拉长。
    /// </summary>
    public bool LockAspectRatio { get; set; } = true;

    /// <summary>
    /// 清晰度：0 = 原样；正数锐化（强化笔画边缘），负数柔化（模糊掉毛刺噪点）。
    /// 取值范围见 <see cref="ImageProcessor.MinSharpness"/> ~ <see cref="ImageProcessor.MaxSharpness"/>。
    /// </summary>
    public int Sharpness { get; set; }

    /// <summary>
    /// 分辨率（DPI）：只用于「毫米 ↔ 像素」换算和写进图片的物理尺寸信息，不改变像素数量。
    /// <para>
    /// 600dpi 扫描件要填 600，否则在「毫米」下按 300dpi 换算出来的尺寸会小一半。
    /// </para>
    /// </summary>
    public int Dpi { get; set; } = ImageProcessor.DefaultDpiValue;

    /// <summary>
    /// 出图时的印章标注宽度（毫米）。0 = 完全按图框槽位等比内接。
    /// <para>
    /// <b>这是全流程唯一的「章体多大」真值</b>：「图片处理」里的毫米输入框与「编辑印章」里的
    /// 印章宽度框编辑的是同一个值，出图也直接读它（见 <see cref="SignatureItem.TryGetEffectiveSizeMm"/>），
    /// 不再有第二份独立数据。像素重采样（<see cref="TargetWidthPx"/>）是另一件正交的事：
    /// 为了让图更清晰而放大像素，不等于要改变打出来的物理尺寸。
    /// </para>
    /// </summary>
    public double PhysicalWidthMm { get; set; }

    /// <summary>
    /// 出图时的印章标注高度（毫米）。0 = 由宽度与图片纵横比推出。
    /// <para>与 <see cref="PhysicalWidthMm"/> 同时填写时取「两者都不超出」的较小比例，章体不变形。</para>
    /// </summary>
    public double PhysicalHeightMm { get; set; }

    /// <summary>
    /// 取出「这枚章应该打多大」。0 / 0 表示「跟随图框槽位等比内接」。
    /// <para>
    /// <b>只认显式标注</b>，不拿 <see cref="TargetWidthPx"/> 反推：界面上「调整尺寸」写的是
    /// 「重采样图片像素，不改变出图时在图框里的实际大小」，若把像素数当成尺寸，
    /// 用户为了清晰度放大过图片的印章会突然变小——那既是行为倒退，也砸了界面上的承诺。
    /// </para>
    /// </summary>
    /// <returns>宽或高任一有效即为 true。</returns>
    public bool TryGetDeclaredSizeMm(out double widthMm, out double heightMm)
    {
        widthMm = PhysicalWidthMm > 0 ? PhysicalWidthMm : 0;
        heightMm = PhysicalHeightMm > 0 ? PhysicalHeightMm : 0;

        // 小到 0.5mm 以下的「标注」只可能是手滑（0.0x 之类的输入），
        // 当真去缩放会让章体缩成看不见的一点，宁可按「跟随槽位」处理。
        if (widthMm < MinDeclaredSizeMm)
        {
            widthMm = 0;
        }

        if (heightMm < MinDeclaredSizeMm)
        {
            heightMm = 0;
        }

        return widthMm > 0 || heightMm > 0;
    }

    /// <summary>小于这个毫米数的标注尺寸不予采信（见 <see cref="TryGetDeclaredSizeMm"/>）。</summary>
    internal const double MinDeclaredSizeMm = 0.5;

    public bool HasEffect
        => RemoveBackground
            || Trim
            || TargetWidthPx > 0
            || TargetHeightPx > 0
            || Sharpness != 0
            || Dpi != ImageProcessor.DefaultDpiValue;

    public ImageProcessSettings Clone()
    {
        return new ImageProcessSettings
        {
            RemoveBackground = RemoveBackground,
            BackgroundThreshold = BackgroundThreshold,
            Trim = Trim,
            TrimPaddingPx = TrimPaddingPx,
            TargetWidthPx = TargetWidthPx,
            TargetHeightPx = TargetHeightPx,
            LockAspectRatio = LockAspectRatio,
            Sharpness = Sharpness,
            Dpi = Dpi,
            PhysicalWidthMm = PhysicalWidthMm,
            PhysicalHeightMm = PhysicalHeightMm,
        };
    }

    /// <summary>界面上的一句话说明（状态栏 / 日志用）。</summary>
    public string Describe()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (RemoveBackground)
        {
            parts.Add($"去底色(阈值{BackgroundThreshold})");
        }

        if (Trim)
        {
            parts.Add(TrimPaddingPx > 0 ? $"修剪空白(留{TrimPaddingPx}px)" : "修剪空白");
        }

        if (TargetWidthPx > 0 || TargetHeightPx > 0)
        {
            parts.Add(TargetWidthPx > 0 && TargetHeightPx > 0
                ? $"宽{TargetWidthPx}×高{TargetHeightPx}px"
                : TargetWidthPx > 0 ? $"宽{TargetWidthPx}px" : $"高{TargetHeightPx}px");
        }

        if (Sharpness != 0)
        {
            parts.Add($"清晰度{Sharpness:+#;-#;0}");
        }

        if (Dpi > 0 && Dpi != ImageProcessor.DefaultDpiValue)
        {
            parts.Add($"{Dpi}dpi");
        }

        // 尺寸单独说明：它不改变像素，但决定打出来多大，用户最关心的往往是这一个。
        if (TryGetDeclaredSizeMm(out var widthMm, out var heightMm))
        {
            parts.Add(heightMm > 0
                ? $"尺寸{widthMm:0.#}×{heightMm:0.#}mm"
                : $"宽{widthMm:0.#}mm");
        }

        return parts.Count == 0 ? "未处理" : string.Join("、", parts);
    }
}

/// <summary>
/// 图片处理引擎：只做像素运算，不碰文件、不碰库。
/// <para>
/// 全部用 <see cref="Marshal"/> 拷贝像素而不是 unsafe 指针：四个目标工程都没开
/// AllowUnsafeBlocks，为几处像素循环改全局编译选项不划算。
/// </para>
/// </summary>
public static class ImageProcessor
{
    /// <summary>去底默认阈值（与历史实现一致：≥246 全透明，214~246 线性过渡）。</summary>
    public const int DefaultThreshold = 246;

    /// <summary>阈值滑块下限。再低会把浅灰笔画也吃掉，留着只会让人误操作。</summary>
    public const int MinThreshold = 150;

    /// <summary>阈值滑块上限。</summary>
    public const int MaxThreshold = 254;

    /// <summary>去底的过渡带宽度（亮度差），让笔画抗锯齿边缘平滑衰减而不是硬切。</summary>
    public const int SoftRange = 32;

    /// <summary>alpha 低于该值视为「空白」。用于修剪与内容边界判定。</summary>
    public const int AlphaCutoff = 8;

    /// <summary>默认分辨率：办公打印机的常用值。</summary>
    public const double DefaultDpi = 300d;

    /// <summary><see cref="DefaultDpi"/> 的整数形式（DPI 参数一律用整数）。</summary>
    public const int DefaultDpiValue = 300;

    /// <summary>允许输入的最小 DPI。再低就属于「屏幕图」，换算毫米毫无意义。</summary>
    public const int MinDpi = 36;

    public const int MaxDpi = 2400;

    public const double MmPerInch = 25.4d;

    /// <summary>清晰度下限（柔化模糊）。</summary>
    public const int MinSharpness = -100;

    /// <summary>清晰度上限（锐化）。</summary>
    public const int MaxSharpness = 100;

    /// <summary>把任意 DPI 收进合法区间；越界视为没填，回落到默认值。</summary>
    public static int NormalizeDpi(double dpi)
    {
        if (double.IsNaN(dpi) || dpi < MinDpi || dpi > MaxDpi)
        {
            return DefaultDpiValue;
        }

        return (int)Math.Round(dpi);
    }

    /// <summary>像素 → 毫米（按 <see cref="DefaultDpi"/>）。</summary>
    public static double PixelsToMm(double pixels) => PixelsToMm(pixels, DefaultDpiValue);

    /// <summary>像素 → 毫米（按给定分辨率）。</summary>
    public static double PixelsToMm(double pixels, double dpi)
    {
        return pixels / NormalizeDpi(dpi) * MmPerInch;
    }

    /// <summary>毫米 → 像素（按 <see cref="DefaultDpi"/>），至少 1 像素。</summary>
    public static int MmToPixels(double millimeters) => MmToPixels(millimeters, DefaultDpiValue);

    /// <summary>毫米 → 像素（按给定分辨率），至少 1 像素。</summary>
    public static int MmToPixels(double millimeters, double dpi)
    {
        var pixels = (int)Math.Round(millimeters / MmPerInch * NormalizeDpi(dpi));
        return Math.Max(1, pixels);
    }

    /// <summary>
    /// 按参数处理一张图，返回新位图（调用方负责 Dispose）。
    /// 顺序固定：先去底（决定哪些像素算空白）→ 再修剪（按 alpha）→ 缩放 → 清晰度（最后）。
    /// <para>
    /// 顺序反了各有代价：修剪夹在中间会按白底算出整幅图；清晰度放缩放之前，
    /// 重采样的柔化会把锐化效果吃掉一半（先锐化再缩小的图总是显得发虚）。
    /// </para>
    /// </summary>
    public static Bitmap Process(Image source, ImageProcessSettings? settings)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var current = CloneArgb(source);

        if (settings == null)
        {
            return current;
        }

        if (settings.RemoveBackground)
        {
            var next = ApplyBackgroundRemoval(current, settings.BackgroundThreshold);
            current.Dispose();
            current = next;
        }

        if (settings.Trim)
        {
            var next = ApplyTrim(current, settings.TrimPaddingPx);
            current.Dispose();
            current = next;
        }

        if ((settings.TargetWidthPx > 0 || settings.TargetHeightPx > 0)
            && (settings.TargetWidthPx != current.Width || settings.TargetHeightPx > 0))
        {
            var next = ApplyResize(current, settings.TargetWidthPx, settings.TargetHeightPx);
            current.Dispose();
            current = next;
        }

        if (settings.Sharpness != 0)
        {
            var next = ApplySharpness(current, settings.Sharpness);
            current.Dispose();
            current = next;
        }

        if (settings.Dpi > 0)
        {
            try
            {
                var dpi = NormalizeDpi(settings.Dpi);
                current.SetResolution(dpi, dpi);
            }
            catch (Exception)
            {
                // DPI 只是元数据，某些 GDI+ 驱动会拒绝设置，失败不影响图像本身。
                // 换算用的仍是我们自己算的 settings.Dpi，与这里无关。
            }
        }

        return current;
    }

    /// <summary>
    /// 去底色：亮度超过阈值的像素变全透明，阈值下方一段区间按亮度线性降低 alpha。
    /// <para>
    /// 只改 alpha 不改 RGB：签名笔画的抗锯齿边缘靠半透明保留，硬阈值二值化会让笔画出锯齿；
    /// 彩色印章（红章）的 RGB 原样保留，只去掉扫描件底灰。
    /// </para>
    /// </summary>
    public static Bitmap ApplyBackgroundRemoval(Bitmap source, int threshold)
    {
        threshold = Math.Max(MinThreshold, Math.Min(MaxThreshold, threshold));
        var low = Math.Max(0, threshold - SoftRange);

        var result = CloneArgb(source);
        var data = result.LockBits(
            new Rectangle(0, 0, result.Width, result.Height),
            ImageLockMode.ReadWrite,
            PixelFormat.Format32bppArgb);
        try
        {
            var count = data.Stride * result.Height;
            var buffer = new byte[count];
            Marshal.Copy(data.Scan0, buffer, 0, count);

            for (var offset = 0; offset < count; offset += 4)
            {
                var alpha = buffer[offset + 3];
                if (alpha == 0)
                {
                    continue;
                }

                // 内存里是 BGRA 顺序。
                var blue = buffer[offset];
                var green = buffer[offset + 1];
                var red = buffer[offset + 2];
                var luminance = (red * 299 + green * 587 + blue * 114) / 1000;

                int scale;
                if (luminance >= threshold)
                {
                    scale = 0;
                }
                else if (luminance > low)
                {
                    scale = (threshold - luminance) * 255 / (threshold - low);
                }
                else
                {
                    scale = 255;
                }

                buffer[offset + 3] = (byte)(alpha * scale / 255);
            }

            Marshal.Copy(buffer, 0, data.Scan0, count);
        }
        finally
        {
            result.UnlockBits(data);
        }

        return result;
    }

    /// <summary>
    /// 修剪四周空白：按 alpha 找不透明内容的外接矩形，裁掉外面的部分，再向外扩
    /// <paramref name="paddingPx"/> 像素留边。整幅全透明（或全不透明）时原样返回。
    /// </summary>
    public static Bitmap ApplyTrim(Bitmap source, int paddingPx)
    {
        var bounds = GetContentBounds(source);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return CloneArgb(source);
        }

        paddingPx = Math.Max(0, paddingPx);
        var left = Math.Max(0, bounds.Left - paddingPx);
        var top = Math.Max(0, bounds.Top - paddingPx);
        var right = Math.Min(source.Width, bounds.Right + paddingPx);
        var bottom = Math.Min(source.Height, bounds.Bottom + paddingPx);

        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0 || (width == source.Width && height == source.Height))
        {
            return CloneArgb(source);
        }

        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, width, height),
                new Rectangle(left, top, width, height),
                GraphicsUnit.Pixel);
        }

        return result;
    }

    /// <summary>等比缩放到指定宽度（高度按原比例四舍五入）。</summary>
    public static Bitmap ApplyResize(Bitmap source, int targetWidthPx)
    {
        return ApplyResize(source, targetWidthPx, 0);
    }

    /// <summary>
    /// 缩放到目标宽高；某一边传 0 时按原图纵横比推出那一边（等比缩放，不变形）。
    /// <para>
    /// 两边都填则严格按给定尺寸重采样——会变形，只有用户主动取消「约束比例」才走这条路径。
    /// </para>
    /// </summary>
    public static Bitmap ApplyResize(Bitmap source, int targetWidthPx, int targetHeightPx)
    {
        if (targetWidthPx <= 0 && targetHeightPx <= 0)
        {
            return CloneArgb(source);
        }

        if (targetWidthPx <= 0)
        {
            targetWidthPx = Math.Max(1, (int)Math.Round(source.Width * (targetHeightPx / (double)source.Height)));
        }
        else if (targetHeightPx <= 0)
        {
            targetHeightPx = Math.Max(1, (int)Math.Round(source.Height * (targetWidthPx / (double)source.Width)));
        }

        if (targetWidthPx == source.Width && targetHeightPx == source.Height)
        {
            return CloneArgb(source);
        }

        var result = new Bitmap(targetWidthPx, targetHeightPx, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(result))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            // 缩放签名这类细线条必须用高质量插值，否则缩小后笔画会出现明显断裂。
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, targetWidthPx, targetHeightPx));
        }

        return result;
    }

    /// <summary>
    /// 清晰度调节：正数锐化（unsharp mask），负数柔化（箱式模糊），0 原样返回。
    /// <para>
    /// 模糊半径按<b>图片短边</b>折算，而不是固定几个像素：对话框里预览的是缩略图，
    /// 固定半径会让预览看着很猛、真图却几乎没变化（或反过来）。按比例算两边效果才一致。
    /// </para>
    /// </summary>
    public static Bitmap ApplySharpness(Bitmap source, int strength)
    {
        strength = Math.Max(MinSharpness, Math.Min(MaxSharpness, strength));
        if (strength == 0 || source.Width < 3 || source.Height < 3)
        {
            return CloneArgb(source);
        }

        var shortSide = Math.Min(source.Width, source.Height);
        if (strength > 0)
        {
            // 锐化：模糊一份出来做 unsharp mask，增益 0.02 ~ 2.0。
            var gain = strength / 50.0;
            var radius = RadiusFor(shortSide, 0.0012 * (1 + gain), 1, 4);
            using var blurred = BoxBlur(source, radius, blurAlpha: false);
            return UnsharpMask(source, blurred, gain);
        }

        // 柔化：半径随强度增长，再按强度把原图与模糊图混合，滑块才是连续过渡。
        // 除以 60（而不是 100）是因为纯按比例混合时 -30 在实图上几乎看不出来，用户会以为滑块失灵。
        var amount = Math.Min(1.0, -strength / 60.0);
        var softRadius = RadiusFor(shortSide, 0.0032 * (0.35 + amount), 1, 6);
        using var soft = BoxBlur(source, softRadius, blurAlpha: true);
        return BlendToward(source, soft, amount);
    }

    private static int RadiusFor(int shortSide, double ratio, int min, int max)
    {
        var radius = (int)Math.Round(shortSide * ratio);
        return Math.Max(min, Math.Min(max, radius));
    }

    /// <summary>
    /// 可分箱式模糊，每像素 O(1)（行内用前缀和，竖直方向用滚动窗口）。
    /// <para>
    /// RGB 以 alpha 作权平均：去底色只把 alpha 清零、RGB 还留着白色，
    /// 直接平均会把这些白像素混进笔画边缘，形成一圈白晕。
    /// alpha 是否一起模糊由 <paramref name="blurAlpha"/> 决定——锐化只动 RGB，
    /// 边缘该保持锐利，不能顺带变软。
    /// </para>
    /// </summary>
    private static Bitmap BoxBlur(Bitmap source, int radius, bool blurAlpha)
    {
        radius = Math.Max(0, radius);
        var width = source.Width;
        var height = source.Height;
        if (radius == 0 || width <= 0 || height <= 0)
        {
            return CloneArgb(source);
        }

        var src = ReadPixels(source, out var srcStride);

        var window = radius * 2 + 1;
        // 环形缓冲：每个槽位是一行的横向窗口和，每像素 4 个 int（B、G、R 的加权和 + alpha 和）。
        var slots = new int[window][];
        var vertical = new int[width * 4];
        var prefix = new int[(width + 2) * 4];
        // 每个 x 的有效横向窗口宽度：边缘窗口会变窄，算 alpha 平均时要按实际宽度除。
        var horizontalCounts = new int[width];

        void ComputeHorizontalRow(int row)
        {
            var slot = row % window;
            var buffer = slots[slot] ?? (slots[slot] = new int[width * 4]);
            var offset = row * srcStride;

            prefix[0] = 0;
            prefix[1] = 0;
            prefix[2] = 0;
            prefix[3] = 0;
            for (var x = 0; x < width; x++)
            {
                var o = offset + x * 4;
                var alpha = src[o + 3];
                var p = (x + 1) * 4;
                prefix[p] = prefix[p - 4] + src[o] * alpha;
                prefix[p + 1] = prefix[p - 3] + src[o + 1] * alpha;
                prefix[p + 2] = prefix[p - 2] + src[o + 2] * alpha;
                prefix[p + 3] = prefix[p - 1] + alpha;
            }

            for (var x = 0; x < width; x++)
            {
                var x1 = Math.Max(0, x - radius);
                var x2 = Math.Min(width - 1, x + radius);
                var p1 = x1 * 4;
                var p2 = (x2 + 1) * 4;
                var d = x * 4;
                buffer[d] = prefix[p2] - prefix[p1];
                buffer[d + 1] = prefix[p2 + 1] - prefix[p1 + 1];
                buffer[d + 2] = prefix[p2 + 2] - prefix[p1 + 2];
                buffer[d + 3] = prefix[p2 + 3] - prefix[p1 + 3];
            }
        }

        void AccumulateRow(int row, int sign)
        {
            var buffer = slots[row % window];
            if (buffer == null)
            {
                return;
            }

            if (sign > 0)
            {
                for (var i = 0; i < vertical.Length; i++)
                {
                    vertical[i] += buffer[i];
                }
            }
            else
            {
                for (var i = 0; i < vertical.Length; i++)
                {
                    vertical[i] -= buffer[i];
                }
            }
        }

        for (var x = 0; x < width; x++)
        {
            horizontalCounts[x] = Math.Min(width - 1, x + radius) - Math.Max(0, x - radius) + 1;
        }

        var initialBottom = Math.Min(height - 1, radius);
        for (var row = 0; row <= initialBottom; row++)
        {
            ComputeHorizontalRow(row);
            AccumulateRow(row, +1);
        }

        var dst = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            if (y > 0)
            {
                // 先减掉滑出窗口的行，再算新行：两者在环形缓冲里是同一个槽位，
                // 顺序反了会把还没减掉的行直接覆盖，整幅图会整体变暗。
                var dropped = y - radius - 1;
                if (dropped >= 0)
                {
                    AccumulateRow(dropped, -1);
                }

                var added = y + radius;
                if (added < height)
                {
                    ComputeHorizontalRow(added);
                    AccumulateRow(added, +1);
                }
            }

            var rowsInWindow = Math.Min(height - 1, y + radius) - Math.Max(0, y - radius) + 1;
            var dstRow = y * width * 4;
            var srcRow = y * srcStride;

            for (var x = 0; x < width; x++)
            {
                var d = dstRow + x * 4;
                var v = x * 4;
                var weight = vertical[v + 3];
                if (weight <= 0)
                {
                    dst[d] = 0;
                    dst[d + 1] = 0;
                    dst[d + 2] = 0;
                    dst[d + 3] = blurAlpha ? (byte)0 : src[srcRow + x * 4 + 3];
                    continue;
                }

                dst[d] = (byte)Math.Min(255, vertical[v] / weight);
                dst[d + 1] = (byte)Math.Min(255, vertical[v + 1] / weight);
                dst[d + 2] = (byte)Math.Min(255, vertical[v + 2] / weight);
                dst[d + 3] = blurAlpha
                    ? (byte)Math.Min(255, weight / (horizontalCounts[x] * rowsInWindow))
                    : src[srcRow + x * 4 + 3];
            }
        }

        return WritePixels(dst, width, height, width * 4);
    }

    /// <summary>unsharp mask：原图 + 增益 ×（原图 − 模糊图）。alpha 保持不变。</summary>
    private static Bitmap UnsharpMask(Bitmap source, Bitmap blurred, double gain)
    {
        var width = source.Width;
        var height = source.Height;
        var src = ReadPixels(source, out var srcStride);
        var blur = ReadPixels(blurred, out var blurStride);
        var dst = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var s = y * srcStride;
            var b = y * blurStride;
            var d = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var so = s + x * 4;
                var bo = b + x * 4;
                dst[d] = SharpenChannel(src[so], blur[bo], gain);
                dst[d + 1] = SharpenChannel(src[so + 1], blur[bo + 1], gain);
                dst[d + 2] = SharpenChannel(src[so + 2], blur[bo + 2], gain);
                // alpha 原样保留：锐化只强化明暗对比，把边缘 alpha 也一起放大只会生出硬毛刺。
                dst[d + 3] = src[so + 3];
                d += 4;
            }
        }

        return WritePixels(dst, width, height, width * 4);
    }

    private static byte SharpenChannel(byte value, byte blurred, double gain)
    {
        var result = value + gain * (value - blurred);
        if (result <= 0)
        {
            return 0;
        }

        return result >= 255 ? (byte)255 : (byte)Math.Round(result);
    }

    /// <summary>按比例往目标图靠：0 = 原图，1 = 全用目标图；alpha 也一起过渡。</summary>
    private static Bitmap BlendToward(Bitmap source, Bitmap target, double amount)
    {
        var width = source.Width;
        var height = source.Height;
        var src = ReadPixels(source, out var srcStride);
        var tgt = ReadPixels(target, out var tgtStride);
        var dst = new byte[width * height * 4];

        for (var y = 0; y < height; y++)
        {
            var s = y * srcStride;
            var t = y * tgtStride;
            var d = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var so = s + x * 4;
                var to = t + x * 4;
                dst[d] = MixChannel(src[so], tgt[to], amount);
                dst[d + 1] = MixChannel(src[so + 1], tgt[to + 1], amount);
                dst[d + 2] = MixChannel(src[so + 2], tgt[to + 2], amount);
                dst[d + 3] = MixChannel(src[so + 3], tgt[to + 3], amount);
                d += 4;
            }
        }

        return WritePixels(dst, width, height, width * 4);
    }

    private static byte MixChannel(byte from, byte to, double amount)
    {
        var value = from + (to - from) * amount;
        if (value <= 0)
        {
            return 0;
        }

        return value >= 255 ? (byte)255 : (byte)Math.Round(value);
    }

    /// <summary>把整幅像素读进数组（32bppArgb，内存序 BGRA）。</summary>
    private static byte[] ReadPixels(Bitmap bitmap, out int stride)
    {
        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            var buffer = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>把像素数组写成新位图；<paramref name="stride"/> 是数组自己的行跨度。</summary>
    private static Bitmap WritePixels(byte[] buffer, int width, int height, int stride)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == stride)
            {
                Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            }
            else
            {
                // 目标行跨度更大时只能逐行拷，整块拷会写越界。
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(buffer, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>
    /// 不透明内容的外接矩形（按 <see cref="AlphaCutoff"/> 判定）。整幅没有不透明像素时返回空矩形。
    /// </summary>
    public static Rectangle GetContentBounds(Bitmap source)
    {
        var data = source.LockBits(
            new Rectangle(0, 0, source.Width, source.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            var count = data.Stride * source.Height;
            var buffer = new byte[count];
            Marshal.Copy(data.Scan0, buffer, 0, count);

            var minX = int.MaxValue;
            var minY = int.MaxValue;
            var maxX = -1;
            var maxY = -1;

            for (var y = 0; y < source.Height; y++)
            {
                var rowStart = y * data.Stride;
                for (var x = 0; x < source.Width; x++)
                {
                    if (buffer[rowStart + x * 4 + 3] <= AlphaCutoff)
                    {
                        continue;
                    }

                    if (x < minX)
                    {
                        minX = x;
                    }

                    if (x > maxX)
                    {
                        maxX = x;
                    }

                    if (y < minY)
                    {
                        minY = y;
                    }

                    if (y > maxY)
                    {
                        maxY = y;
                    }
                }
            }

            if (maxX < 0 || maxY < 0)
            {
                return Rectangle.Empty;
            }

            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }
        finally
        {
            source.UnlockBits(data);
        }
    }

    /// <summary>复制一份 32bppArgb 位图（统一像素格式，后面的 LockBits 步长才可预测）。</summary>
    public static Bitmap CloneArgb(Image source)
    {
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        return bitmap;
    }
}
