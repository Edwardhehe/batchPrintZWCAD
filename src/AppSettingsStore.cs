using System;
using System.IO;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

public sealed class AppSettings
{
    public string LastOutputDirectory { get; set; } = "";
    public string LastPlotDevice { get; set; } = "";
    public string LastStyleSheet { get; set; } = "";
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
            return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
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
}
