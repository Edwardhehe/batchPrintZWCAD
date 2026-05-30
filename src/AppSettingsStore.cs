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
    public bool OpenExternalDwgForPlot { get; set; } = true;
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

        return settings;
    }
}
