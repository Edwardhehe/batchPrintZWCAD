using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ZwcadBatchPlot;

/// <summary>
/// 多文件批打清单合并（纯逻辑，无 CAD 依赖，便于单元测试）。
/// 多次“多文件批打”应累加清单：只替换本次成功重扫的「文件 + 空间」，其余已有任务保持原顺序保留。
/// </summary>
public static class MultiFileJobMerge
{
    /// <summary>路径规范化：去空白、转绝对路径（解析 ..\ 等），失败时退回去空白后的原值。</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var trimmed = path!.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    /// <summary>「源文件|空间名」键；调用方用 <see cref="StringComparer.OrdinalIgnoreCase"/> 比较。</summary>
    public static string SpaceKey(string? file, string? space)
        => NormalizePath(file) + "|" + (space ?? "");

    /// <summary>
    /// 任务身份键：与两个批打窗体原有的框选累加去重规则一致（有句柄按句柄，否则按几何窗口），
    /// 仅把源文件路径规范化，避免大小写或 ..\ 写法不同导致同一图框重复入表。
    /// </summary>
    public static string IdentityKey(PlotJob job)
    {
        var file = NormalizePath(job.SourceFile);
        if (!string.IsNullOrWhiteSpace(job.BlockHandle))
        {
            return $"H|{file}|{job.SpaceName}|{job.BlockHandle}";
        }

        return $"G|{file}|{job.SpaceName}|{job.MinX:0.###}|{job.MinY:0.###}|{job.MaxX:0.###}|{job.MaxY:0.###}";
    }

    /// <summary>
    /// 保留不属于本次重扫空间的已有项（原顺序），再追加新扫描结果（按身份键去重）。
    /// <paramref name="rescannedSpaceKeys"/> 只应包含本次扫描成功的文件的勾选空间；扫描失败的文件保留旧结果。
    /// </summary>
    public static List<T> ReplaceRescannedSpaces<T>(
        IEnumerable<T> existing,
        IEnumerable<T> incoming,
        IEnumerable<string> rescannedSpaceKeys,
        Func<T, PlotJob> getJob)
    {
        if (getJob == null)
        {
            throw new ArgumentNullException(nameof(getJob));
        }

        var rescanned = new HashSet<string>(
            rescannedSpaceKeys ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var result = new List<T>();
        foreach (var item in existing ?? Enumerable.Empty<T>())
        {
            var job = getJob(item);
            if (!rescanned.Contains(SpaceKey(job.SourceFile, job.SpaceName)))
            {
                result.Add(item);
            }
        }

        var identities = new HashSet<string>(
            result.Select(item => IdentityKey(getJob(item))),
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in incoming ?? Enumerable.Empty<T>())
        {
            if (identities.Add(IdentityKey(getJob(item))))
            {
                result.Add(item);
            }
        }

        return result;
    }
}
