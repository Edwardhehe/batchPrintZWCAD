using System;
using System.IO;
using System.Text;
using System.Threading;

namespace ZwcadBatchPlot;

/// <summary>
/// 设置 / 图框库等小型 JSON 文件的原子写入（纯 .NET，无 CAD 依赖）。
/// 先写同目录下唯一临时文件，再用 File.Replace（保留 .bak）或 File.Move 换入；
/// 杀毒软件、另一个 CAD 实例短暂占用目标文件时按 50/100/200/400/800ms 退避重试，
/// 仍失败时退回“备份后直接覆盖复制”，最后总会清理临时文件。
/// </summary>
public static class AtomicFileWriter
{
    private static readonly int[] RetryDelaysMs = { 50, 100, 200, 400, 800 };

    /// <summary>与 File.WriteAllText(path, contents) 默认编码一致：UTF-8 无 BOM。</summary>
    private static readonly Encoding DefaultEncoding = new UTF8Encoding(false, true);

    /// <summary>仅供单元测试替换 File.Replace，以确定性地模拟“重试窗口内始终被占用”。</summary>
    internal static Action<string, string, string>? ReplaceOverrideForTests { get; set; }

    public static void Write(string path, string contents, Encoding? encoding = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("文件路径不能为空。", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("文件路径无效: " + path, nameof(path));
        var backupPath = fullPath + ".bak";
        var tempPath = Path.Combine(
            directory,
            Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(tempPath, contents ?? "", encoding ?? DefaultEncoding);
            for (var attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
            {
                if (attempt > 0)
                {
                    Thread.Sleep(RetryDelaysMs[attempt - 1]);
                }

                try
                {
                    if (File.Exists(fullPath))
                    {
                        Replace(tempPath, fullPath, backupPath);
                    }
                    else
                    {
                        File.Move(tempPath, fullPath);
                    }

                    return;
                }
                catch (Exception ex) when (IsRetryable(ex, tempPath))
                {
                    // 目标文件被短暂占用（如 IOException 1176/32）；稍后重试。
                }
            }

            // 重试窗口内始终无法换入：尽力备份当前文件，再直接覆盖复制。复制失败时异常交给调用方。
            try
            {
                if (File.Exists(fullPath))
                {
                    File.Copy(fullPath, backupPath, true);
                }
            }
            catch
            {
            }

            File.Copy(tempPath, fullPath, true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private static void Replace(string tempPath, string fullPath, string backupPath)
    {
        var hook = ReplaceOverrideForTests;
        if (hook != null)
        {
            hook(tempPath, fullPath, backupPath);
            return;
        }

        File.Replace(tempPath, fullPath, backupPath, true);
    }

    private static bool IsRetryable(Exception error, string tempPath)
    {
        // 临时文件已不存在时重试没有意义。
        if (!File.Exists(tempPath))
        {
            return false;
        }

        switch (error)
        {
            case DirectoryNotFoundException:
                return false;
            case FileNotFoundException:
                // 另一写入者在 Exists 与 Replace 之间换走了目标文件；临时文件仍在，下一轮走 Move 分支。
                return true;
            case IOException:
            case UnauthorizedAccessException:
                return true;
            default:
                return false;
        }
    }
}
