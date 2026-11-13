using System;

namespace ZwcadBatchPlot;

/// <summary>签章/二维码诊断日志，并入现有 BatchPlotLogger（受「生成打印日志」开关控制）。</summary>
internal static class PluginLog
{
    public static void Error(string category, string message, Exception? ex = null)
    {
        var text = ex == null ? message : message + " " + ex.GetType().Name;
        BatchPlotLogger.AddPending("ERROR", "[" + category + "] " + text);
    }

    public static void Run(string message)
    {
        BatchPlotLogger.AddPending("INFO", message);
    }

    public static void Timing(string message)
    {
        BatchPlotLogger.AddPending("INFO", message);
    }
}
