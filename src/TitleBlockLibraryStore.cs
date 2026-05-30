using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

public static class TitleBlockLibraryStore
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZwcadBatchPlot");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "TitleBlockLibrary.json");

    public static TitleBlockLibrary Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new TitleBlockLibrary();
        }

        var json = File.ReadAllText(path);
        return JsonConvert.DeserializeObject<TitleBlockLibrary>(json) ?? new TitleBlockLibrary();
    }

    public static void Save(TitleBlockLibrary library, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonConvert.SerializeObject(library, Formatting.Indented);
        File.WriteAllText(path, json);
    }

    public static bool Upsert(TitleBlockDefinition definition, string? path = null)
    {
        var library = Load(path);
        var existing = library.Blocks.FirstOrDefault(x =>
            string.Equals(x.BlockName, definition.BlockName, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            library.Blocks.Add(definition);
            Save(library, path);
            return true;
        }
        else
        {
            existing.HasPrintRegion = definition.HasPrintRegion;
            existing.CoordinateMode = string.IsNullOrWhiteSpace(definition.CoordinateMode) ? "Local" : definition.CoordinateMode;
            existing.PrintRegion = definition.PrintRegion;
            existing.PaperName = definition.PaperName;
            existing.PaperWidthMm = definition.PaperWidthMm;
            existing.PaperHeightMm = definition.PaperHeightMm;
            existing.TitleRegion = definition.TitleRegion;
            existing.DrawingNumberRegion = definition.DrawingNumberRegion;
            existing.UpdatedAt = DateTime.Now;
        }

        Save(library, path);
        return false;
    }
}
