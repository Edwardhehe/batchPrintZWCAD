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

    public static string MakeUnique(string directory, string fileNameWithoutExtension, ISet<string>? reservedPaths, bool avoidExistingFile)
    {
        Directory.CreateDirectory(directory);
        var clean = TrimFileNameForPath(Clean(fileNameWithoutExtension), directory);
        var path = Path.Combine(directory, clean + ".pdf");
        var index = 1;
        while ((avoidExistingFile && File.Exists(path)) || reservedPaths?.Contains(path) == true)
        {
            var suffix = "_" + index;
            var uniqueName = TrimToLength(clean, Math.Max(1, DefaultMaxFileNameLength - suffix.Length)) + suffix;
            path = Path.Combine(directory, uniqueName + ".pdf");
            index++;
        }

        reservedPaths?.Add(path);
        return path;
    }

    private static string TrimFileNameForPath(string value, string directory)
    {
        var maxNameLength = DefaultMaxFileNameLength;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            maxNameLength = Math.Min(maxNameLength, Math.Max(20, LegacyMaxPathLength - directory.Length - ".pdf".Length - 1));
        }

        return TrimToLength(value, maxNameLength);
    }

    private static string TrimToLength(string value, int maxLength)
    {
        value = string.IsNullOrWhiteSpace(value) ? "未命名" : value.Trim();
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value.Substring(0, maxLength).Trim();
    }
}
