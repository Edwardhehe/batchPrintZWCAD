using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ZwcadBatchPlot;

public static class PdfDocumentService
{
    public static void Merge(IReadOnlyList<string> inputFiles, string outputPath)
    {
        if (inputFiles == null || inputFiles.Count == 0)
        {
            throw new InvalidOperationException("没有可合并的 PDF 文件。");
        }

        foreach (var file in inputFiles)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                throw new FileNotFoundException("待合并的 PDF 文件不存在。", file);
            }
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("合并 PDF 输出路径无效: " + outputPath);
        Directory.CreateDirectory(directory);

        var temporaryOutput = fullOutputPath + "." + Guid.NewGuid().ToString("N") + ".tmp.pdf";
        try
        {
            using (var output = new PdfDocument())
            {
                output.PageLayout = PdfPageLayout.SinglePage;
                foreach (var file in inputFiles)
                {
                    using var input = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                    PdfPage? firstPage = null;
                    foreach (var page in input.Pages)
                    {
                        var newPage = output.AddPage(page);
                        firstPage ??= newPage;
                    }

                    if (firstPage != null)
                    {
                        output.Outlines.Add(
                            Path.GetFileNameWithoutExtension(file),
                            firstPage,
                            true,
                            PdfOutlineStyle.Bold);
                    }
                }

                if (output.PageCount == 0)
                {
                    throw new InvalidDataException("待合并 PDF 中没有有效页面。");
                }

                output.Save(temporaryOutput);
            }

            Validate(temporaryOutput);
            if (File.Exists(fullOutputPath))
            {
                File.Delete(fullOutputPath);
            }

            File.Move(temporaryOutput, fullOutputPath);
        }
        finally
        {
            if (File.Exists(temporaryOutput))
            {
                File.Delete(temporaryOutput);
            }
        }
    }

    public static void Validate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new IOException("PDF 文件不存在或为空: " + path);
        }

        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        if (pdf.PageCount == 0 || !pdf.Pages.Cast<PdfPage>().Any(page => page.Contents.Elements.Count > 0))
        {
            throw new InvalidDataException("PDF 页面内容为空: " + path);
        }
    }
}
