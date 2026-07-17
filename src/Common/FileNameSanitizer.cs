using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace ZwcadBatchPlot;

public static class FileNameSanitizer
{
    private const int DefaultMaxFileNameLength = 120;
    private const int LegacyMaxPathLength = 240;
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public static string Clean(string value)
    {
        var cleaned = new string((value ?? "").Select(ch => InvalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "未命名" : cleaned;
    }

    public static string MakeUnique(string directory, string fileNameWithoutExtension)
    {
        return MakeUnique(directory, fileNameWithoutExtension, null);
    }

    public static string MakeUnique(string directory, string fileNameWithoutExtension, ISet<string>? reservedPaths)
    {
        return MakeUnique(directory, fileNameWithoutExtension, reservedPaths, true);
    }

    public static string MakeUnique(
        string directory,
        string fileNameWithoutExtension,
        ISet<string>? reservedPaths,
        bool avoidExistingFile,
        string extension = ".pdf",
        bool createDirectory = true)
    {
        if (createDirectory)
        {
            Directory.CreateDirectory(directory);
        }
        var clean = TrimFileNameForPath(Clean(fileNameWithoutExtension), directory, extension);
        var path = Path.Combine(directory, clean + extension);
        var index = 1;
        while ((avoidExistingFile && File.Exists(path)) || reservedPaths?.Contains(path) == true)
        {
            var suffix = "_" + index;
            var maxNameLength = GetMaxFileNameLength(directory, extension);
            var uniqueName = TrimToLength(clean, Math.Max(1, maxNameLength - suffix.Length)) + suffix;
            path = Path.Combine(directory, uniqueName + extension);
            index++;
        }

        reservedPaths?.Add(path);
        return path;
    }

    private static string TrimFileNameForPath(string value, string directory, string extension = ".pdf")
    {
        return TrimToLength(value, GetMaxFileNameLength(directory, extension));
    }

    private static int GetMaxFileNameLength(string directory, string extension = ".pdf")
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return DefaultMaxFileNameLength;
        }

        return Math.Min(
            DefaultMaxFileNameLength,
            Math.Max(1, LegacyMaxPathLength - Path.GetFullPath(directory).Length - extension.Length - 1));
    }

    private static string TrimToLength(string value, int maxLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? "未命名" : value.Trim();
        return value.Length <= maxLength ? value : value.Substring(0, maxLength).Trim();
    }

    /// <summary>
    /// 根据用户配置的字段键列表，从 PlotJob 中提取对应的值组成文件名片段。
    /// 空值自动跳过，确保最终文件名不含多余分隔符。
    /// sequenceNumber > 0 时 "Sequence" 键生效，按 sequenceDigits 补零。
    /// </summary>
    public static List<string> GetFileNameParts(PlotJob job, List<string> fieldKeys, int sequenceNumber = 0, int sequenceDigits = 2)
    {
        var parts = new List<string>();
        foreach (var key in fieldKeys)
        {
            var value = key switch
            {
                "DrawingNumber" => job.DrawingNumber,
                "Title" => job.Title,
                "Date" => job.Date,
                "Revision" => job.Revision,
                "Phase" => job.Phase,
                "Info1" => job.Info1,
                "Info2" => job.Info2,
                "PaperName" => job.PaperName,
                "Sequence" => sequenceNumber > 0 ? sequenceNumber.ToString($"D{Math.Max(1, Math.Min(10, sequenceDigits))}") : "",
                _ => ""
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value.Trim());
            }
        }

        return parts;
    }
}
