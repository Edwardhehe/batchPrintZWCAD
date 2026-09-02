using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

/**
 * @file PlotterService.Media.cs（AutoCAD）
 * @description 图纸/介质选择与纸张尺寸匹配。
 *
 * 主要功能：
 * - ChooseMedia：按纸名、物理尺寸、旋转偏好选介质
 * - GetMediaCatalog / 缓存：减少 PC3 刷新成本
 * - BestRasterMedia：PNG/JPG 毫米匹配失败时的长宽比兜底
 *
 * 核心代码：
 * - IsRasterPlotDevice：区分栅格与矢量设备
 * - GetPlotPaperSizeMm / PixelsToMillimeters：像素介质尺寸换算到毫米再比对
 * - EnsureRequiredMediaSize：校验后确认介质尺寸仍符合任务要求
 *
 * 注意：选纸/旋转应与 PDF 同一策略；栅格仅在匹配失败时走 BestRasterMedia。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** ChooseMedia：按纸名/尺寸/旋转选择介质；栅格毫米匹配失败时走 BestRasterMedia。 */
    private static MediaChoice ChooseMedia(
        PlotSettingsValidator validator,
        Layout layout,
        string deviceName,
        PlotJob job,
        Extents2d window,
        out bool usedCachedCatalog)
    {
        var catalog = GetMediaCatalog(
            validator,
            layout,
            deviceName,
            // 批打已在开始前一次性写入全部纸张；仅首张新增纸触发设备重载，后续精确纸张复用缓存。
            forceDeviceReload: job.CustomPaperWasAdded,
            out usedCachedCatalog);
        var names = catalog.Select(x => x.Name).ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException($"打印机没有可用纸张: {deviceName}");
        }

        // 扩大纸张留白模式：按有效尺寸（含留白）选纸
        var targetWidth = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm
            : job.PaperWidthMm > 0 ? job.PaperWidthMm : Math.Abs(job.MaxX - job.MinX);
        var targetHeight = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm
            : job.PaperHeightMm > 0 ? job.PaperHeightMm : Math.Abs(job.MaxY - job.MinY);
        var choices = catalog.Select(item =>
        {
            var directError = DirectSizeError(item.WidthMm, item.HeightMm, targetWidth, targetHeight);
            var rotatedError = DirectSizeError(item.WidthMm, item.HeightMm, targetHeight, targetWidth);
            return new MediaChoice
            {
                Name = item.Name,
                WidthMm = item.WidthMm,
                HeightMm = item.HeightMm,
                Error = Math.Min(directError, rotatedError),
                IsFullBleed = item.IsFullBleed,
                PreferredRotation = rotatedError < directError ? PlotRotation.Degrees090 : PlotRotation.Degrees000
            };
        }).ToList();

        // PNG/JPG 与 PDF 同一套选纸：目标毫米尺寸来自前端；目录项已把像素换算成毫米。
        // 仅在毫米匹配失败时才按长宽比兜底选像素画布（见下方 BestRasterMedia）。
        var matchTolerance = job.RequireExactPaperSize ? ExactMediaToleranceMm : MediaMatchToleranceMm;
        var exact = choices
            .Where(x => x.Error <= matchTolerance)
            .OrderBy(x => x.Error)
            .ThenBy(x => x.IsFullBleed ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (exact != null && (job.RequireExactPaperSize || IsLongPaperName(job.PaperName ?? "")))
        {
            exact.RequiresExactSize = true;
            exact.SizeToleranceMm = matchTolerance;
            return exact;
        }

        if (job.RequireExactPaperSize)
        {
            if (IsRasterPlotDevice(deviceName))
            {
                return BestRasterMedia(choices, targetWidth, targetHeight)
                       ?? throw new InvalidOperationException(
                           $"AutoCAD 的 {deviceName} 未加载精确任意纸张 {targetWidth:0.######} x {targetHeight:0.######} mm；"
                           + "已停止打印，禁止回退到相近或同名纸张。");
            }

            var nearest = string.Join(", ", choices
                .OrderBy(x => x.Error)
                .Take(5)
                .Select(x => $"{x.Name}[{x.WidthMm:0.######}x{x.HeightMm:0.######},误差{x.Error:0.######}]")
                .ToArray());
            throw new InvalidOperationException(
                $"AutoCAD 的 {deviceName} 未加载精确任意纸张 {targetWidth:0.######} x {targetHeight:0.######} mm；"
                + $"已停止打印，禁止回退到相近或同名纸张。介质数={choices.Count}；最近={nearest}");
        }

        var named = BestNamedMedia(choices, job);
        if (named != null)
        {
            named.RequiresExactSize = IsLongPaperName(job.PaperName ?? "");
            return named;
        }

        if (exact != null)
        {
            return exact;
        }

        if (IsRasterPlotDevice(deviceName))
        {
            return BestRasterMedia(choices, targetWidth, targetHeight)
                   ?? throw new InvalidOperationException($"栅格输出设备没有可用像素介质: {deviceName}");
        }

        if (IsLongPaperName(job.PaperName ?? "") && targetWidth > 0 && targetHeight > 0)
        {
            return new MediaChoice
            {
                Name = $"按尺寸匹配 {targetWidth:0.##} x {targetHeight:0.##} mm",
                WidthMm = targetWidth,
                HeightMm = targetHeight,
                Error = 0,
                UseClosestBySize = true,
                RequiresExactSize = true,
                PreferredRotation = targetWidth >= targetHeight ? PlotRotation.Degrees090 : PlotRotation.Degrees000
            };
        }

        var closest = choices
            .OrderBy(x => x.Error)
            .ThenBy(x => x.IsFullBleed ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (closest != null)
        {
            return closest;
        }

        var fallbackName = BestMediaNameByText(names, job) ?? names[0];
        return new MediaChoice
        {
            Name = fallbackName,
            PreferredRotation = job.PaperWidthMm >= job.PaperHeightMm ? PlotRotation.Degrees090 : PlotRotation.Degrees000
        };
    }

    /** GetMediaCatalog：读取或缓存设备介质目录（含物理尺寸）。 */
    private static IReadOnlyList<MediaCatalogItem> GetMediaCatalog(
        PlotSettingsValidator validator,
        Layout layout,
        string deviceName,
        bool forceDeviceReload,
        out bool usedCache)
    {
        if (forceDeviceReload)
        {
            InvalidateMediaCatalog(deviceName);
            try
            {
                // PMP 是 PC3 的附属配置。先让 AutoCAD 全局重新枚举 PC3，再刷新当前 PlotSettings。
                PlotConfigManager.RefreshList(RefreshCode.RefreshPC3DevicesList);
                PlotConfigManager.SetCurrentConfig(Path.GetFileName(deviceName)).RefreshMediaNameList();
            }
            catch
            {
                // 老版本若不支持全局刷新，仍继续执行下面的设备解绑/重绑刷新。
            }
        }

        var cacheKey = BuildMediaCatalogCacheKey(deviceName, layout.ModelType);
        lock (MediaCatalogCacheLock)
        {
            if (MediaCatalogCache.TryGetValue(cacheKey, out var cached))
            {
                usedCache = true;
                return cached;
            }
        }

        usedCache = false;
        using var settings = new PlotSettings(layout.ModelType);
        settings.CopyFrom(layout);
        if (forceDeviceReload)
        {
            try
            {
                validator.SetPlotConfigurationName(settings, "None", null);
                validator.RefreshLists(settings);
            }
            catch
            {
                // 某些 AutoCAD 版本不允许对当前布局设置 None；下面仍会重新绑定目标设备。
            }
        }

        validator.SetPlotConfigurationName(settings, deviceName, null);
        validator.RefreshLists(settings);
        var isRaster = IsRasterPlotDevice(deviceName);
        var paperUnit = isRaster ? PlotPaperUnit.Pixels : PlotPaperUnit.Millimeters;
        var dpi = isRaster ? AcadPlotterInstaller.GetRasterDpi(deviceName) : (X: 100d, Y: 100d);
        validator.SetPlotPaperUnits(settings, paperUnit);

        var catalog = new List<MediaCatalogItem>();
        foreach (var name in validator.GetCanonicalMediaNameList(settings).Cast<string>())
        {
            var size = GetMediaSize(validator, settings, name, paperUnit);
            if (size == null)
            {
                continue;
            }

            var widthMm = isRaster ? PixelsToMillimeters(size.Value.Width, dpi.X) : size.Value.Width;
            var heightMm = isRaster ? PixelsToMillimeters(size.Value.Height, dpi.Y) : size.Value.Height;

            catalog.Add(new MediaCatalogItem
            {
                Name = name,
                WidthMm = widthMm,
                HeightMm = heightMm,
                IsFullBleed = IsFullBleedMedia(name)
            });
        }

        lock (MediaCatalogCacheLock)
        {
            // 只缓存纸张纯数据，CAD 的 PlotSettings 等对象仍按每次打印创建和释放。
            MediaCatalogCache[cacheKey] = catalog;
        }

        return catalog;
    }

    /** BuildMediaCatalogCacheKey：介质缓存键：设备名 + 模型/布局 + PC3 指纹。 */
    private static string BuildMediaCatalogCacheKey(string deviceName, bool modelType)
    {
        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
        // 2027 迁移旧配置后可能同时存在多个同名 PC3。缓存必须跟踪 AutoCAD
        // 实际解析到的完整路径及其真实关联 PMP，不能只指纹化程序假定的根目录副本。
        var devicePath = AcadPlotterInstaller.ResolveActivePlotterPath(deviceName);
        if (string.IsNullOrWhiteSpace(devicePath) && !string.IsNullOrWhiteSpace(plottersDirectory))
            devicePath = Path.Combine(plottersDirectory, deviceName);

        var pmpPath = AcadPlotterInstaller.ReadAttachedPmpPath(devicePath);
        if (string.IsNullOrWhiteSpace(pmpPath) && !string.IsNullOrWhiteSpace(plottersDirectory))
        {
            pmpPath = Path.Combine(
                plottersDirectory,
                "PMP Files",
                Path.GetFileNameWithoutExtension(deviceName) + ".pmp");
        }

        return string.Join("|", deviceName, modelType ? "M" : "P", GetFileFingerprint(devicePath), GetFileFingerprint(pmpPath));
    }

    /** GetFileFingerprint：文件指纹（路径+时间+大小），用于判断 PC3 是否变更。 */
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

    /** InvalidateMediaCatalog：清除指定设备的介质目录缓存。 */
    private static void InvalidateMediaCatalog(string deviceName)
    {
        lock (MediaCatalogCacheLock)
        {
            var prefix = deviceName + "|";
            foreach (var key in MediaCatalogCache.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                MediaCatalogCache.Remove(key);
            }
        }
    }

    /** BestNamedMedia：在候选中按纸名文本匹配最优介质。 */
    private static MediaChoice? BestNamedMedia(IEnumerable<MediaChoice> choices, PlotJob job)
    {
        var paper = job.PaperName ?? "";
        var basePaper = GetBasePaperName(paper);
        return choices
            .Where(x => MediaNameMatchesPaper(x.Name, paper, basePaper))
            .OrderBy(x => x.Error)
            .ThenBy(x => x.IsFullBleed ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /** BestRasterMedia：栅格兜底：按窗口长宽比选最接近的像素介质。 */
    private static MediaChoice? BestRasterMedia(
        IEnumerable<MediaChoice> choices,
        double targetWidth,
        double targetHeight)
    {
        if (targetWidth <= 0d || targetHeight <= 0d)
        {
            return null;
        }

        var targetAspect = Math.Max(targetWidth, targetHeight) / Math.Min(targetWidth, targetHeight);
        // 与 PDF 选纸失败后的兜底：按长宽比找像素画布，横竖与目标纸张一致时不旋转。
        return choices
            .Where(choice => choice.WidthMm > 0d && choice.HeightMm > 0d)
            .OrderBy(choice => Math.Abs(Math.Log(
                (Math.Max(choice.WidthMm, choice.HeightMm)
                 / Math.Min(choice.WidthMm, choice.HeightMm)) / targetAspect)))
            .ThenBy(choice => (choice.WidthMm >= choice.HeightMm) == (targetWidth >= targetHeight) ? 0 : 1)
            .ThenByDescending(choice => choice.WidthMm * choice.HeightMm)
            .Select(choice =>
            {
                choice.PreferredRotation = (choice.WidthMm >= choice.HeightMm) != (targetWidth >= targetHeight)
                    ? PlotRotation.Degrees090
                    : PlotRotation.Degrees000;
                return choice;
            })
            .FirstOrDefault();
    }

    /** IsRasterPlotDevice：是否为 PNG/JPG 等栅格出图设备。 */
    private static bool IsRasterPlotDevice(string deviceName)
    {
        return deviceName.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /** EnsureRequiredMediaSize：校验写回后的介质尺寸是否仍满足任务要求。 */
    private static void EnsureRequiredMediaSize(PlotSettings settings, MediaChoice media, string deviceName)
    {
        if (!media.RequiresExactSize)
        {
            return;
        }

        var size = GetPlotPaperSizeMm(settings, deviceName);
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        var directError = DirectSizeError(size.X, size.Y, media.WidthMm, media.HeightMm);
        var rotatedError = DirectSizeError(size.X, size.Y, media.HeightMm, media.WidthMm);
        var error = Math.Min(directError, rotatedError);
        if (error <= media.SizeToleranceMm)
        {
            return;
        }

        throw new InvalidOperationException(
            $"AutoCAD 输出设备缺少匹配纸张。需要 {media.WidthMm:0.##} x {media.HeightMm:0.##} mm，"
            + $"实际匹配到 {size.X:0.##} x {size.Y:0.##} mm。请在所选 PC3 中添加对应加长纸，或使用支持自定义纸张的输出设备。");
    }

    /** BestMediaNameByText：仅按介质名字符串匹配纸张名称。 */
    private static string? BestMediaNameByText(IEnumerable<string> names, PlotJob job)
    {
        var paper = job.PaperName ?? "";
        var basePaper = GetBasePaperName(paper);
        return names
            .Where(x => MediaNameMatchesPaper(x, paper, basePaper))
            .OrderBy(x => IsFullBleedMedia(x) ? 0 : 1)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /** MediaNameMatchesPaper：介质名是否匹配完整纸名或去长边后缀的基名。 */
    private static bool MediaNameMatchesPaper(string mediaName, string paper, string basePaper)
    {
        if (IsLongPaperName(paper))
        {
            return (!string.IsNullOrWhiteSpace(paper)
                    && mediaName.IndexOf(paper, StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrWhiteSpace(basePaper)
                    && mediaName.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0
                    && IsLongMediaName(mediaName))
                || (!string.IsNullOrWhiteSpace(basePaper)
                    && mediaName.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0
                    && IsLongMediaName(mediaName));
        }

        return (!string.IsNullOrWhiteSpace(paper)
                && mediaName.IndexOf(paper, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(basePaper)
                && mediaName.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(basePaper)
                && mediaName.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /** IsLongPaperName：纸名是否带加长标记（如加长、+_）。 */
    private static bool IsLongPaperName(string paperName)
    {
        return paperName.IndexOf('+') > 0;
    }

    /** GetBasePaperName：去掉加长后缀得到标准纸名基名。 */
    private static string GetBasePaperName(string paperName)
    {
        var plusIndex = paperName.IndexOf('+');
        return plusIndex > 0 ? paperName.Substring(0, plusIndex) : paperName;
    }

    /** IsLongMediaName：介质名是否为加长纸。 */
    private static bool IsLongMediaName(string mediaName)
    {
        return mediaName.IndexOf("+", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("long", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("extend", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("extended", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /** IsFullBleedMedia：是否满版/无边距介质名。 */
    private static bool IsFullBleedMedia(string mediaName)
    {
        return mediaName.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("full bleed", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("无边距", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("满幅", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /** RotationOrder：以首选旋转开头，再枚举其余 0/90/180/270。 */
    private static IEnumerable<PlotRotation> RotationOrder(PlotRotation preferred)
    {
        yield return preferred;

        foreach (var rotation in new[]
        {
            PlotRotation.Degrees000,
            PlotRotation.Degrees090,
            PlotRotation.Degrees270,
            PlotRotation.Degrees180
        })
        {
            if (rotation != preferred)
            {
                yield return rotation;
            }
        }
    }

    /** static：从 PlotSettings 读取当前介质物理尺寸。 */
    private static (double Width, double Height)? GetMediaSize(
        PlotSettingsValidator validator,
        PlotSettings settings,
        string mediaName,
        PlotPaperUnit paperUnit)
    {
        try
        {
            validator.SetCanonicalMediaName(settings, mediaName);
            validator.SetPlotPaperUnits(settings, paperUnit);
            var size = settings.PlotPaperSize;
            if (size.X > 0 && size.Y > 0)
            {
                return (size.X, size.Y);
            }
        }
        catch
        {
        }

        return TryParseMediaSize(mediaName);
    }

    /** GetPlotPaperSizeMm：把当前纸张尺寸统一换算为毫米。 */
    private static Point2d GetPlotPaperSizeMm(PlotSettings settings, string deviceName)
    {
        var size = settings.PlotPaperSize;
        if (!IsRasterPlotDevice(deviceName))
        {
            return size;
        }

        var dpi = AcadPlotterInstaller.GetRasterDpi(deviceName);
        return new Point2d(
            PixelsToMillimeters(size.X, dpi.X),
            PixelsToMillimeters(size.Y, dpi.Y));
    }

    /** PixelsToMillimeters：像素按 DPI 换毫米（25.4/dpi）。 */
    private static double PixelsToMillimeters(double pixels, double dpi)
    {
        return pixels * 25.4d / (dpi > 0d ? dpi : 100d);
    }

    /** DirectSizeError：宽高差绝对值之和，用于尺寸匹配排序。 */
    private static double DirectSizeError(double mediaWidth, double mediaHeight, double targetWidth, double targetHeight)
    {
        return Math.Max(Math.Abs(mediaWidth - targetWidth), Math.Abs(mediaHeight - targetHeight));
    }

    /** static：从介质名解析宽高数字（如 297x210）。 */
    private static (double Width, double Height)? TryParseMediaSize(string name)
    {
        var match = Regex.Match(
            name,
            @"(?<w>\d+(?:\.\d+)?)\s*[_-]?\s*(?:x|X|\u00D7)\s*[_-]?\s*(?<h>\d+(?:\.\d+)?)\s*[_-]?\s*(?<unit>MM|MILLIMETERS?|IN|INCH(?:ES)?)?",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var width = double.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = double.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        if (unit is "IN" or "INCH" or "INCHES")
        {
            width *= 25.4;
            height *= 25.4;
        }

        return (width, height);
    }
}
