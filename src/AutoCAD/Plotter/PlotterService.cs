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
 * @file PlotterService.cs（AutoCAD）
 * @description 出图服务入口：批量/单张打印与预览的编排层。
 *
 * 主要功能：
 * - PlotMany / Plot / Preview：对外 API
 * - 按文档分组调度（当前打开图 / 外部多文件）
 * - 文本转几何、介质目录缓存等跨任务共享状态
 *
 * 核心代码：
 * - GetGroupKey：当前图走 PlotDocumentJobs，外部图走 PlotExternalFileJobs
 * - PlotDocumentJobs：单文件路径；PlotExternalFileJobs：多文件独立路径
 *
 * 注意：本文件不做 PlotSettings 细节配置；窗口、比例、介质见同目录其他 partial。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    private const double MediaMatchToleranceMm = 3d;
    private const double ExactMediaToleranceMm = 0.05d;

    /** PlotJobResult：单次出图任务结果：关联作业与异常；Succeeded 表示无 Error。 */
    public sealed class PlotJobResult
    {
        public PlotJob Job { get; set; } = new();
        public Exception? Error { get; set; }
        public bool Succeeded => Error == null;
    }

    /** MediaChoice：介质选择结果：名称、毫米尺寸、旋转偏好及是否要求精确尺寸。 */
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

    /** MediaCatalogItem：介质目录缓存项：名称与物理宽高（毫米）。 */
    private sealed class MediaCatalogItem
    {
        public string Name { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public bool IsFullBleed { get; set; }
    }

    /** CachedMediaCatalogException：缓存的介质目录失效时抛出，触发清缓存并重读 PC3。 */
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

    /** ValidatedPlot：已通过 PlotInfoValidator 的打印包：Info / Settings / Media / Rotation。 */
    private sealed class ValidatedPlot : IDisposable
    {
        public PlotInfo Info { get; set; } = new();
        public PlotSettings Settings { get; set; } = null!;
        public MediaChoice Media { get; set; } = new();
        public PlotRotation Rotation { get; set; }

        /** Dispose：释放 PlotSettings。 */
        public void Dispose()
        {
            Settings.Dispose();
        }
    }

    /** PlotMany：批量出图入口：按文档分组调度，收集每张图的成功/失败结果。 */
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
        using var variables = PlotSystemVariables.Apply(settings.PlotTransparency);
        try
        {
            foreach (var group in jobs.GroupBy(job => GetGroupKey(job, currentDocument)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupJobs = group.ToList();
                try
                {
                    if (group.Key == "__CURRENT__")
                    {
                        PlotDocumentJobs(currentDocument, groupJobs, deviceName, styleSheet, settings, beforeJob, results, cancellationToken);
                    }
                    else
                    {
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

    /** Plot：单张出图：走当前文档路径的 PlotDatabase。 */
    public static void Plot(PlotJob job, string deviceName, string styleSheet, Document currentDocument, AppSettings settings)
    {
        var result = PlotMany(new[] { job }, deviceName, styleSheet, currentDocument, settings).FirstOrDefault();
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
        using var variables = PlotSystemVariables.Apply(settings.PlotTransparency);
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

    /** GetGroupKey：当前打开图一组，外部图按完整路径一组。出图不再走侧库。 */
    private static string GetGroupKey(PlotJob job, Document currentDocument)
    {
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            return "__CURRENT__";
        }

        return string.IsNullOrWhiteSpace(job.SourceFile) ? "" : Path.GetFullPath(job.SourceFile);
    }

    /** EnsureTextGeometryMode：PDF 出图前按设置切换文本转几何相关模式。 */
    private static void EnsureTextGeometryMode(string deviceName, bool convertToGeometry)
    {
        WaitForPlotIdle();
        var result = AcadPlotterInstaller.ApplyTextGeometryMode(deviceName, convertToGeometry);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }

        if (result.Changed)
        {
            // PC3/PMP 内容改变后，纸张目录缓存也必须失效；否则宿主仍可能沿用旧设备快照。
            InvalidateMediaCatalog(deviceName);
        }
    }

    /** PlotDocumentJobs：当前文档组：按模型/布局各 Regen 一次后，按已有窗口逐张 PlotDatabase。 */
    private static void PlotDocumentJobs(
        Document doc,
        IReadOnlyList<PlotJob> jobs,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        // 同一模型或同一布局只激活并重生成一次；图框窗口仍用批打已准备好的数据。
        string? lastSpaceKey = null;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                using (doc.LockDocument())
                {
                    EnsureSpaceRegenerated(doc, job, ref lastSpaceKey);
                    // 当前图批打原先未对齐视图；UCS/斜图框与单张打印不一致时会打斜。
                    if (!job.IsPaperSpace && !job.IsDcsWindow)
                    {
                        PrepareEditorViewForPlot(doc, job);
                    }

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

    /** PlotSideDatabaseJobs：侧开 Database 组：只读库出图，不占用 UI 文档。出图入口已不再调用。 */
    private static void PlotSideDatabaseJobs(
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
                PlotDatabase(db, Path.GetFileName(sourceFile), job, deviceName, styleSheet, settings, null);
                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }
}
