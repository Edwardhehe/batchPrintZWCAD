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

namespace ZwcadBatchPlot;

public static class PlotterService
{
    private const double MediaMatchToleranceMm = 3d;
    private const double ExactMediaToleranceMm = 0.05d;

    public sealed class PlotJobResult
    {
        public PlotJob Job { get; set; } = new();
        public Exception? Error { get; set; }
        public bool Succeeded => Error == null;
    }

    private sealed class MediaChoice
    {
        public string Name { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double Error { get; set; }
        public bool IsFullBleed { get; set; }
        public bool UseClosestBySize { get; set; }
        public bool RequiresExactSize { get; set; }
        public double SizeToleranceMm { get; set; } = MediaMatchToleranceMm;
        public bool FromCachedCatalog { get; set; }
        public PlotRotation PreferredRotation { get; set; }
    }

    private sealed class MediaCatalogItem
    {
        public string Name { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public bool IsFullBleed { get; set; }
    }

    private sealed class CachedMediaCatalogException : Exception
    {
        public CachedMediaCatalogException(Exception innerException)
            : base("缓存的打印纸张配置已失效。", innerException)
        {
        }
    }

    private static readonly object MediaCatalogCacheLock = new();
    private static readonly Dictionary<string, IReadOnlyList<MediaCatalogItem>> MediaCatalogCache =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class ValidatedPlot : IDisposable
    {
        public PlotInfo Info { get; set; } = new();
        public PlotSettings Settings { get; set; } = null!;
        public MediaChoice Media { get; set; } = new();
        public PlotRotation Rotation { get; set; }

        public void Dispose()
        {
            Settings.Dispose();
        }
    }

    public static List<PlotJobResult> PlotMany(
        IReadOnlyList<PlotJob> jobs,
        string deviceName,
        string styleSheet,
        Document currentDocument,
        AppSettings settings,
        Action<PlotJob>? beforeJob = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PlotJobResult>();
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;

        using var variables = PlotSystemVariables.Apply();
        try
        {
            foreach (var group in jobs.GroupBy(job => GetGroupKey(job, currentDocument, settings)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupJobs = group.ToList();
                try
                {
                    if (group.Key == "__CURRENT__")
                    {
                        PlotDocumentJobs(currentDocument, groupJobs, deviceName, styleSheet, beforeJob, results, cancellationToken);
                    }
                    else if (group.Key.StartsWith("__DB__:", StringComparison.OrdinalIgnoreCase))
                    {
                        PlotSideDatabaseJobs(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, beforeJob, results, cancellationToken);
                    }
                    else
                    {
                        PlotOpenedDocumentJobs(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, beforeJob, results, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    foreach (var job in groupJobs.Where(job => !results.Any(x => ReferenceEquals(x.Job, job))))
                    {
                        results.Add(new PlotJobResult { Job = job, Error = ex });
                    }
                }
            }
        }
        finally
        {
            if (oldActive != null && !oldActive.IsDisposed)
            {
                CadApp.DocumentManager.MdiActiveDocument = oldActive;
            }
        }

        return results;
    }

    public static void Plot(PlotJob job, string deviceName, string styleSheet, Document currentDocument, AppSettings settings)
    {
        var result = PlotMany(new[] { job }, deviceName, styleSheet, currentDocument, settings).FirstOrDefault();
        if (result?.Error != null)
        {
            throw result.Error;
        }
    }

    public static void Preview(PlotJob job, string deviceName, string styleSheet, Document currentDocument)
    {
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;
        var doc = IsCurrentDocumentJob(job, currentDocument) ? currentDocument : FindOpenDocument(job.SourceFile);
        var shouldClose = doc == null;
        doc ??= OpenDocument(job.SourceFile);

        using var variables = PlotSystemVariables.Apply();
        try
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
            using (doc.LockDocument())
            {
                // 首次扫描得到的图框信息已可用于预览，避免每次点击预览都重新扫描整张图纸。
                ActivateLayout(doc.Database, job);
                PrepareEditorViewForPlot(doc, job);
                PreviewDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, doc);
            }
        }
        finally
        {
            if (shouldClose)
            {
                CloseWithoutSave(doc);
            }

            if (oldActive != null && !oldActive.IsDisposed)
            {
                CadApp.DocumentManager.MdiActiveDocument = oldActive;
            }
        }
    }

    private static string GetGroupKey(PlotJob job, Document currentDocument, AppSettings settings)
    {
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            return "__CURRENT__";
        }

        var file = string.IsNullOrWhiteSpace(job.SourceFile) ? "" : Path.GetFullPath(job.SourceFile);
        return settings.OpenExternalDwgForPlot ? file : "__DB__:" + file;
    }

