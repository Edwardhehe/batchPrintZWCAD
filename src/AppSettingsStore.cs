using System;
using System.IO;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

public sealed class AppSettings
{
    public string LastOutputDirectory { get; set; } = "";
    public string LastPlotDevice { get; set; } = "";
    public string LastStyleSheet { get; set; } = "";
    public bool RememberLastOutputDirectory { get; set; } = true;
    public string DefaultOutputSubfolder { get; set; } = "PDF";
    public bool AutoScanCurrentDrawing { get; set; } = true;
    public double PaperMatchToleranceMm { get; set; } = 3;
    public bool AllowStandardPaperNameFallback { get; set; } = true;
    public bool ShowPlotProgress { get; set; } = true;
    public bool AddSequenceWhenPdfExists { get; set; } = false;
    public string PdfFileNameSeparator { get; set; } = "_";
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
    public static string Path =>
        System.IO.Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(Path);
            return Normalize(JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings());
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        File.WriteAllText(Path, json);
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
        settings.PdfFileNameSeparator ??= "_";

        return settings;
    }
}
