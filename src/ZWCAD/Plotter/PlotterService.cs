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
 * - 当前文档 / 外部多文件两条分组路径
 * - 布局激活、介质名缓存
 *
 * 核心代码：
 * - GetPlotGroupKey：当前图走 PlotCurrentDocumentGroup，外部图走 PlotExternalFileJobs
 * - PlotCurrentDocumentGroup / PlotExternalFileJobs
 *
 * 注意：具体设备与 PlotSettings 配置在 Pipeline；介质/比例/窗口见其他 partial。
 * 出图窗口以批打/预览已写入的 job 为准，不再出图前重扫图框。
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
            foreach (var group in jobs.GroupBy(job => GetPlotGroupKey(job, currentDocument)))
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

                    PlotExternalFileJobs(
                        groupJobs,
                        groupJobs[0].SourceFile,
                        deviceName,
                        styleSheet,
                        settings,
                        beforeJob,
                        results,
                        cancellationToken);
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

    /** Plot：单张出图：当前图直接 PlotDatabase；外部图走独立打开文档路径。 */
    public static void Plot(PlotJob job, string deviceName, string styleSheet, Document currentDocument, AppSettings settings)
    {
        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        using var transparency = PlotTransparencyOverride.Apply(settings.PlotTransparency);
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            using (currentDocument.LockDocument())
            {
                PlotDatabase(currentDocument.Database, currentDocument.Name, job, deviceName, styleSheet, settings, currentDocument);
            }

            return;
        }

        var results = new List<PlotJobResult>();
        PlotExternalFileJobs(
            new[] { job },
            job.SourceFile,
            deviceName,
            styleSheet,
            settings,
            null,
            results,
            CancellationToken.None);
        var result = results.FirstOrDefault();
        if (result?.Error != null)
        {
            throw result.Error;
        }
    }

    /** Preview：当前图走单文件 Regen；外部图须已在应用上下文打开，否则走独立打开路径。 */
    public static void Preview(PlotJob job, string deviceName, string styleSheet, Document currentDocument)
    {
        var settings = AppSettingsStore.Load();
        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        using var transparency = PlotTransparencyOverride.Apply(settings.PlotTransparency);
        var active = CadApp.DocumentManager.MdiActiveDocument;
        if (active != null && IsCurrentDocumentJob(job, active))
        {
            currentDocument = active;
        }

        if (!IsCurrentDocumentJob(job, currentDocument))
        {
            PreviewExternalFile(job, deviceName, styleSheet);
            return;
        }

        using (currentDocument.LockDocument())
        {
            string? lastSpaceKey = null;
            EnsureSpaceRegenerated(currentDocument, job, ref lastSpaceKey);
            if (!job.IsPaperSpace && !job.IsDcsWindow)
            {
                PrepareEditorViewForPlot(currentDocument, job);
            }

            // 首次扫描得到的图框信息已可用于预览，避免每次点击预览都重新扫描整张图纸。
            PreviewDatabase(currentDocument.Database, currentDocument.Name, job, deviceName, styleSheet, currentDocument);
        }
    }

    /** GetPlotGroupKey：当前打开图一组，外部图按完整路径一组。出图不再走侧库。 */
    private static string GetPlotGroupKey(PlotJob job, Document currentDocument)
    {
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            return "__CURRENT__";
        }

        return string.IsNullOrWhiteSpace(job.SourceFile) ? "" : Path.GetFullPath(job.SourceFile);
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

    /** PlotCurrentDocumentGroup：当前文档组：按模型/布局各 Regen 一次后，按已有窗口逐张 PlotDatabase。 */
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
        string? lastSpaceKey = null;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                using (currentDocument.LockDocument())
                {
                    EnsureSpaceRegenerated(currentDocument, job, ref lastSpaceKey);
                    // 当前图批打原先未对齐视图；UCS/斜图框与单张打印不一致时会打斜。
                    if (!job.IsPaperSpace && !job.IsDcsWindow)
                    {
                        PrepareEditorViewForPlot(currentDocument, job);
                    }

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

    /** PlotSideDatabaseGroup：侧开 Database 组：只读库出图，不占用 UI 文档。出图入口已不再调用。 */
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

    /**
     * EnsureSpaceRegenerated：切换到目标模型/布局后重生成一次。
     * 同一 SpaceName 只执行一次，避免每个图框重复 Regen。
     * 仅用于当前打开图；多文件批打不走此路径。
     */
    private static void EnsureSpaceRegenerated(Document doc, PlotJob job, ref string? lastSpaceKey)
    {
        var spaceKey = string.IsNullOrWhiteSpace(job.SpaceName) ? "__CURRENT__" : job.SpaceName.Trim();
        if (string.Equals(lastSpaceKey, spaceKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ActivateLayout(job);
        doc.Editor.Regen();
        lastSpaceKey = spaceKey;
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
