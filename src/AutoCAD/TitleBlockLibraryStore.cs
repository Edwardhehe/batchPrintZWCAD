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
            var backupPath = path + ".bak";
            var library = File.Exists(backupPath)
                ? Deserialize(backupPath)
                : new TitleBlockLibrary();
            return isDefaultPath ? MergeZwcadLibrary(library) : library;
        }

        TitleBlockLibrary loaded;
        try
        {
            loaded = Deserialize(path);
        }
        catch (Exception primaryError)
        {
            var backupPath = path + ".bak";
            if (!File.Exists(backupPath))
            {
                throw new InvalidDataException("图框库文件损坏，且没有可用备份: " + path, primaryError);
            }

            loaded = Deserialize(backupPath);
        }
        return isDefaultPath ? MergeZwcadLibrary(loaded) : loaded;
    }

    public static void Save(TitleBlockLibrary library, string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("图框库路径无效: " + path);
        Directory.CreateDirectory(directory);
        var json = JsonConvert.SerializeObject(library, Formatting.Indented);
        WriteAtomically(path, json);
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
            if (zwcadLibrary != null)
            {
                NormalizeFrameCoordinates(zwcadLibrary);
            }

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

    private static void NormalizeFrameCoordinates(TitleBlockLibrary library)
    {
        foreach (var definition in library.Blocks)
        {
            if (!string.Equals(definition.CoordinateMode, "Local", StringComparison.OrdinalIgnoreCase)
                || !HasArea(definition.PrintRegion))
            {
                continue;
            }

            definition.TitleRegion = ToFrameRelative(definition.TitleRegion, definition.PrintRegion);
            definition.DrawingNumberRegion = ToFrameRelative(definition.DrawingNumberRegion, definition.PrintRegion);
            definition.CoordinateMode = "Frame";
        }
    }

    private static LocalRectangle ToFrameRelative(LocalRectangle region, LocalRectangle referenceFrame)
    {
        return LocalRectangle.FromPoints(
            region.MinX - referenceFrame.MinX,
            region.MinY - referenceFrame.MinY,
            region.MaxX - referenceFrame.MinX,
            region.MaxY - referenceFrame.MinY);
    }

    private static bool HasArea(LocalRectangle region)
    {
        return Math.Abs(region.MaxX - region.MinX) > 1e-6
            && Math.Abs(region.MaxY - region.MinY) > 1e-6;
    }

    private static TitleBlockLibrary Deserialize(string path)
    {
        var json = File.ReadAllText(path);
        var loaded = JsonConvert.DeserializeObject<TitleBlockLibrary>(json) ?? new TitleBlockLibrary();
        loaded.Blocks ??= new System.Collections.Generic.List<TitleBlockDefinition>();
        NormalizeFrameCoordinates(loaded);
        return loaded;
    }

    private static void WriteAtomically(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        var tempPath = fullPath + ".tmp";
        var backupPath = fullPath + ".bak";
        File.WriteAllText(tempPath, contents);
        try
        {
            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, backupPath, true);
            }
            else
            {
                File.Move(tempPath, fullPath);
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
}
