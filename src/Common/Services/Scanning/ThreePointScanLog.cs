using System;
using System.IO;
using System.Text;

namespace ZwcadBatchPlot;

/// <summary>
/// 三点图签识别的诊断日志，写到 <c>%APPDATA%\AcadBatchPlot\Logs\ThreePointScan.log</c>。
/// 用户在别的机器上「识别不到」时，这份日志能直接看出是框内没有文字、
/// 没有锚点文字，还是三点几何对不上，避免只能靠猜。
/// </summary>
internal static class ThreePointScanLog
{
    private static readonly object Gate = new();

    public static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AcadBatchPlot",
                "Logs");
            return Path.Combine(dir, "ThreePointScan.log");
        }
    }

    /// <summary>写一行，失败绝不影响主流程。</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = FilePath;
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // 日志只留最近约 1MB，超了直接截断重开，避免无限增长。
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
        }
    }

    /// <summary>每次识别开始时打一条分隔，便于区分多轮操作。</summary>
    public static void StartRun(string title)
    {
        Write($"===== {title} =====");
    }
}