    private static void PlotOpenedDocumentJobs(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        var doc = FindOpenDocument(sourceFile);
        var shouldClose = doc == null;
        doc ??= OpenDocument(sourceFile);

        try
        {
            PlotDocumentJobs(doc, jobs, deviceName, styleSheet, beforeJob, results, cancellationToken);
        }
        finally
        {
            if (shouldClose)
            {
                CloseWithoutSave(doc);
            }
        }
    }

    private static void PlotDocumentJobs(
        Document doc,
        IReadOnlyList<PlotJob> jobs,
        string deviceName,
        string styleSheet,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        CadApp.DocumentManager.MdiActiveDocument = doc;

        using (doc.LockDocument())
        {
            RefreshJobsFromDatabase(doc.Database, jobs);
        }

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                using (doc.LockDocument())
                {
                    ActivateLayout(doc.Database, job);
                    PrepareEditorViewForPlot(doc, job);
                    PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, doc);
                }

                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    private static void PlotSideDatabaseJobs(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(sourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        db.ResolveXrefs(true, false);
        RefreshJobsFromDatabase(db, jobs);

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                PlotDatabase(db, Path.GetFileName(sourceFile), job, deviceName, styleSheet, null);
                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    private static void PlotDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, Document? plotDocument)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            throw new InvalidOperationException("未找到可用的输出设备。");
        }

        WaitForPlotIdle();

        var oldDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            var window = GetPlotWindow(job, plotDocument);
            using var plot = CreateValidatedPlot(layout, job, window, deviceName, styleSheet);

            PrepareOutputFile(job.OutputPath);
            RunPlot(plot.Info, documentName, job.OutputPath, job.DrawingNumber);

            tr.Commit();
            WaitForPlotIdle();
            ValidatePlotOutput(job.OutputPath);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldDatabase;
        }
    }

    private static ValidatedPlot CreateValidatedPlot(
        Layout layout,
        PlotJob job,
        Extents2d window,
        string deviceName,
        string styleSheet)
    {
        try
        {
            return CreateValidatedPlotCore(layout, job, window, deviceName, styleSheet);
        }
        catch (CachedMediaCatalogException)
        {
            // PC3/PMP 可能在 CAD 会话中被更新；仅当缓存目录失效时清缓存并完整读取一次。
            InvalidateMediaCatalog(deviceName);
            return CreateValidatedPlotCore(layout, job, window, deviceName, styleSheet);
        }
    }

    private static ValidatedPlot CreateValidatedPlotCore(
        Layout layout,
        PlotJob job,
        Extents2d window,
        string deviceName,
        string styleSheet)
    {
        var validator = PlotSettingsValidator.Current;
        var media = ChooseMedia(validator, layout, deviceName, job, out var usedCachedCatalog);
        media.FromCachedCatalog = usedCachedCatalog;
        var errors = new List<string>();

        var preferredRotation = ResolveWindowRotation(media.PreferredRotation, job, window);
        foreach (var rotation in RotationOrder(preferredRotation))
        {
            var settings = new PlotSettings(layout.ModelType);
            try
            {
                settings.CopyFrom(layout);
                ConfigurePlotSettings(validator, settings, deviceName, styleSheet, media, rotation, window, job);

                var info = new PlotInfo
                {
                    Layout = layout.ObjectId,
                    OverrideSettings = settings
                };

                new PlotInfoValidator
                {
                    MediaMatchingPolicy = MatchingPolicy.MatchEnabled
                }.Validate(info);

                return new ValidatedPlot
                {
                    Info = info,
                    Settings = settings,
                    Media = media,
                    Rotation = rotation
                };
            }
            catch (Exception ex)
            {
                errors.Add($"{media.Name}/{rotation}: {ex.Message}");
                settings.Dispose();
            }
        }

        var failure = new InvalidOperationException(
            "AutoCAD 不接受当前打印设置。"
            + $" 图纸={job.DrawingNumber}_{job.Title};"
            + $" 目标纸张={job.PaperWidthMm:0.##}x{job.PaperHeightMm:0.##}mm;"
            + $" 窗口=({window.MinPoint.X:0.###},{window.MinPoint.Y:0.###})-({window.MaxPoint.X:0.###},{window.MaxPoint.Y:0.###});"
            + " 尝试结果=" + string.Join(" | ", errors));
        if (media.FromCachedCatalog)
        {
            throw new CachedMediaCatalogException(failure);
        }

        throw failure;
    }

