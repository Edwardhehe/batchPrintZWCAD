using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using QRCoder;

namespace ZwcadBatchPlot;

/// <summary>
/// 把「内容模板 + 纸面尺寸」渲染成二维码 PNG 字节流，供 PDF 后处理贴图。
/// <para>
/// 为什么先出位图而不直接画矢量：PDF 后处理那段只认「一个已存在的图片文件」
/// （<see cref="StampPlacement.ImagePath"/>），复用它可以连 <c>/Rotate</c> 页面补偿、
/// 旋转绘制、产物校验一起白拿。代价是要按足够高的像素密度渲染，见 <see cref="RenderDpi"/>。
/// </para>
/// <para>
/// 用 <see cref="PngByteQRCode"/> 而不是 <c>QRCode</c>：后者内部走 System.Drawing，
/// 有的宿主进程里 GDI+ 状态被 CAD 搅乱会出怪图；前者是纯托管 PNG 编码，四套目标都能跑。
/// </para>
/// </summary>
internal static class QrCodeRenderer
{
    /// <summary>
    /// 位图渲染密度。位图最终会被缩放到几十毫米的物理尺寸，
    /// 600dpi 下 25mm ≈ 591px，每模块十几个像素，缩放后模块边缘不会糊成灰边导致扫不出。
    /// </summary>
    internal const double RenderDpi = 600d;

    /// <summary>同尺寸下内容过长会让模块变密，超过这个字节数就提醒用户（不阻止）。</summary>
    internal const int ContentLengthWarningBytes = 100;

    private static readonly Regex PlaceholderPattern =
        new Regex(@"\{([^{}]+)\}", RegexOptions.Compiled);

    /// <summary>
    /// 展开内容模板：<c>{图号}</c> 之类替换成当前图纸的值。
    /// <para>
    /// 未知占位符**原样保留**，不替换成空——用户把 <c>{图名}</c> 写成 <c>{图名称}</c> 时，
    /// 打出来的二维码里能看到花括号，一眼就知道模板写错了；静默吞掉只会得到一张没人看不懂的码。
    /// </para>
    /// </summary>
    internal static string ExpandTemplate(string? template, PlotJob? job)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "";
        }

        if (job == null)
        {
            return template ?? "";
        }

        return PlaceholderPattern.Replace(template, match =>
        {
            var key = match.Groups[1].Value.Trim();
            string? value = key switch
            {
                "图号" => job.DrawingNumber,
                "图名" => job.Title,
                "版本" => job.Revision,
                "日期" => job.Date,
                "阶段" => job.Phase,
                "文件名" => SafeStem(job.SourceFile),
                "布局" => job.SpaceName,
                "块名" => job.BlockName,
                _ => null,
            };

            return value == null ? match.Value : value;
        }).Trim();
    }

    /// <summary>
    /// 渲染二维码 PNG。失败时返回 <c>false</c> 并给出可读原因，**不抛异常**——
    /// 二维码是可选增强，不能因为它让整批出图失败（与签章同一策略）。
    /// </summary>
    /// <param name="content">二维码内容（已展开模板）。</param>
    /// <param name="errorCorrection">L/M/Q/H，非法值按 M 处理。</param>
    /// <param name="widthMm">目标物理宽度（毫米）。</param>
    /// <param name="heightMm">目标物理高度（毫米）。</param>
    /// <param name="png">渲染结果。</param>
    /// <param name="error">失败原因。</param>
    internal static bool TryRenderPng(
        string? content,
        string? errorCorrection,
        double widthMm,
        double heightMm,
        out byte[] png,
        out string error)
    {
        png = new byte[0];   // net45 没有 Array.Empty<T>()
        error = "";

        if (string.IsNullOrWhiteSpace(content))
        {
            error = "内容为空（检查内容模板里的占位符是否都有值）";
            return false;
        }

        var sideMm = Math.Min(widthMm, heightMm);
        if (sideMm <= 0.1)
        {
            error = "位置尺寸无效（宽或高为 0）";
            return false;
        }

        QRCodeData? data = null;
        try
        {
            using (var generator = new QRCodeGenerator())
            {
                // 内容全 ASCII 时按 ISO-8859-1 编码，兼容性最好；
                // 含中文才开 ECI UTF-8——带 ECI 头会让一部分老扫码器不肯认，没必要为纯数字图号付这个代价。
                var isAscii = IsPureAscii(content!);
                data = generator.CreateQrCode(
                    content,
                    ParseEcc(errorCorrection),
                    forceUtf8: false,
                    utf8BOM: false,
                    eciMode: isAscii ? QRCodeGenerator.EciMode.Iso8859_1 : QRCodeGenerator.EciMode.Utf8,
                    requestedVersion: -1);

                var moduleCount = data.ModuleMatrix.Count;
                var pixelsPerModule = ComputePixelsPerModule(moduleCount, sideMm);
                png = new PngByteQRCode(data).GetGraphic(pixelsPerModule, drawQuietZones: true);
            }

            if (Encoding.UTF8.GetByteCount(content) > ContentLengthWarningBytes)
            {
                // 不拦，只提示：同尺寸下内容越长模块越密，印出来可能扫不出，换更大的位置或缩短模板。
                error = $"内容 {Encoding.UTF8.GetByteCount(content)} 字节，偏长，建议缩短或加大二维码尺寸";
            }

            return png.Length > 0;
        }
        catch (Exception ex)
        {
            // DataTooLongException 等都在这里：给出原因而不是让整批出图中断。
            error = "生成失败：" + ex.Message;
            png = new byte[0];   // net45 没有 Array.Empty<T>()
            return false;
        }
        finally
        {
            data?.Dispose();
        }
    }

    /// <summary>
    /// 每模块像素数：先按目标物理尺寸与 <see cref="RenderDpi"/> 算出目标像素，
    /// 再取整到整模块——每模块正好整数像素，缩放时没有半像素插值，模块边缘才是硬边。
    /// <para>
    /// <paramref name="moduleCount"/> 直接用 <c>QRCodeData.ModuleMatrix.Count</c>，
    /// **不要再加静区**：QRCoder 的矩阵本身已含四周各 4 模块静区（实测 21 个数据模块的 version 1
    /// 返回 Count = 29）。加了会重复计算，位图比目标尺寸小一圈，等于白降密度。
    /// </para>
    /// </summary>
    internal static int ComputePixelsPerModule(int moduleCount, double sideMm)
    {
        if (moduleCount <= 0)
        {
            return 1;
        }

        var targetPixels = sideMm / 25.4d * RenderDpi;
        var perModule = (int)Math.Round(targetPixels / moduleCount, MidpointRounding.AwayFromZero);
        return Math.Max(1, perModule);
    }

    /// <summary>纠错等级字符串转枚举；非法值一律按 M（最常用的平衡档）。</summary>
    private static QRCodeGenerator.ECCLevel ParseEcc(string? value)
    {
        return (value ?? "").Trim().ToUpperInvariant() switch
        {
            "L" => QRCodeGenerator.ECCLevel.L,
            "Q" => QRCodeGenerator.ECCLevel.Q,
            "H" => QRCodeGenerator.ECCLevel.H,
            _ => QRCodeGenerator.ECCLevel.M,
        };
    }

    private static bool IsPureAscii(string value)
    {
        foreach (var ch in value)
        {
            if (ch > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static string SafeStem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        try
        {
            return Path.GetFileNameWithoutExtension(path) ?? "";
        }
        catch
        {
            // 路径里有非法字符时别让模板展开抛异常，原样返回。
            return path ?? "";
        }
    }
}
