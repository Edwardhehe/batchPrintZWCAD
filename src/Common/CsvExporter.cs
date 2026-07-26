using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZwcadBatchPlot;

public static class CsvExporter
{
    public static void ExportJobs(string path, IEnumerable<PlotJob> jobs, AppSettings? settings = null)
    {
        settings ??= AppSettingsStore.Load();
        var lines = new List<string>
        {
            "是否打印,图号,图名,信息1,信息2,图幅,比例,实际尺寸,输出纸张,块名,空间,文件,输出PDF,识别说明"
        };

        foreach (var job in jobs)
        {
            lines.Add(string.Join(",",
                Csv(job.Selected ? "是" : "否"),
                Csv(job.DrawingNumber),
                Csv(job.Title),
                Csv(job.Info1),
                Csv(job.Info2),
                Csv(FileNameSanitizer.NormalizeLongPaperFraction(
                    OutputPaperNameResolver.Resolve(job, settings.OutputLongPaperSnapToleranceMm),
                    settings.LongPaperNameFormat)),
                Csv(job.ScaleText),
                Csv(job.SizeText),
                Csv(job.PaperSizeText),
                Csv(job.BlockName),
                Csv(job.SpaceName),
                Csv(job.SourceFile),
                Csv(job.OutputPath),
                Csv(job.DetectionNote)));
        }

        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Csv(string value)
    {
        value ??= "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
