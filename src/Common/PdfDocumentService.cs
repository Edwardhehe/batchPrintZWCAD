using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using iTextSharp.text;
using iTextSharp.text.pdf;

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
            using var stream = new FileStream(temporaryOutput, FileMode.Create);
            var document = new Document();
            var copy = new PdfCopy(document, stream);
            document.Open();

            var totalPages = 0;
            foreach (var file in inputFiles)
            {
                using var reader = new PdfReader(file);
                for (var i = 1; i <= reader.NumberOfPages; i++)
                {
                    var page = copy.GetImportedPage(reader, i);
                    copy.AddPage(page);
                }

                totalPages += reader.NumberOfPages;
                copy.FreeReader(reader);
            }

            if (totalPages == 0)
            {
                document.Close();
                throw new InvalidDataException("待合并 PDF 中没有有效页面。");
            }

            document.Close();

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

        using var reader = new PdfReader(path);
        if (reader.NumberOfPages == 0)
        {
            throw new InvalidDataException("PDF 页面内容为空: " + path);
        }

        try
        {
            var page = reader.GetPageN(1);
            if (page == null || page.GetAsDict(PdfName.CONTENTS) == null)
            {
                throw new InvalidDataException("PDF 页面内容为空: " + path);
            }
        }
        catch
        {
            throw new InvalidDataException("PDF 页面内容为空: " + path);
        }
    }
}
