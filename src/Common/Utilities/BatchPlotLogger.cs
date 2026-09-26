using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private const int MaxPendingLines = 500;
    private static readonly object PendingSync = new();
    private static readonly List<string> PendingLines = new();

    /// <summary>
    /// 出图管道（PlotterService）内部没有窗体日志列表，诊断行先暂存于此，
    /// 由批打窗体写日志时（AppendLog / SaveRunLog）按时间顺序并入本次运行日志。日志总开关关闭时不暂存。
    /// </summary>
    public static void AddPending(string level, string message)
    {
        if (!IsEnabled)
        {
            return;
        }

        lock (PendingSync)
        {
            PendingLines.Add(Format(level, message));
            if (PendingLines.Count > MaxPendingLines)
            {
                PendingLines.RemoveRange(0, PendingLines.Count - MaxPendingLines);
            }
        }
    }

    /// <summary>取出并清空暂存的管道诊断行。</summary>
    public static IReadOnlyList<string> DrainPending()
    {
        lock (PendingSync)
        {
            if (PendingLines.Count == 0)
            {
                return Array.Empty<string>();
            }

            var drained = PendingLines.ToList();
            PendingLines.Clear();
            return drained;
        }
    }

    public static string SaveRunLog(IEnumerable<string> lines)
    {
        if (!IsEnabled)
        {
            return "";
        }

        Directory.CreateDirectory(LogDirectory);
        var path = Path.Combine(LogDirectory, "BatchPlot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log");
        File.WriteAllLines(path, lines.Concat(DrainPending()).ToList(), Encoding.UTF8);
        return path;
    }

    public static string Format(string level, string message)
    {
        return $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
    }
}
