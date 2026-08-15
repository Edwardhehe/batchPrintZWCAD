using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZwcadBatchPlot;

public static class BatchPlotLogger
{
    /// <summary>
    /// 所有插件日志文件共用一个总开关。调用点仍可准备诊断信息，但关闭时不得创建目录或写文件。
    /// </summary>
    public static bool IsEnabled => AppSettingsStore.Load().GeneratePrintLog;

    public static string LogDirectory =>
#if AUTOCAD
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadBatchPlot", "Logs");
#else
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZwcadBatchPlot", "Logs");
#endif

    public static string SaveRunLog(IEnumerable<string> lines)
    {
        if (!IsEnabled)
        {
            return "";
        }

        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, "BatchPlot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log");
        File.WriteAllLines(path, lines, Encoding.UTF8);
        return path;
    }

    public static string Format(string level, string message)
    {
        return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
    }
}
