using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

public sealed class AppSettings
{
    public string LastOutputDirectory { get; set; } = "";
    public string LastPlotDevice { get; set; } = "";
    public string LastStyleSheet { get; set; } = "";
    public bool RememberLastOutputDirectory { get; set; } = true;
    public string DefaultOutputSubfolder { get; set; } = "PDF";
    public bool AutoScanCurrentDrawing { get; set; }
    public double PaperMatchToleranceMm { get; set; } = 3;
    public bool AllowStandardPaperNameFallback { get; set; } = true;
    public bool ShowPlotProgress { get; set; } = true;
    public bool AddSequenceWhenPdfExists { get; set; } = false;
    public bool MergePdf { get; set; }
    public bool AddFileNameSequence { get; set; }
    public bool LeavePaperMargin { get; set; }
    public double PaperMarginMm { get; set; } = 1;
    public string PdfFileNameSeparator { get; set; } = "_";
    public List<string> PdfFileNameFields { get; set; } = new() { "DrawingNumber", "Title" };
    public bool OpenExternalDwgForPlot { get; set; } = true;
    public double DirectoryIndexWidth { get; set; } = 900;
    public double DirectoryNumberWidth { get; set; } = 3200;
    public double DirectoryTitleWidth { get; set; } = 5200;
    public double DirectoryPaperWidth { get; set; } = 1200;
    public double DirectoryRemarkWidth { get; set; } = 1400;
    public double DirectoryRowHeight { get; set; } = 650;
    public double DirectoryTextHeightRatio { get; set; } = 0.42;
    public string DirectoryTextStyleName { get; set; } = "";
}

public static class AppSettingsStore
{
    // 仅允许 Windows 文件名可安全使用的连接符；明确排除 \\ / : * ? " < > | 等非法字符。
    private static readonly HashSet<string> AllowedFileNameSeparators = new(StringComparer.Ordinal)
    {
        "_", " ", "+", "-", ".", "~", "=", ""
    };

    public static string Path =>
        System.IO.Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return LoadFrom(Path);
            }

            var backupPath = Path + ".bak";
            return File.Exists(backupPath) ? LoadFrom(backupPath) : new AppSettings();
        }
        catch
        {
            var backupPath = Path + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    return LoadFrom(backupPath);
                }
                catch
                {
                }
            }

            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        WriteAtomically(Path, json);
    }

    public static AppSettings Default()
    {
        return new AppSettings();
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultOutputSubfolder))
        {
            settings.DefaultOutputSubfolder = "PDF";
        }

        if (settings.PaperMatchToleranceMm <= 0)
        {
            settings.PaperMatchToleranceMm = 3;
        }

        if (settings.DirectoryIndexWidth <= 0)
        {
            settings.DirectoryIndexWidth = 900;
        }

        if (settings.DirectoryNumberWidth <= 0)
        {
            settings.DirectoryNumberWidth = 3200;
        }

        if (settings.DirectoryTitleWidth <= 0)
        {
            settings.DirectoryTitleWidth = 5200;
        }

        if (settings.DirectoryPaperWidth <= 0)
        {
            settings.DirectoryPaperWidth = 1200;
        }

        if (settings.DirectoryRemarkWidth <= 0)
        {
            settings.DirectoryRemarkWidth = 1400;
        }

        if (settings.DirectoryRowHeight <= 0)
        {
            settings.DirectoryRowHeight = 650;
        }

        if (settings.DirectoryTextHeightRatio <= 0 || settings.DirectoryTextHeightRatio > 0.9)
        {
            settings.DirectoryTextHeightRatio = 0.42;
        }

        settings.DirectoryTextStyleName ??= "";
        if (settings.PaperMarginMm <= 0)
        {
            settings.PaperMarginMm = 1;
        }

        settings.PdfFileNameSeparator = NormalizeFileNameSeparator(settings.PdfFileNameSeparator);
        if (settings.PdfFileNameFields == null || settings.PdfFileNameFields.Count == 0)
        {
            settings.PdfFileNameFields = new List<string> { "DrawingNumber", "Title" };
        }
        else
        {
            // 去重，防止旧版 UI 产生的重复项
            settings.PdfFileNameFields = settings.PdfFileNameFields
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return settings;
    }

    public static string NormalizeFileNameSeparator(string? separator)
    {
        return AllowedFileNameSeparators.Contains(separator ?? "_") ? separator ?? "_" : "_";
    }

    private static void WriteAtomically(string path, string contents)
    {
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        File.WriteAllText(tempPath, contents);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static AppSettings LoadFrom(string path)
    {
        var json = File.ReadAllText(path);
        return Normalize(JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings());
    }
}
