using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.PlottingServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

/**
 * @file PlotterService.Media.cs（ZWCAD）
 * @description 介质名选择、缓存与纸张尺寸校验。
 *
 * 主要功能：
 * - SelectMedia / SelectMediaFromNames：按尺寸与名称选介质
 * - FindRasterMediaByAspectRatio：栅格设备按窗口长宽比兜底
 * - GetMediaNames 缓存：避免重复 RefreshLists
 *
 * 核心代码：
 * - FindByPhysicalSize / EnsureExactMediaSize：毫米容差匹配与写回校验
 * - IsRasterPlotDevice：PNG/JPG 等栅格驱动分支入口
 *
 * 注意：单介质无刷新快捷路径（TrySelectExactSingleMediaWithoutRefresh）仅用于性能优化。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** SelectMedia：获取介质列表并选择匹配项（含栅格长宽比兜底）。 */
    private static MediaSelection? SelectMedia(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        PlotJob job,
        AppSettings settings,
        string deviceName,
        bool modelType,
        Extents2d rasterWindow)
    {
        var media = GetMediaNames(validator, plotSettings, deviceName, modelType);
        var windowWidth = Math.Abs(rasterWindow.MaxPoint.X - rasterWindow.MinPoint.X);
        var windowHeight = Math.Abs(rasterWindow.MaxPoint.Y - rasterWindow.MinPoint.Y);
        return SelectMediaFromNames(media, job, settings, deviceName, windowWidth, windowHeight);
    }

    /** SelectMediaFromNames：在给定介质名列表中按尺寸/名称选择。 */
    private static MediaSelection? SelectMediaFromNames(
        IReadOnlyList<string> media,
        PlotJob job,
        AppSettings settings,
        string deviceName,
        double rasterWindowWidth = 0d,
        double rasterWindowHeight = 0d)
    {
        if (media.Count == 0)
        {
            return null;
        }

        if (IsRasterPlotDevice(deviceName))
        {
            RasterPlotOrientation.GetDcsOrientedPaperSize(
                job, rasterWindowWidth, rasterWindowHeight, out var rasterWidth, out var rasterHeight);
            return FindRasterMediaByAspectRatio(media, rasterWidth, rasterHeight);
        }

        var tolerance = job.RequireExactPaperSize
            ? ExactMediaToleranceMm
            : settings.PaperMatchToleranceMm;
        // 扩大纸张留白模式：按有效尺寸（含留白）选纸
        var searchWidth = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var searchHeight = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        var exact = FindByPhysicalSize(media, searchWidth, searchHeight, tolerance);
        if (exact != null)
        {
            return exact;
        }

        if (job.RequireExactPaperSize)
            return null;

        // 名称兜底是标准纸张的固定兼容策略，不再作为用户设置；精确任意纸张已在上方直接返回，仍禁止兜底。
        var paperName = job.PaperName ?? "";
        var plusIndex = paperName.IndexOf('+');
        var basePaper = plusIndex > 0 ? paperName.Substring(0, plusIndex) : paperName;
        if (plusIndex > 0)
        {
            var longNamed = media.FirstOrDefault(x => x.IndexOf(paperName, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? media.FirstOrDefault(x => x.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0
                    && x.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0);
            if (longNamed != null)
            {
                return new MediaSelection { Name = longNamed, NeedsRotation = false };
            }
        }
        else
        {
            var named = media.FirstOrDefault(x => x.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? media.FirstOrDefault(x => x.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0);
            if (named != null)
            {
                return new MediaSelection { Name = named, NeedsRotation = false };
            }
        }

        return null;
    }

    /** FindRasterMediaByAspectRatio：栅格设备按窗口长宽比选介质。 */
    private static MediaSelection? FindRasterMediaByAspectRatio(
        IEnumerable<string> mediaNames,
        double targetWidth,
        double targetHeight)
    {
        if (targetWidth <= 0d || targetHeight <= 0d)
        {
            return null;
        }

        var targetAspect = Math.Max(targetWidth, targetHeight) / Math.Min(targetWidth, targetHeight);
        // 原生 PNG/JPG 绘图器的介质通常以像素命名，并不等于 A 系列毫米纸张。
        // 按长宽比选择最接近且分辨率最大的介质，再由 ScaleToFit 保证图框完整输出。
        return mediaNames
            .Select(name => new { Name = name, Size = TryParseMediaSize(name) })
            .Where(item => item.Size != null
                           && item.Size.Value.Width > 0d
                           && item.Size.Value.Height > 0d)
            .Select(item => new
            {
                item.Name,
                Width = item.Size!.Value.Width,
                Height = item.Size.Value.Height,
                AspectError = Math.Abs(Math.Log(
                    (Math.Max(item.Size.Value.Width, item.Size.Value.Height)
                     / Math.Min(item.Size.Value.Width, item.Size.Value.Height)) / targetAspect))
            })
            .OrderBy(item => item.AspectError)
            // 同比例介质优先选择与 DCS 窗口相同的方向；只有设备缺少该方向时才旋转。
            .ThenBy(item => (item.Width >= item.Height) == (targetWidth >= targetHeight) ? 0 : 1)
            .ThenByDescending(item => item.Width * item.Height)
            .Select(item => new MediaSelection
            {
                Name = item.Name,
                NeedsRotation = (item.Width >= item.Height) != (targetWidth >= targetHeight)
            })
            .FirstOrDefault();
    }

    /** IsRasterPlotDevice：是否为栅格出图设备（PNG/JPG 等）。 */
    private static bool IsRasterPlotDevice(string deviceName)
    {
        return deviceName.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /** TrySelectExactSingleMediaWithoutRefresh：仅一名介质时跳过 RefreshLists 的快捷路径。 */
    private static MediaSelection? TrySelectExactSingleMediaWithoutRefresh(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        PlotJob job,
        AppSettings settings,
        string deviceName,
        bool modelType)
    {
        // 先只重绑设备并读取当前列表；只有新 PMP 尚未可见时，调用方才回退到完整 RefreshLists。
        if (job.CustomPaperWasAdded)
            validator.SetPlotConfigurationName(plotSettings, "None", null);

        validator.SetPlotConfigurationName(plotSettings, deviceName, null);
        TrySetPlotPaperUnits(validator, plotSettings, PlotPaperUnit.Millimeters);
        var names = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
        var media = SelectMediaFromNames(names, job, settings, deviceName);
        if (media != null)
            SetCachedMediaNames(deviceName, modelType, names);

        return media;
    }

    /** HasCachedMediaNames：是否已有该设备的介质名缓存。 */
    private static bool HasCachedMediaNames(string deviceName, bool modelType)
    {
        var cacheKey = BuildMediaNameCacheKey(deviceName, modelType);
        lock (MediaNameCacheLock)
        {
            return MediaNameCache.ContainsKey(cacheKey);
        }
    }

    /** SetCachedMediaNames：写入介质名缓存。 */
    private static void SetCachedMediaNames(string deviceName, bool modelType, IReadOnlyList<string> media)
    {
        var cacheKey = BuildMediaNameCacheKey(deviceName, modelType);
        lock (MediaNameCacheLock)
        {
            MediaNameCache[cacheKey] = media;
        }
    }

    /** GetMediaNames：读取介质名列表（带缓存）。 */
    private static IReadOnlyList<string> GetMediaNames(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        string deviceName,
        bool modelType)
    {
        var cacheKey = BuildMediaNameCacheKey(deviceName, modelType);
        lock (MediaNameCacheLock)
        {
            if (MediaNameCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var media = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
        lock (MediaNameCacheLock)
        {
            // 仅缓存纸张名称，不缓存任何与当前事务绑定的 ZWCAD 对象。
            MediaNameCache[cacheKey] = media;
        }

        return media;
    }

    /** BuildMediaNameCacheKey：介质名缓存键（设备+模型/布局+指纹）。 */
    private static string BuildMediaNameCacheKey(string deviceName, bool modelType)
    {
        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
        var devicePath = string.IsNullOrWhiteSpace(plottersDirectory)
            ? ""
            : Path.Combine(plottersDirectory, deviceName);
        var pmpPath = string.IsNullOrWhiteSpace(plottersDirectory)
            ? ""
            : Path.Combine(
                plottersDirectory,
                "PMP Files",
                Path.GetFileNameWithoutExtension(deviceName) + ".pmp");
        return string.Join("|", deviceName, modelType ? "M" : "P", GetFileFingerprint(devicePath), GetFileFingerprint(pmpPath));
    }

    /** GetFileFingerprint：PC3 等文件指纹。 */
    private static string GetFileFingerprint(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch
        {
            return "unavailable";
        }
    }

    /** TrySetPlotPaperUnits：尝试设置纸张单位，失败返回 false。 */
    private static bool TrySetPlotPaperUnits(PlotSettingsValidator validator, PlotSettings plotSettings, PlotPaperUnit units)
    {
        try
        {
            validator.SetPlotPaperUnits(plotSettings, units);
            return true;
        }
        catch (ZwSoft.ZwCAD.Runtime.Exception ex) when (ex.ErrorStatus == ZwSoft.ZwCAD.Runtime.ErrorStatus.InvalidInput)
        {
            return false;
        }
    }

    /** FindByPhysicalSize：按毫米宽高与容差匹配介质名。 */
    private static MediaSelection? FindByPhysicalSize(IEnumerable<string> mediaNames, double widthMm, double heightMm, double toleranceMm)
    {
        if (widthMm <= 0 || heightMm <= 0)
        {
            return null;
        }

        var parsed = mediaNames
            .Select(name => new { Name = name, Size = TryParseMediaSize(name) })
            .Where(x => x.Size != null)
            .Select(x => new
            {
                x.Name,
                DirectError = DirectSizeError(x.Size!.Value.Width, x.Size.Value.Height, widthMm, heightMm),
                RotatedError = DirectSizeError(x.Size.Value.Width, x.Size.Value.Height, heightMm, widthMm)
            })
            .ToList();

        var direct = parsed
            .Where(x => x.DirectError <= toleranceMm)
            .OrderBy(x => x.DirectError)
            .Select(x => new MediaSelection { Name = x.Name, NeedsRotation = false })
            .FirstOrDefault();
        if (direct != null)
        {
            return direct;
        }

        return parsed
            .Where(x => x.RotatedError <= toleranceMm)
            .OrderBy(x => x.RotatedError)
            .Select(x => new MediaSelection { Name = x.Name, NeedsRotation = true })
            .FirstOrDefault();
    }

    /** static：从介质名解析宽高。 */
    private static (double Width, double Height)? TryParseMediaSize(string mediaName)
    {
        var match = Regex.Match(mediaName, @"(?<w>\d+(?:\.\d+)?)\s*[xX]\s*(?<h>\d+(?:\.\d+)?)\s*(?<unit>MM|毫米|IN|英寸)?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var width = double.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = double.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        if (unit is "IN" or "英寸")
        {
            width *= 25.4;
            height *= 25.4;
        }

        return (width, height);
    }

    /** DirectSizeError：尺寸误差（宽高差之和）。 */
    private static double DirectSizeError(double mediaWidth, double mediaHeight, double targetWidth, double targetHeight)
    {
        return Math.Max(Math.Abs(mediaWidth - targetWidth), Math.Abs(mediaHeight - targetHeight));
    }

    /** EnsureExactMediaSize：要求精确纸张时校验当前介质尺寸。 */
    private static void EnsureExactMediaSize(PlotSettings plotSettings, PlotJob job)
    {
        if (!job.RequireExactPaperSize)
            return;

        var size = plotSettings.PlotPaperSize;
        // 扩大纸张留白模式：校验实际加载尺寸应等于有效尺寸（原始+留白×2），而非原始尺寸。
        var expectedW = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var expectedH = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        var direct = DirectSizeError(size.X, size.Y, expectedW, expectedH);
        var rotated = DirectSizeError(size.X, size.Y, expectedH, expectedW);
        if (Math.Min(direct, rotated) <= ExactMediaToleranceMm)
            return;

        throw new InvalidOperationException(
            $"中望 CAD 实际加载纸张 {size.X:0.######} x {size.Y:0.######} mm，"
            + $"与任意纸张 {expectedW:0.######} x {expectedH:0.######} mm 不一致；"
            + "已停止打印，禁止生成错误页幅。");
    }
}
