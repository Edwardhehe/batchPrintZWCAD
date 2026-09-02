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
 * @file PlotterService.Documents.cs（ZWCAD）
 * @description 输出文件准备、结果校验与打印空闲等待。
 *
 * 主要功能：
 * - PrepareOutputFile：创建目录、删除旧文件
 * - ValidatePlotOutput：文件存在/非空，PDF 用 PdfSharp 校验
 * - WaitForPlotIdle / TryPlotCleanup / TryCloseWithoutSave：同步与清理
 *
 * 核心代码：
 * - ValidatePlotOutput：防止引擎“成功”但未写出有效文件
 * - TryCloseWithoutSave：侧开文档关闭且不保存
 *
 * 注意：布局激活与已打开图刷新在入口 PlotterService.cs，不在本文件。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** PrepareOutputFile：创建输出目录并删除旧文件。 */
    private static void PrepareOutputFile(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("输出路径缺少目录: " + outputPath);
        }

        Directory.CreateDirectory(directory);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }

    /** ValidatePlotOutput：校验输出文件；PDF 用 PdfSharp 打开验证。 */
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

    /** WaitForPlotIdle：等待 PlotFactory 空闲。 */
    private static void WaitForPlotIdle()
    {
        const int timeoutMs = 10 * 60 * 1000;
        var waited = 0;
        while (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            if (waited >= timeoutMs)
            {
                throw new InvalidOperationException("CAD 当前打印任务长时间未结束。");
            }

            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(250);
            waited += 250;
        }
    }

    /** TryPlotCleanup：安全执行清理回调。 */
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

    /** TryCloseWithoutSave：关闭文档且不保存。 */
    private static void TryCloseWithoutSave(Document doc)
    {
        try
        {
            doc.CloseAndDiscard();
        }
        catch
        {
            // Printing is already complete. Leave the document open rather than risk saving user data.
        }
    }
}
