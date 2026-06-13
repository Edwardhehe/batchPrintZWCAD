using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

public static class TitleBlockLibraryStore
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadBatchPlot");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "TitleBlockLibrary.json");

    private static string ZwcadLibraryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZwcadBatchPlot", "TitleBlockLibrary.json");

    public static TitleBlockLibrary Load(string? path = null)
    {
        path ??= DefaultPath;
        var isDefaultPath = string.Equals(Path.GetFullPath(path), Path.GetFullPath(DefaultPath), StringComparison.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            var library = new TitleBlockLibrary();
            return isDefaultPath ? MergeZwcadLibrary(library) : library;
        }

        var json = File.ReadAllText(path);
        var loaded = JsonConvert.DeserializeObject<TitleBlockLibrary>(json) ?? new TitleBlockLibrary();
        return isDefaultPath ? MergeZwcadLibrary(loaded) : loaded;
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

    private static TitleBlockLibrary MergeZwcadLibrary(TitleBlockLibrary library)
    {
        try
        {
            if (!File.Exists(ZwcadLibraryPath))
            {
                return library;
            }

            var zwcadJson = File.ReadAllText(ZwcadLibraryPath);
            var zwcadLibrary = JsonConvert.DeserializeObject<TitleBlockLibrary>(zwcadJson);
            if (zwcadLibrary?.Blocks == null || zwcadLibrary.Blocks.Count == 0)
            {
                return library;
            }

            var changed = false;
            foreach (var block in zwcadLibrary.Blocks)
            {
                if (string.IsNullOrWhiteSpace(block.BlockName))
                {
                    continue;
                }

                var existing = library.Blocks.FirstOrDefault(x =>
                    string.Equals(x.BlockName, block.BlockName, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    library.Blocks.Add(block);
                    changed = true;
                    continue;
                }

                if (block.UpdatedAt > existing.UpdatedAt)
                {
                    var index = library.Blocks.IndexOf(existing);
                    library.Blocks[index] = block;
                    changed = true;
                }
            }

            if (changed)
            {
                try
                {
                    Save(library);
                }
                catch
                {
                }
            }

            return library;
        }
        catch
        {
            return library;
        }
    }
}
