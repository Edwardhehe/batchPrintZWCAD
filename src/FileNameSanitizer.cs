using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace ZwcadBatchPlot;

public static class FileNameSanitizer
{
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
        Directory.CreateDirectory(directory);
        var clean = Clean(fileNameWithoutExtension);
        var path = Path.Combine(directory, clean + ".pdf");
        var index = 1;
        while (File.Exists(path) || reservedPaths?.Contains(path) == true)
        {
            path = Path.Combine(directory, $"{clean}_{index}.pdf");
            index++;
        }

        reservedPaths?.Add(path);
        return path;
    }
}