    private static void ConfigurePlotSettings(
        PlotSettingsValidator validator,
        PlotSettings settings,
        string deviceName,
        string styleSheet,
        MediaChoice media,
        PlotRotation rotation,
        Extents2d window,
        PlotJob job)
    {
        try
        {
            validator.SetPlotConfigurationName(settings, deviceName, media.UseClosestBySize ? null : media.Name);
        }
        catch
        {
            validator.SetPlotConfigurationName(settings, deviceName, null);
        }

        validator.RefreshLists(settings);
        var paperUnit = IsRasterPlotDevice(deviceName)
            ? PlotPaperUnit.Pixels
            : PlotPaperUnit.Millimeters;
        // AutoCAD 的 PNG/JPG 栅格设备只接受 Pixels；强制设为 Millimeters 会直接抛出 eInvalidInput。
        validator.SetPlotPaperUnits(settings, paperUnit);
        if (media.UseClosestBySize)
        {
            validator.SetClosestMediaName(settings, media.WidthMm, media.HeightMm, PlotPaperUnit.Millimeters, false);
        }
        else
        {
            validator.SetCanonicalMediaName(settings, media.Name);
        }

        EnsureRequiredMediaSize(settings, media, deviceName);
        validator.SetPlotWindowArea(settings, window);
        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
        ConfigurePlotScale(validator, settings, window, job, deviceName);
        validator.SetPlotCentered(settings, true);
        validator.SetPlotRotation(settings, rotation);

        if (!string.IsNullOrWhiteSpace(styleSheet))
        {
            validator.SetCurrentStyleSheet(settings, styleSheet);
        }
    }

    private static void ConfigurePlotScale(
        PlotSettingsValidator validator,
        PlotSettings settings,
        Extents2d window,
        PlotJob job,
        string deviceName)
    {
        if (!job.LeavePaperMargin)
        {
            if (job.UseExactWindowScale)
            {
                SetExactWindowScale(validator, settings, window, deviceName);
                return;
            }

            validator.SetUseStandardScale(settings, true);
            validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
            return;
        }

        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        if (windowWidth <= 0d || windowHeight <= 0d)
            throw new InvalidOperationException("打印窗口尺寸无效，无法计算打印比例。");

        if (job.PaperMarginMm > 0d)
        {
            // ── 扩大纸张模式 ──
            // 纸张已被扩大为 PaperWidthMm+margin*2 × PaperHeightMm+margin*2。
            // 比例按原始图框尺寸（未扩大）计算，使内容居中、四周留白均等。
            var originalShortMm = Math.Min(job.PaperWidthMm, job.PaperHeightMm);
            var windowShortSide = Math.Min(windowWidth, windowHeight);
            if (originalShortMm <= 0d || windowShortSide <= 0d)
                throw new InvalidOperationException("纸张或打印窗口尺寸无效，无法计算扩大纸张留白比例。");
            var scale = originalShortMm / windowShortSide;
            validator.SetUseStandardScale(settings, false);
            validator.SetCustomPrintScale(settings, new CustomScale(scale, 1d));
            return;
        }

        // ── 缩比例模式（PaperMarginMm < 0 时 abs 取值） ──
        var marginMm = Math.Abs(job.PaperMarginMm) > 0d ? Math.Abs(job.PaperMarginMm) : 1d;
        var paperSize = GetPlotPaperSizeMm(settings, deviceName);
        var paperShortSide = Math.Min(paperSize.X, paperSize.Y);
        var windowShort = Math.Min(windowWidth, windowHeight);
        var usableShortSide = paperShortSide - marginMm * 2d;

        if (usableShortSide <= 0d)
            throw new InvalidOperationException("留白值过大导致可用纸张面积为零，请减小留白距离。");

        // 保持原图框窗口不变，只缩小打印比例。扩大窗口会把图框外的相邻对象带入 PDF。
        var scaleReduced = usableShortSide / windowShort;
        validator.SetUseStandardScale(settings, false);
        validator.SetCustomPrintScale(settings, new CustomScale(scaleReduced, 1d));
    }

