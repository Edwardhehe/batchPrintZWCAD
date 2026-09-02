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
 * @file PlotterService.Documents.cs（AutoCAD）
 * @description 布局定位、文档开关与输出文件校验。
 *
 * 主要功能：
 * - FindLayoutForJob / ActivateLayout：按任务找到并激活布局
 * - RefreshJobsFromDatabase：已打开图刷新窗口，避免旧坐标出图
 * - PrepareOutputFile / ValidatePlotOutput：落盘前准备与落盘后校验
 * - WaitForPlotIdle / TryPlotCleanup：等待引擎空闲与安全清理
 *
 * 核心代码：
 * - RefreshJobsFromDatabase：重新扫描失败必须中止，防止错误窗口批量输出
 * - ValidatePlotOutput：检查文件存在、非空，PDF 额外用 PdfSharp 打开验证
 *
 * 注意：文档关闭一律不保存；勿在出图路径触发另存。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** FindLayoutForJob：在事务中按布局名/模型空间定位 Layout。 */
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

    /** ActivateLayout：切换数据库当前布局，保证出图读到正确页面设置。 */
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

    /** RefreshJobsFromDatabase：已打开图重新扫描图框窗口；失败则中止以免错窗批量输出。 */
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

    /** PrepareOutputFile：创建输出目录并删除已存在的同名文件。 */
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

    /** OpenDocument：打开图纸文档（用于已打开路径之外的补开）。 */
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

    /** CloseWithoutSave：关闭文档且不保存修改。 */
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

    /** FindOpenDocument：在文档管理器中按路径查找已打开文档。 */
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

    /** IsCurrentDocumentJob：任务源文件是否对应当前活动文档。 */
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

    /** WaitForPlotIdle：轮询直到 PlotFactory 空闲，避免引擎重入。 */
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

    /** TryPlotCleanup：安全执行清理动作，吞掉清理阶段异常。 */
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

    /** ValidatePlotOutput：校验输出文件存在且非空；PDF 再用 PdfSharp 打开验证。 */
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
}
