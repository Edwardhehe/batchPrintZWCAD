using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

public static class TitleBlockLibraryStore
{
#if ZWCAD
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZwcadBatchPlot");
#else
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadBatchPlot");
#endif

    public static string DefaultPath => Path.Combine(DefaultDirectory, "TitleBlockLibrary.json");

    public static TitleBlockLibrary Load(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
        {
            try
            {
                return Deserialize(path);
            }
            catch (Exception primaryError)
            {
                if (IsJsonAssemblyLoadError(primaryError))
                {
                    throw new InvalidOperationException(
                        "无法加载 Newtonsoft.Json。请确认插件目录中有 Newtonsoft.Json.dll，并关闭天正等可能占用该组件的插件后重试。",
                        primaryError);
                }

                var backupPath = path + ".bak";
                if (File.Exists(backupPath))
                {
                    try
                    {
                        return Deserialize(backupPath);
                    }
                    catch
                    {
                    }
                }

                throw new InvalidDataException("图框库文件损坏，且备份无法恢复: " + path, primaryError);
            }
        }

        var missingPrimaryBackup = path + ".bak";
        return File.Exists(missingPrimaryBackup)
            ? Deserialize(missingPrimaryBackup)
            : new TitleBlockLibrary();
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
            existing.DateRegion = definition.DateRegion;
            existing.RevisionRegion = definition.RevisionRegion;
            existing.PhaseRegion = definition.PhaseRegion;
            existing.Info1Region = definition.Info1Region;
            existing.Info2Region = definition.Info2Region;
            existing.UpdatedAt = DateTime.Now;
        }

        Save(library, path);
        return false;
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
            // 旧版图框库若保存了可选字段的块内坐标，也一并迁移为相对打印框坐标。
            definition.DateRegion = HasArea(definition.DateRegion) ? ToFrameRelative(definition.DateRegion, definition.PrintRegion) : definition.DateRegion;
            definition.RevisionRegion = HasArea(definition.RevisionRegion) ? ToFrameRelative(definition.RevisionRegion, definition.PrintRegion) : definition.RevisionRegion;
            definition.PhaseRegion = HasArea(definition.PhaseRegion) ? ToFrameRelative(definition.PhaseRegion, definition.PrintRegion) : definition.PhaseRegion;
            definition.Info1Region = HasArea(definition.Info1Region) ? ToFrameRelative(definition.Info1Region, definition.PrintRegion) : definition.Info1Region;
            definition.Info2Region = HasArea(definition.Info2Region) ? ToFrameRelative(definition.Info2Region, definition.PrintRegion) : definition.Info2Region;
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

    /// <summary>
    /// 判断异常是否由 Newtonsoft.Json 程序集加载失败引起，避免把缺 DLL / 版本冲突误报成图框库损坏。
    /// </summary>
    private static bool IsJsonAssemblyLoadError(Exception error)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            var fileName = current is FileLoadException loadError
                ? loadError.FileName
                : current is FileNotFoundException missingError
                    ? missingError.FileName
                    : null;
            var text = fileName ?? current.Message;
            if (!string.IsNullOrWhiteSpace(text)
                && text.IndexOf("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static TitleBlockLibrary Deserialize(string path)
    {
        var json = File.ReadAllText(path);
        var loaded = JsonConvert.DeserializeObject<TitleBlockLibrary>(json) ?? new TitleBlockLibrary();
        loaded.Blocks ??= new List<TitleBlockDefinition>();
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
