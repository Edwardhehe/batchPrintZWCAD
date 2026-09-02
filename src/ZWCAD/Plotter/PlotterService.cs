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
 * @file PlotterService.cs（ZWCAD）
 * @description 出图服务入口：批量/单张打印与预览的编排层。
 *
 * 主要功能：
 * - PlotMany / Plot / Preview：对外 API
 * - 当前文档 / 已打开文档 / 侧开 Database 三条分组路径
 * - 布局激活、已打开图窗口刷新、介质名缓存
 *
 * 核心代码：
 * - GetPlotGroupKey：任务分组键，决定打开方式
 * - PlotCurrentDocumentGroup / PlotOpenedDocumentGroup / PlotSideDatabaseGroup
 * - RefreshJobWindowFromOpenedDocument：已打开图重算窗口后再打印
 *
 * 注意：具体设备与 PlotSettings 配置在 Pipeline；介质/比例/窗口见其他 partial。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    private const double ExactMediaToleranceMm = 0.05d;

    /** PlotJobResult：单次出图任务结果：关联作业与异常。 */
    public sealed class PlotJobResult
    {
        public PlotJob Job { get; set; } = new();
        public Exception? Error { get; set; }
        public bool Succeeded => Error == null;
    }

    /** MediaSelection：介质选择结果：介质名及是否需要旋转。 */
    private sealed class MediaSelection
    {
        public string Name { get; set; } = "";
        public bool NeedsRotation { get; set; }
    }

    private static readonly object MediaNameCacheLock = new();
    private static readonly Dictionary<string, IReadOnlyList<string>> MediaNameCache =
        new(StringComparer.OrdinalIgnoreCase);

    /** PlotMany：批量出图入口：按分组键调度当前/已打开/侧开路径。 */
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

        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        using var transparency = PlotTransparencyOverride.Apply(settings.PlotTransparency);
        try
        {
            foreach (var group in jobs.GroupBy(job => GetPlotGroupKey(job, currentDocument, settings)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupJobs = group.ToList();
                try
                {
                    if (group.Key == "__CURRENT__")
                    {
                        PlotCurrentDocumentGroup(groupJobs, currentDocument, deviceName, styleSheet, settings, beforeJob, results, cancellationToken);
                        continue;
                    }

                    if (group.Key.StartsWith("__DB__:", StringComparison.OrdinalIgnoreCase))
                    {
                        PlotSideDatabaseGroup(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, settings, beforeJob, results, cancellationToken);
                        continue;
                    }

                    PlotOpenedDocumentGroup(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, settings, beforeJob, results, cancellationToken);
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

    /** Plot：单张出图：按源文件选择当前文档或打开文档路径。 */
    public static void Plot(PlotJob job, string deviceName, string styleSheet, Document currentDocument, AppSettings settings)
    {
        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        using var transparency = PlotTransparencyOverride.Apply(settings.PlotTransparency);
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            using (currentDocument.LockDocument())
            {
                ActivateLayout(job);
                RefreshJobWindowFromOpenedDocument(currentDocument.Database, job);
                PlotDatabase(currentDocument.Database, currentDocument.Name, job, deviceName, styleSheet, settings, currentDocument);
            }

            return;
        }

        if (!settings.OpenExternalDwgForPlot)
        {
            using var db = new Database(false, true);
            db.ReadDwgFile(job.SourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
            db.CloseInput(true);
            db.ResolveXrefs(true, false);
            PlotDatabase(db, Path.GetFileName(job.SourceFile), job, deviceName, styleSheet, settings);
            return;
        }

        PlotOpenedDocument(job, deviceName, styleSheet, settings);
    }

    /** Preview：单张预览：激活布局后 PreviewDatabase。 */
    public static void Preview(PlotJob job, string deviceName, string styleSheet, Document currentDocument)
    {
        var settings = AppSettingsStore.Load();
        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        using var transparency = PlotTransparencyOverride.Apply(settings.PlotTransparency);
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;
        var doc = IsCurrentDocumentJob(job, currentDocument) ? currentDocument : FindOpenDocument(job.SourceFile);
        var shouldClose = doc == null;
        doc ??= CadApp.DocumentManager.Open(job.SourceFile, false);

        try
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
            using (doc.LockDocument())
            {
                ActivateLayout(job);
                // 首次扫描得到的图框信息已可用于预览，避免每次点击预览都重新扫描整张图纸。
                PrepareEditorViewForPlot(doc, job);
                PreviewDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, doc);
            }
        }
        finally
        {
            if (shouldClose)
            {
                TryCloseWithoutSave(doc);
            }

            if (oldActive != null && !oldActive.IsDisposed)
            {
                CadApp.DocumentManager.MdiActiveDocument = oldActive;
            }
        }
    }

    /** GetPlotGroupKey：生成分组键：当前文档 / 侧开 Database / 已打开路径。 */
    private static string GetPlotGroupKey(PlotJob job, Document currentDocument, AppSettings settings)
    {
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            return "__CURRENT__";
        }

        var file = string.IsNullOrWhiteSpace(job.SourceFile) ? "" : Path.GetFullPath(job.SourceFile);
        return settings.OpenExternalDwgForPlot ? file : "__DB__:" + file;
    }

    /** EnsureTextGeometryMode：按设置处理文本转几何相关模式。 */
    private static void EnsureTextGeometryMode(string deviceName, bool convertToGeometry)
    {
        WaitForPlotIdle();
        var result = AcadPlotterInstaller.ApplyTextGeometryMode(deviceName, convertToGeometry);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    /** PlotCurrentDocumentGroup：当前文档组：激活布局后逐张 PlotDatabase。 */
    private static void PlotCurrentDocumentGroup(
        IReadOnlyList<PlotJob> jobs,
        Document currentDocument,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        CadApp.DocumentManager.MdiActiveDocument = currentDocument;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                using (currentDocument.LockDocument())
                {
                    ActivateLayout(job);
                    RefreshJobWindowFromOpenedDocument(currentDocument.Database, job);
                    PrepareEditorViewForPlot(currentDocument, job);
                    PlotDatabase(currentDocument.Database, currentDocument.Name, job, deviceName, styleSheet, settings, currentDocument);
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

    /** PlotOpenedDocumentGroup：已打开文档组：切活动文档、刷新窗口后出图。 */
    private static void PlotOpenedDocumentGroup(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        var doc = FindOpenDocument(sourceFile);
        var shouldClose = doc == null;
        doc ??= CadApp.DocumentManager.Open(sourceFile, false);

        try
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    beforeJob?.Invoke(job);
                    using (doc.LockDocument())
                    {
                        ActivateLayout(job);
                        RefreshJobWindowFromOpenedDocument(doc.Database, job);
                        PrepareEditorViewForPlot(doc, job);
                        PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, settings, doc);
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
        finally
        {
            if (shouldClose)
            {
                TryCloseWithoutSave(doc);
            }
        }
    }

    /** PlotSideDatabaseGroup：侧开 Database 组：只读库出图，不占用 UI 文档。 */
    private static void PlotSideDatabaseGroup(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(sourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        db.ResolveXrefs(true, false);

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                PlotDatabase(db, Path.GetFileName(sourceFile), job, deviceName, styleSheet, settings);
                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    /** PlotOpenedDocument：打开或定位文档后执行一组任务。 */
    private static void PlotOpenedDocument(PlotJob job, string deviceName, string styleSheet, AppSettings settings)
    {
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;
        var doc = FindOpenDocument(job.SourceFile);
        var shouldClose = doc == null;
        doc ??= CadApp.DocumentManager.Open(job.SourceFile, false);

        try
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
            using (doc.LockDocument())
            {
                ActivateLayout(job);
                RefreshJobWindowFromOpenedDocument(doc.Database, job);
                PrepareEditorViewForPlot(doc, job);
                PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, settings, doc);
            }
        }
        finally
        {
            if (oldActive != null && !oldActive.IsDisposed)
            {
                CadApp.DocumentManager.MdiActiveDocument = oldActive;
            }

            if (shouldClose)
            {
                TryCloseWithoutSave(doc);
            }
        }
    }

    /** ActivateLayout：切换当前文档布局。 */
    private static void ActivateLayout(PlotJob job)
    {
        if (string.IsNullOrWhiteSpace(job.SpaceName))
        {
            return;
        }

        try
        {
            LayoutManager.Current.CurrentLayout = job.SpaceName;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法激活目标布局“{job.SpaceName}”，已停止打印以避免输出错误区域。", ex);
        }
    }

    /** RefreshJobWindowFromOpenedDocument：已打开图重算图框窗口，避免旧坐标出图。 */
    private static void RefreshJobWindowFromOpenedDocument(Database db, PlotJob job)
    {
        if (job.IsManualWindow)
        {
            return;
        }

        var library = TitleBlockLibraryStore.Load();
        var candidates = TitleBlockScanner.Scan(db, library, job.SourceFile)
            .Where(x => string.Equals(x.SpaceName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var refreshed = candidates.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(job.BlockHandle)
                && string.Equals(x.BlockHandle, job.BlockHandle, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(x =>
                string.Equals(x.DrawingNumber, job.DrawingNumber, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Title, job.Title, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(x => x.MatchIndex == job.MatchIndex);

        if (refreshed == null)
        {
            throw new InvalidOperationException(
                $"重新打开图纸后未找到原图框。布局={job.SpaceName}，块={job.BlockName}，句柄={job.BlockHandle}。请重新扫描图纸后再打印。");
        }

        job.MinX = refreshed.MinX;
        job.MinY = refreshed.MinY;
        job.MaxX = refreshed.MaxX;
        job.MaxY = refreshed.MaxY;
        job.IsDcsWindow = false;  // 重新扫描后坐标回到 WCS，清除 DCS 标记
        job.PaperName = refreshed.PaperName;
        job.ScaleText = refreshed.ScaleText;
        job.SizeText = refreshed.SizeText;
        job.PaperSizeText = refreshed.PaperSizeText;
        job.PaperWidthMm = refreshed.PaperWidthMm;
        job.PaperHeightMm = refreshed.PaperHeightMm;
    }

    /** FindOpenDocument：按路径查找已打开文档。 */
    private static Document? FindOpenDocument(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(file);
        foreach (Document doc in CadApp.DocumentManager)
        {
            var docFile = doc.Database.Filename;
            if (!string.IsNullOrWhiteSpace(docFile)
                && string.Equals(Path.GetFullPath(docFile), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return doc;
            }
        }

        return null;
    }

    /** IsCurrentDocumentJob：任务是否属于当前活动文档。 */
    private static bool IsCurrentDocumentJob(PlotJob job, Document currentDocument)
    {
        var currentFile = currentDocument.Database.Filename;
        if (string.IsNullOrWhiteSpace(currentFile))
        {
            return string.Equals(job.SourceFile, currentDocument.Name, StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(job.SourceFile))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(job.SourceFile), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase);
    }
}
