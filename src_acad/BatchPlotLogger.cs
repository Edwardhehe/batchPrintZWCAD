using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZwcadBatchPlot;

public static class BatchPlotLogger
{
    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadBatchPlot", "Logs");

    public static string SaveRunLog(IEnumerable<string> lines)
    {
        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, "BatchPlot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return path;
    }

    public static string Format(string level, string message)
    {
        return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
    }
}