    private static void SetExactWindowScale(
        PlotSettingsValidator validator,
        PlotSettings settings,
        Extents2d window,
        string deviceName)
    {
        var paper = GetPlotPaperSizeMm(settings, deviceName);
        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        var paperLong = Math.Max(paper.X, paper.Y);
        var paperShort = Math.Min(paper.X, paper.Y);
        var windowLong = Math.Max(windowWidth, windowHeight);
        var windowShort = Math.Min(windowWidth, windowHeight);
        if (paperShort <= 0d || windowShort <= 0d)
            throw new InvalidOperationException("任意纸张或打印窗口尺寸无效，无法计算精确打印比例。");

        var scale = Math.Min(paperLong / windowLong, paperShort / windowShort);
        validator.SetUseStandardScale(settings, false);
        validator.SetCustomPrintScale(settings, new CustomScale(scale, 1d));
    }

    private static MediaChoice ChooseMedia(
        PlotSettingsValidator validator,
        Layout layout,
        string deviceName,
        PlotJob job,
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

    private static string BuildMediaCatalogCacheKey(string deviceName, bool modelType)
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
        // PublishToWeb PNG/JPG 的纸张本质是像素画布，毫米尺寸不会与 A 系列图幅相等。
        // 优先匹配长宽比，再选较大画布，避免用毫米绝对差错误地选到低质量或畸变介质。
        return choices
            .Where(choice => choice.WidthMm > 0d && choice.HeightMm > 0d)
            .OrderBy(choice => Math.Abs(Math.Log(
                (Math.Max(choice.WidthMm, choice.HeightMm)
                 / Math.Min(choice.WidthMm, choice.HeightMm)) / targetAspect)))
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

    private static bool IsRasterPlotDevice(string deviceName)
    {
        return deviceName.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0;
    }

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

    private static bool IsLongPaperName(string paperName)
    {
        return paperName.IndexOf('+') > 0;
    }

    private static string GetBasePaperName(string paperName)
    {
        var plusIndex = paperName.IndexOf('+');
        return plusIndex > 0 ? paperName.Substring(0, plusIndex) : paperName;
    }

    private static bool IsLongMediaName(string mediaName)
    {
        return mediaName.IndexOf("+", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("long", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("extend", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("extended", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsFullBleedMedia(string mediaName)
    {
        return mediaName.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("full bleed", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("无边距", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("满幅", StringComparison.OrdinalIgnoreCase) >= 0;
    }

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

    private static double PixelsToMillimeters(double pixels, double dpi)
    {
        return pixels * 25.4d / (dpi > 0d ? dpi : 100d);
    }

    private static void RunPlot(PlotInfo info, string documentName, string outputPath, string sheetName)
    {
        using var engine = PlotFactory.CreatePublishEngine();
        using var progress = new PlotProgressDialog(false, 1, true);

        var plotStarted = false;
        var documentStarted = false;
        var sheetStarted = false;
        var pageStarted = false;
        var graphicsStarted = false;

        try
        {
            progress.set_PlotMsgString(PlotMessageIndex.DialogTitle, "批量打印");
            progress.set_PlotMsgString(PlotMessageIndex.SheetProgressCaption, sheetName);
            progress.LowerPlotProgressRange = 0;
            progress.UpperPlotProgressRange = 100;
            progress.PlotProgressPos = 0;
            progress.OnBeginPlot();
            progress.IsVisible = true;

            engine.BeginPlot(progress, null);
            plotStarted = true;
            engine.BeginDocument(info, documentName, null, 1, true, outputPath);
            documentStarted = true;
            progress.OnBeginSheet();
            sheetStarted = true;

            using var pageInfo = new PlotPageInfo();
            engine.BeginPage(pageInfo, info, true, null);
            pageStarted = true;
            engine.BeginGenerateGraphics(null);
            graphicsStarted = true;
            engine.EndGenerateGraphics(null);
            graphicsStarted = false;
            engine.EndPage(null);
            pageStarted = false;

            progress.OnEndSheet();
            sheetStarted = false;
            engine.EndDocument(null);
            documentStarted = false;
            progress.PlotProgressPos = 100;
            progress.OnEndPlot();
            engine.EndPlot(null);
            plotStarted = false;
        }
        finally
        {
            if (graphicsStarted)
            {
                TryPlotCleanup(() => engine.EndGenerateGraphics(null));
            }

            if (pageStarted)
            {
                TryPlotCleanup(() => engine.EndPage(null));
            }

            if (sheetStarted)
            {
                TryPlotCleanup(progress.OnEndSheet);
            }

            if (documentStarted)
            {
                TryPlotCleanup(() => engine.EndDocument(null));
            }

            if (plotStarted)
            {
                TryPlotCleanup(progress.OnEndPlot);
                TryPlotCleanup(() => engine.EndPlot(null));
            }
        }
    }

    private static Layout FindLayoutForJob(Transaction tr, Database db, PlotJob job)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        Layout? first = null;
        Layout? model = null;
        Layout? firstPaper = null;
        var availableLayouts = new List<string>();

        foreach (ObjectId id in blockTable)
        {
            var record = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!record.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(record.LayoutId, OpenMode.ForRead);
            availableLayouts.Add(layout.LayoutName);
            first ??= layout;
            if (layout.ModelType)
            {
                model ??= layout;
            }
            else
            {
                firstPaper ??= layout;
            }

            if (string.Equals(record.Name, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout.LayoutName, job.SpaceName, StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        if (!string.IsNullOrWhiteSpace(job.SpaceName))
        {
            throw new InvalidOperationException(
                $"未找到目标布局“{job.SpaceName}”。可用布局: {string.Join(", ", availableLayouts)}。请重新扫描图纸。");
        }

        if (!job.IsPaperSpace && model != null)
        {
            return model;
        }

        if (job.IsPaperSpace && firstPaper != null)
        {
            return firstPaper;
        }

        return first ?? throw new InvalidOperationException("未找到可打印布局。");
    }

    private static void ActivateLayout(Database db, PlotJob job)
    {
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            LayoutManager.Current.CurrentLayout = layout.LayoutName;
            tr.Commit();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法激活目标布局“{job.SpaceName}”，已停止打印以避免输出错误区域。", ex);
        }
    }

    private static void RefreshJobsFromDatabase(Database db, IReadOnlyList<PlotJob> jobs)
    {
        var refreshableJobs = jobs.Where(job => !job.IsManualWindow).ToList();
        if (refreshableJobs.Count == 0)
        {
            return;
        }

        try
        {
            var library = TitleBlockLibraryStore.Load();
            var scanned = TitleBlockScanner.Scan(db, library, refreshableJobs[0].SourceFile);

            foreach (var job in refreshableJobs)
            {
                var refreshed = scanned
                    .Where(x => string.Equals(x.SpaceName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(x =>
                        !string.IsNullOrWhiteSpace(job.BlockHandle)
                        && string.Equals(x.BlockHandle, job.BlockHandle, StringComparison.OrdinalIgnoreCase))
                    ?? scanned.FirstOrDefault(x =>
                        string.Equals(x.SpaceName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase)
                        &&
                        string.Equals(x.DrawingNumber, job.DrawingNumber, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Title, job.Title, StringComparison.OrdinalIgnoreCase))
                    ?? scanned.FirstOrDefault(x =>
                        string.Equals(x.SpaceName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase)
                        && x.MatchIndex == job.MatchIndex);

                if (refreshed == null)
                {
                    throw new InvalidOperationException(
                        $"重新打开图纸后未找到原图框。布局={job.SpaceName}，块={job.BlockName}，句柄={job.BlockHandle}。请重新扫描图纸后再打印。");
                }

                job.MinX = refreshed.MinX;
                job.MinY = refreshed.MinY;
                job.MaxX = refreshed.MaxX;
                job.MaxY = refreshed.MaxY;
                job.IsDcsWindow = false;  // 重新扫描后坐标回到 WCS
                job.PaperName = refreshed.PaperName;
                job.PaperWidthMm = refreshed.PaperWidthMm;
                job.PaperHeightMm = refreshed.PaperHeightMm;
                job.PaperSizeText = refreshed.PaperSizeText;
                job.ScaleText = refreshed.ScaleText;
                job.SizeText = refreshed.SizeText;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("重新扫描已打开图纸失败，已停止打印以避免输出错误窗口。", ex);
        }
    }

    private static PlotRotation ResolveWindowRotation(
        PlotRotation paperRotation,
        PlotJob job,
        Extents2d window)
    {
        var paperWidth = job.PaperWidthMm;
        var paperHeight = job.PaperHeightMm;
        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        if (paperWidth <= 1e-9 || paperHeight <= 1e-9
            || windowWidth <= 1e-9 || windowHeight <= 1e-9)
        {
            return paperRotation;
        }

        var paperIsLandscape = paperWidth >= paperHeight;
        var windowIsLandscape = windowWidth >= windowHeight;
        if (paperIsLandscape == windowIsLandscape)
        {
            return paperRotation;
        }

        return paperRotation switch
        {
            PlotRotation.Degrees000 => PlotRotation.Degrees090,
            PlotRotation.Degrees090 => PlotRotation.Degrees000,
            PlotRotation.Degrees180 => PlotRotation.Degrees270,
            PlotRotation.Degrees270 => PlotRotation.Degrees180,
            _ => paperRotation
        };
    }

    private static void PreviewDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, Document plotDocument)
    {
        WaitForPlotIdle();

        var oldDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            var window = GetPlotWindow(job, plotDocument);
            using var plot = CreateValidatedPlot(layout, job, window, deviceName, styleSheet);
            RunPreview(plot.Info, documentName);
            tr.Commit();
            WaitForPlotIdle();
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldDatabase;
        }
    }

    private static void PrepareEditorViewForPlot(Document doc, PlotJob job)
    {
        // 图纸空间无视图概念，跳过
        // IsDcsWindow：DCS 基于用户原始旋转视图计算，重置视图会破坏坐标系一致性
        if (job.IsPaperSpace || job.IsDcsWindow)
        {
            return;
        }

        try
        {
            var minX = Math.Min(job.MinX, job.MaxX);
            var minY = Math.Min(job.MinY, job.MaxY);
            var maxX = Math.Max(job.MinX, job.MaxX);
            var maxY = Math.Max(job.MinY, job.MaxY);
            var width = Math.Max(maxX - minX, 1);
            var height = Math.Max(maxY - minY, 1);
            var centerX = (minX + maxX) / 2d;
            var centerY = (minY + maxY) / 2d;

            using var view = doc.Editor.GetCurrentView();
            view.ViewDirection = Vector3d.ZAxis;
            view.ViewTwist = 0;
            view.Target = new Point3d(centerX, centerY, 0);
            view.CenterPoint = Point2d.Origin;
            view.Width = width * 1.05;
            view.Height = height * 1.05;
            doc.Editor.SetCurrentView(view);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法规范打印视图，已停止打印以避免输出空白或偏移页面。", ex);
        }
    }

    private static Extents2d GetPlotWindow(PlotJob job, Document? plotDocument)
    {
        // 单张打印已在 BatchPlotCommands.SinglePlotCore 完成 UCS→DCS 全链路变换，直接使用
        if (job.IsDcsWindow)
        {
            return new Extents2d(
                Math.Min(job.MinX, job.MaxX),
                Math.Min(job.MinY, job.MaxY),
                Math.Max(job.MinX, job.MaxX),
                Math.Max(job.MinY, job.MaxY));
        }

        if (job.IsPaperSpace)
        {
            return new Extents2d(
                Math.Min(job.MinX, job.MaxX),
                Math.Min(job.MinY, job.MaxY),
                Math.Max(job.MinX, job.MaxX),
                Math.Max(job.MinY, job.MaxY));
        }

        if (plotDocument != null)
        {
            try
            {
                var view = plotDocument.Editor.GetCurrentView();
                var worldToDisplay = GetWorldToDisplayMatrix(view);
                var points = new[]
                {
                    new Point3d(job.MinX, job.MinY, 0).TransformBy(worldToDisplay),
                    new Point3d(job.MinX, job.MaxY, 0).TransformBy(worldToDisplay),
                    new Point3d(job.MaxX, job.MinY, 0).TransformBy(worldToDisplay),
                    new Point3d(job.MaxX, job.MaxY, 0).TransformBy(worldToDisplay)
                };

                return new Extents2d(
                    points.Min(p => p.X),
                    points.Min(p => p.Y),
                    points.Max(p => p.X),
                    points.Max(p => p.Y));
            }
            catch
            {
            }
        }

        return new Extents2d(
            Math.Min(job.MinX, job.MaxX),
            Math.Min(job.MinY, job.MaxY),
            Math.Max(job.MinX, job.MaxX),
            Math.Max(job.MinY, job.MaxY));
    }

    private static Matrix3d GetWorldToDisplayMatrix(ViewTableRecord view)
    {
        var matrix = Matrix3d.PlaneToWorld(view.ViewDirection);
        matrix = Matrix3d.Displacement(view.Target - Point3d.Origin) * matrix;
        matrix = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * matrix;
        return matrix.Inverse();
    }

    private static void RunPreview(PlotInfo plotInfo, string documentName)
    {
        using var engine = PlotFactory.CreatePreviewEngine((int)PreviewEngineFlags.Plot);
        var plotStarted = false;
        var documentStarted = false;
        var pageStarted = false;
        var graphicsStarted = false;

        try
        {
            engine.BeginPlot(null, null);
            plotStarted = true;
            engine.BeginDocument(plotInfo, documentName, null, 1, false, null);
            documentStarted = true;
            using var pageInfo = new PlotPageInfo();
            engine.BeginPage(pageInfo, plotInfo, true, null);
            pageStarted = true;
            engine.BeginGenerateGraphics(null);
            graphicsStarted = true;
            engine.EndGenerateGraphics(null);
            graphicsStarted = false;
            engine.EndPage(null);
            pageStarted = false;
            engine.EndDocument(null);
            documentStarted = false;
            engine.EndPlot(null);
            plotStarted = false;
        }
        finally
        {
            if (graphicsStarted) TryPlotCleanup(() => engine.EndGenerateGraphics(null));
            if (pageStarted) TryPlotCleanup(() => engine.EndPage(null));
            if (documentStarted) TryPlotCleanup(() => engine.EndDocument(null));
            if (plotStarted) TryPlotCleanup(() => engine.EndPlot(null));
        }
    }

    private static void PrepareOutputFile(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }

    private static Document OpenDocument(string file)
    {
#if ACAD_CORE
        CadApp.DocumentManager.AppContextOpenDocument(file);
        return FindOpenDocument(file)
            ?? CadApp.DocumentManager.MdiActiveDocument
            ?? throw new InvalidOperationException("AutoCAD 已打开 DWG，但插件未能取得文档对象: " + file);
#else
        return CadApp.DocumentManager.Open(file, false);
#endif
    }

    private static void CloseWithoutSave(Document doc)
    {
#if ACAD_CORE
        var close = doc.GetType().GetMethod("CloseAndDiscard");
        if (close == null)
        {
            return;
        }

        var parameters = close.GetParameters();
        if (parameters.Length == 0)
        {
            close.Invoke(doc, null);
        }
        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
        {
            close.Invoke(doc, new object?[] { null });
        }
#else
        doc.CloseAndDiscard();
#endif
    }

    private static Document? FindOpenDocument(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(file);
        foreach (Document doc in CadApp.DocumentManager)
        {
            var name = doc.Database.Filename;
            if (!string.IsNullOrWhiteSpace(name)
                && string.Equals(Path.GetFullPath(name), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return doc;
            }
        }

        return null;
    }

    private static bool IsCurrentDocumentJob(PlotJob job, Document currentDocument)
    {
        var currentFile = currentDocument.Database.Filename;
        if (string.IsNullOrWhiteSpace(currentFile))
        {
            return string.Equals(job.SourceFile, currentDocument.Name, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(job.SourceFile)
            && string.Equals(Path.GetFullPath(job.SourceFile), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase);
    }

    private static void WaitForPlotIdle()
    {
        const int timeoutMs = 10 * 60 * 1000;
        var waited = 0;

        while (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            if (waited >= timeoutMs)
            {
                throw new InvalidOperationException("AutoCAD 当前打印任务长时间未结束。");
            }

            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(250);
            waited += 250;
        }
    }

    private static void TryPlotCleanup(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }

    private static void ValidatePlotOutput(string outputPath)
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new IOException("打印引擎未生成输出文件: " + outputPath);
        }

        if (string.Equals(Path.GetExtension(outputPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            using var stream = File.OpenRead(outputPath);
            var actual = new byte[signature.Length];
            if (stream.Read(actual, 0, actual.Length) != actual.Length || !actual.SequenceEqual(signature))
            {
                throw new InvalidDataException("PNG 已生成但文件格式无效，已按打印失败处理: " + outputPath);
            }
            return;
        }

        if (string.Equals(Path.GetExtension(outputPath), ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(outputPath), ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(outputPath);
            var signature = new byte[3];
            if (stream.Read(signature, 0, signature.Length) != signature.Length
                || signature[0] != 0xFF
                || signature[1] != 0xD8
                || signature[2] != 0xFF)
            {
                throw new InvalidDataException("JPG 已生成但文件格式无效，已按打印失败处理: " + outputPath);
            }
            return;
        }

        if (string.Equals(Path.GetExtension(outputPath), ".dwf", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(outputPath);
            var signature = new byte[6];
            if (stream.Read(signature, 0, signature.Length) != signature.Length
                || !string.Equals(System.Text.Encoding.ASCII.GetString(signature), "(DWF V", StringComparison.Ordinal))
            {
                throw new InvalidDataException("DWF 已生成但文件格式无效，已按打印失败处理: " + outputPath);
            }
            return;
        }

        using var pdf = PdfReader.Open(outputPath, PdfDocumentOpenMode.Import);
        if (pdf.PageCount == 0)
        {
            throw new InvalidDataException("PDF 已生成但没有有效页面，已按打印失败处理: " + outputPath);
        }
    }

    private static double DirectSizeError(double mediaWidth, double mediaHeight, double targetWidth, double targetHeight)
    {
        return Math.Max(Math.Abs(mediaWidth - targetWidth), Math.Abs(mediaHeight - targetHeight));
    }

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

    private sealed class PlotSystemVariables : IDisposable
    {
        private readonly List<(string Name, object? Value)> _oldValues = new();
        private bool _disposed;

        public static PlotSystemVariables Apply()
        {
            var variables = new PlotSystemVariables();
            variables.Set("BACKGROUNDPLOT", 0);
            variables.Set("PUBLISHCOLLATE", 0);
            variables.Set("PDFSHX", 0);
            return variables;
        }

        private void Set(string name, object value)
        {
            try
            {
                var oldValue = CadApp.GetSystemVariable(name);
                CadApp.SetSystemVariable(name, value);
                _oldValues.Add((name, oldValue));
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var i = _oldValues.Count - 1; i >= 0; i--)
            {
                try
                {
                    CadApp.SetSystemVariable(_oldValues[i].Name, _oldValues[i].Value);
                }
                catch
                {
                }
            }
        }
    }
}
