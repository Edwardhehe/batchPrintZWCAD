using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PiaNO;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public static class AcadPlotterInstaller
{
    public const string PreferredPdfPlotter = "LA_pdf.pc3";
    private const string PreferredPmp = "LA_pdf.pmp";

    public sealed class InstallResult
    {
        public bool SourceFound { get; set; }
        public bool Installed { get; set; }
        public string TargetPlotterDirectory { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public static InstallResult InstallBundledPlotter()
    {
        var result = new InstallResult();
        try
        {
            var targetRootFromCad = GetAutoCadPlotterDirectory();
            if (!ShouldPreferBundledPia2Plotter() && !string.IsNullOrWhiteSpace(targetRootFromCad))
            {
                result.TargetPlotterDirectory = targetRootFromCad;
                var generatedPmpDir = Path.Combine(targetRootFromCad, "PMP Files");
                Directory.CreateDirectory(targetRootFromCad);
                Directory.CreateDirectory(generatedPmpDir);

                var generatedPc3 = Path.Combine(targetRootFromCad, PreferredPdfPlotter);
                var generatedPmp = Path.Combine(generatedPmpDir, PreferredPmp);
                if (TryInstallFromCurrentDwgToPdf(targetRootFromCad, generatedPc3, generatedPmp, out var generatedMessage))
                {
                    result.SourceFound = true;
                    result.Installed = true;
                    result.Message = generatedMessage;
                    return result;
                }
            }

            var sourceRoot = FindBundledPlotterRoot();
            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                result.Message = "未找到随插件附带的 LA_pdf 打印机配置。";
                return result;
            }

            result.SourceFound = true;

            // 读用户机器 DWG To PDF.pc3 判断 PIA 版本，选对应资源
            var piaSub = ShouldPreferBundledPia2Plotter()
                ? "PIA2"
                : PmpPiaConverter.IsCadPia3Compatible() ? "PIA3" : "PIA2";
            var sourcePc3 = Path.Combine(sourceRoot, piaSub, PreferredPdfPlotter);
            var sourcePmp = Path.Combine(sourceRoot, piaSub, "PMP Files", PreferredPmp);
            if (!File.Exists(sourcePc3) || !File.Exists(sourcePmp))
            {
                result.Message = $"LA_pdf 打印机配置不完整，需要 {piaSub}/{PreferredPdfPlotter} 和 {piaSub}/PMP Files/{PreferredPmp}。";
                return result;
            }

            var targetRoot = GetAutoCadPlotterDirectory();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                result.Message = "未能定位 AutoCAD Plotters 目录。";
                return result;
            }

            result.TargetPlotterDirectory = targetRoot;
            var targetPmpDir = Path.Combine(targetRoot, "PMP Files");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(targetPmpDir);

            var targetPc3 = Path.Combine(targetRoot, PreferredPdfPlotter);
            var targetPmp = Path.Combine(targetPmpDir, PreferredPmp);

            if (ShouldPreferBundledPia2Plotter())
            {
                InstallPia2FromCurrentDwgToPdf(sourcePc3, targetPc3, targetPmp);
                result.Installed = File.Exists(targetPc3) && File.Exists(targetPmp);
                result.Message = result.Installed
                    ? "LA_pdf plotter has been generated from the PIA2 seed."
                    : "LA_pdf plotter generation failed.";
                return result;
            }

            // 已有有效文件且版本匹配 → 跳过
            if (InstalledPlotterFilesMatch(sourcePc3, targetPc3, sourcePmp, targetPmp, targetRoot))
            {
                result.Installed = true;
                result.Message = "LA_pdf 打印机配置已存在且有效。";
                return result;
            }

            File.Copy(sourcePc3, targetPc3, overwrite: true);
            File.Copy(sourcePmp, targetPmp, overwrite: true);
            NormalizeInstalledPlotterFiles(targetRoot, targetPc3, targetPmp);

            result.Installed = File.Exists(targetPc3) && File.Exists(targetPmp);
            result.Message = result.Installed
                ? "LA_pdf 打印机配置已可用。"
                : "LA_pdf 打印机配置未复制。";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            return result;
        }
    }

    private static bool IsValidPlotterFile(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = new byte[30];
            if (fs.Read(header, 0, header.Length) < 10) return false;
            var text = System.Text.Encoding.ASCII.GetString(header);
            return text.StartsWith("PIAFILEVERSION") || text.StartsWith("[Meta]");
        }
        catch { return false; }
    }

    private static bool ShouldPreferBundledPia2Plotter()
    {
#if ACAD_CORE
        return false;
#else
        return true;
#endif
    }

    private static bool TryInstallFromCurrentDwgToPdf(string plottersDirectory, string targetPc3, string targetPmp, out string message)
    {
        message = "";
        try
        {
            var sourcePc3 = Path.Combine(plottersDirectory, "DWG To PDF.pc3");
            if (!File.Exists(sourcePc3))
            {
                message = "未找到当前 AutoCAD 的 DWG To PDF.pc3。";
                return false;
            }

            var raw = File.ReadAllText(sourcePc3);
            if (TryReadPia3Json(raw, out var pia3Root))
            {
                InstallPia3FromCurrentDwgToPdf(pia3Root, targetPc3, targetPmp);
                message = "LA_pdf 打印机配置已按当前 AutoCAD DWG To PDF 生成。";
                return true;
            }

            InstallPia2FromCurrentDwgToPdf(sourcePc3, targetPc3, targetPmp);
            message = "LA_pdf 打印机配置已按当前 AutoCAD DWG To PDF 生成。";
            return true;
        }
        catch (Exception ex)
        {
            message = "按当前 AutoCAD DWG To PDF 生成 LA_pdf 失败，已尝试随包兜底：" + ex.Message;
            return false;
        }
    }

    private static void InstallPia3FromCurrentDwgToPdf(JObject sourceRoot, string targetPc3, string targetPmp)
    {
        var pc3Root = (JObject)sourceRoot.DeepClone();
        NormalizePia3RootMeta(pc3Root, targetPmp);
        File.WriteAllText(targetPc3, "PIAFILEVERSION_3.0,json\n" + pc3Root.ToString(Formatting.Indented));

        var pmpRoot = (JObject)sourceRoot.DeepClone();
        var data = EnsureObject(pmpRoot, "data");
        data.Remove("media");
        data.Remove("io");
        data.Remove("res_color_mem");
        data.Remove("custom");
        NormalizePia3RootMeta(pmpRoot, targetPmp);
        AddPia3UserMedia(data);
        File.WriteAllText(targetPmp, "PIAFILEVERSION_3.0,json\n" + pmpRoot.ToString(Formatting.Indented));
    }

    private static void InstallPia2FromCurrentDwgToPdf(string sourcePc3, string targetPc3, string targetPmp)
    {
        var driverPath = FindPdfDriverPath();

        var pc3 = new PlotterConfiguration(sourcePc3)
        {
            ModelPath = targetPmp,
            ModelBase = Path.GetFileNameWithoutExtension(targetPmp),
            TruetypeAsText = true
        };
        if (!string.IsNullOrWhiteSpace(driverPath))
        {
            pc3.DriverPath = driverPath;
        }

        pc3.Saves(targetPc3);

        var pmp = new PlotterConfiguration(sourcePc3)
        {
            ModelPath = targetPmp,
            ModelBase = Path.GetFileNameWithoutExtension(targetPmp),
            TruetypeAsText = true
        };
        if (!string.IsNullOrWhiteSpace(driverPath))
        {
            pmp.DriverPath = driverPath;
        }

        pmp.Remove("media");
        pmp.Remove("io");
        pmp.Remove("res_color_mem");
        pmp.Remove("custom");
        pmp.Remove("mod");
        pmp.Remove("del");
        pmp.Remove("udm");
        pmp.Remove("hidden");
        AddPia2MediaContainers(pmp);
        pmp.Saves(targetPmp);
    }

    private static void NormalizePia3RootMeta(JObject root, string pmpPath)
    {
        var meta = EnsureObject(EnsureObject(root, "data"), "meta");
        meta["user_defined_model_pathname"] = pmpPath;
        meta["user_defined_model_basename"] = Path.GetFileNameWithoutExtension(pmpPath);
    }

    private static JObject EnsureObject(JObject parent, string name)
    {
        if (parent[name] is JObject existing)
        {
            return existing;
        }

        var created = new JObject();
        parent[name] = created;
        return created;
    }

    private static void AddPia3UserMedia(JObject data)
    {
        var mediaCaps = CreateMediaCaps();
        data["mod"] = new JObject { ["media"] = mediaCaps.DeepClone() };
        data["del"] = new JObject { ["media"] = mediaCaps.DeepClone() };
        data["hidden"] = new JObject { ["media"] = mediaCaps.DeepClone() };

        var udmMedia = (JObject)mediaCaps.DeepClone();
        var descriptions = new JObject();
        var sizes = new JObject();
        var index = 0;
        foreach (var paper in StandardUserPapers())
        {
            descriptions[index.ToString(CultureInfo.InvariantCulture)] = CreatePia3Description(paper);
            sizes[index.ToString(CultureInfo.InvariantCulture)] = CreatePia3Size(paper);
            index++;
        }

        udmMedia["description"] = descriptions;
        udmMedia["size"] = sizes;
        udmMedia["calibration"] = new JObject
        {
            ["_x"] = 1.0,
            ["_y"] = 1.0
        };
        data["udm"] = new JObject
        {
            ["calibration"] = new JObject
            {
                ["_x"] = 1.0,
                ["_y"] = 1.0
            },
            ["media"] = udmMedia
        };
    }

    private static JObject CreateMediaCaps() => new()
    {
        ["abilities"] = "500005500500505555000005550000000550000500000500000",
        ["caps_state"] = "000000000000000000000000000000000000000000000000000",
        ["ui_owner"] = "11111111111111111111000",
        ["size_max_x"] = 5080.0,
        ["size_max_y"] = 5080.0
    };

    private sealed class PaperSpec
    {
        public string Name { get; set; } = "";
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private static IEnumerable<PaperSpec> StandardUserPapers()
    {
        var basePapers = new[]
        {
            new PaperSpec { Name = "A4", Width = 297, Height = 210 },
            new PaperSpec { Name = "A3", Width = 420, Height = 297 },
            new PaperSpec { Name = "A2", Width = 594, Height = 420 },
            new PaperSpec { Name = "A1", Width = 841, Height = 594 },
            new PaperSpec { Name = "A0", Width = 1189, Height = 841 }
        };
        var multipliers = Enumerable.Range(8, 17).Select(unit => unit / 8d);

        foreach (var paper in basePapers)
        {
            foreach (var multiplier in multipliers)
            {
                var suffix = Math.Abs(multiplier - 1d) < 1e-9
                    ? ""
                    : "_" + multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "L";
                yield return new PaperSpec
                {
                    Name = paper.Name + suffix,
                    Width = Math.Round(paper.Width * multiplier),
                    Height = paper.Height
                };
            }
        }
    }

    private static JObject CreatePia3Description(PaperSpec paper)
    {
        var area = paper.Width * paper.Height;
        var descName = MediaDescriptionName(paper);
        return new JObject
        {
            ["caps_type"] = 2,
            ["dimensional"] = true,
            ["media_bounds_urx"] = paper.Width,
            ["media_bounds_ury"] = paper.Height,
            ["name"] = descName,
            ["printable_area"] = area,
            ["printable_bounds_llx"] = 0.0,
            ["printable_bounds_lly"] = 0.0,
            ["printable_bounds_urx"] = paper.Width,
            ["printable_bounds_ury"] = paper.Height
        };
    }

    private static JObject CreatePia3Size(PaperSpec paper) => new()
    {
        ["caps_type"] = 2,
        ["landscape_mode"] = true,
        ["localized_name"] = $"{paper.Name} ({FormatMm(paper.Width)} x {FormatMm(paper.Height)} mm)",
        ["media_description_name"] = MediaDescriptionName(paper),
        ["media_group"] = 15,
        ["name"] = $"UserDefinedMetric {paper.Name} ({FormatMm(paper.Width)} x {FormatMm(paper.Height)}mm)"
    };

    private static string MediaDescriptionName(PaperSpec paper)
    {
        var area = paper.Width * paper.Height;
        return $"UserDefinedMetric {paper.Name} Landscape {FormatMm(paper.Width)}W x {FormatMm(paper.Height)}H - (0, 0) x ({FormatPlain(paper.Width)}, {FormatPlain(paper.Height)}) ={FormatPlain(area)} mm";
    }

    private static void AddPia2MediaContainers(PlotterConfiguration config)
    {
        var caps = "{\n"
            + "abilities=\"500005500500505555000005550000000550000500000500000\n"
            + "caps_state=\"000000000000000000000000000000000000000000000000000\n"
            + "ui_owner=\"11111111111111111111000\n"
            + "size_max_x=5080.0\n"
            + "size_max_y=5080.0\n}";

        config.Add("mod", "").Add("media", caps);
        config.Add("del", "").Add("media", caps);
        var udm = config.Add("udm", "");
        var media = udm.Add("media", caps);
        var size = media.Add("size");
        var description = media.Add("description");
        var index = 0;
        foreach (var paper in StandardUserPapers())
        {
            var id = index.ToString(CultureInfo.InvariantCulture);
            size.Add(id, CreatePia2SizeText(id, paper));
            description.Add(id, CreatePia2DescriptionText(id, paper));
            index++;
        }

        config.Add("hidden", "").Add("media", caps);
    }

    private static string CreatePia2SizeText(string id, PaperSpec paper)
    {
        return id + "{\n"
            + "caps_type=2\n"
            + $"name=\"UserDefinedMetric {paper.Name} ({FormatMm(paper.Width)} x {FormatMm(paper.Height)}mm)\n"
            + $"localized_name=\"{paper.Name} ({FormatMm(paper.Width)} x {FormatMm(paper.Height)} mm)\n"
            + $"media_description_name=\"{MediaDescriptionName(paper)}\n"
            + "media_group=15\n"
            + "landscape_mode=TRUE\n}";
    }

    private static string CreatePia2DescriptionText(string id, PaperSpec paper)
    {
        return id + "{\n"
            + "caps_type=2\n"
            + $"name=\"{MediaDescriptionName(paper)}\n"
            + $"media_bounds_urx={FormatNumber(paper.Width)}\n"
            + $"media_bounds_ury={FormatNumber(paper.Height)}\n"
            + "printable_bounds_llx=0.0\n"
            + "printable_bounds_lly=0.0\n"
            + $"printable_bounds_urx={FormatNumber(paper.Width)}\n"
            + $"printable_bounds_ury={FormatNumber(paper.Height)}\n"
            + $"printable_area={FormatNumber(paper.Width * paper.Height)}\n"
            + "dimensional=TRUE\n}";
    }

    private static string FormatMm(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatPlain(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatNumber(double value) => value.ToString("0.0#########", CultureInfo.InvariantCulture);

    private static string FindPdfDriverPath()
    {
        try
        {
            var driverDirectory = GetSystemVariableString("ACADDRV");
            if (Directory.Exists(driverDirectory))
            {
                return Directory.GetFiles(driverDirectory, "*.hdi")
                    .FirstOrDefault(path => Path.GetFileName(path).IndexOf("pdfplot", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? "";
            }
        }
        catch
        {
        }

        return "";
    }

    private static bool FileContentEquals(string source, string target)
    {
        try
        {
            var sourceInfo = new FileInfo(source);
            var targetInfo = new FileInfo(target);
            if (!sourceInfo.Exists || !targetInfo.Exists || sourceInfo.Length != targetInfo.Length)
            {
                return false;
            }

            using var sourceStream = File.OpenRead(source);
            using var targetStream = File.OpenRead(target);
            var sourceBuffer = new byte[8192];
            var targetBuffer = new byte[8192];
            while (true)
            {
                var sourceRead = sourceStream.Read(sourceBuffer, 0, sourceBuffer.Length);
                var targetRead = targetStream.Read(targetBuffer, 0, targetBuffer.Length);
                if (sourceRead != targetRead)
                {
                    return false;
                }

                if (sourceRead == 0)
                {
                    return true;
                }

                for (var i = 0; i < sourceRead; i++)
                {
                    if (sourceBuffer[i] != targetBuffer[i])
                    {
                        return false;
                    }
                }
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool InstalledPlotterFilesMatch(
        string sourcePc3,
        string targetPc3,
        string sourcePmp,
        string targetPmp,
        string plottersDirectory)
    {
        if (!IsValidPlotterFile(targetPc3) || !IsValidPlotterFile(targetPmp))
        {
            return false;
        }

        var driverPath = ReadDriverPath(Path.Combine(plottersDirectory, "DWG To PDF.pc3"));
        return PlotterFileContentMatches(sourcePc3, targetPc3, targetPmp, driverPath)
            && PlotterFileContentMatches(sourcePmp, targetPmp, targetPmp, driverPath);
    }

    private static bool PlotterFileContentMatches(string source, string target, string pmpPath, string driverPath)
    {
        try
        {
            var sourceRaw = File.ReadAllText(source);
            var targetRaw = File.ReadAllText(target);
            if (TryNormalizePia3Text(sourceRaw, pmpPath, driverPath, out var normalizedSource)
                && TryNormalizePia3Text(targetRaw, pmpPath, driverPath, out var normalizedTarget))
            {
                return string.Equals(normalizedSource, normalizedTarget, StringComparison.Ordinal);
            }
        }
        catch
        {
            return false;
        }

        return FileContentEquals(source, target);
    }

    private static void NormalizeInstalledPlotterFiles(string plottersDirectory, string pc3Path, string pmpPath)
    {
        try
        {
            var driverPath = ReadDriverPath(Path.Combine(plottersDirectory, "DWG To PDF.pc3"));
            NormalizePia3Meta(pc3Path, pmpPath, driverPath);
            NormalizePia3Meta(pmpPath, pmpPath, driverPath);
        }
        catch
        {
            // Plotter normalization is a compatibility aid. A copied valid PC3/PMP should still be usable.
        }
    }

    private static string ReadDriverPath(string pc3Path)
    {
        try
        {
            if (!File.Exists(pc3Path))
            {
                return "";
            }

            var raw = File.ReadAllText(pc3Path);
            if (!TryReadPia3Json(raw, out var root))
            {
                return "";
            }

            return root["data"]?["meta"]?["driver_pathname"]?.Value<string>() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static void NormalizePia3Meta(string path, string pmpPath, string driverPath)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var raw = File.ReadAllText(path);
        if (!TryReadPia3Json(raw, out var root))
        {
            return;
        }

        var meta = root["data"]?["meta"] as JObject;
        if (meta == null)
        {
            return;
        }

        meta["user_defined_model_pathname"] = pmpPath;
        meta["user_defined_model_basename"] = Path.GetFileNameWithoutExtension(pmpPath);
        if (!string.IsNullOrWhiteSpace(driverPath))
        {
            meta["driver_pathname"] = driverPath;
        }

        File.WriteAllText(path, "PIAFILEVERSION_3.0,json\n" + root.ToString(Formatting.Indented));
    }

    private static bool TryNormalizePia3Text(string raw, string pmpPath, string driverPath, out string normalized)
    {
        normalized = "";
        if (!TryReadPia3Json(raw, out var root))
        {
            return false;
        }

        var meta = root["data"]?["meta"] as JObject;
        if (meta == null)
        {
            return false;
        }

        meta["user_defined_model_pathname"] = pmpPath;
        meta["user_defined_model_basename"] = Path.GetFileNameWithoutExtension(pmpPath);
        if (!string.IsNullOrWhiteSpace(driverPath))
        {
            meta["driver_pathname"] = driverPath;
        }

        normalized = "PIAFILEVERSION_3.0,json\n" + root.ToString(Formatting.Indented);
        return true;
    }

    private static bool TryReadPia3Json(string raw, out JObject root)
    {
        root = new JObject();
        if (!raw.StartsWith("PIAFILEVERSION_3.0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var jsonStart = raw.IndexOf('{');
        if (jsonStart < 0)
        {
            return false;
        }

        root = JObject.Parse(raw.Substring(jsonStart));
        return true;
    }

    private static string? FindBundledPlotterRoot()
    {
        foreach (var root in GetCandidateBaseDirectories())
        {
            var candidate = Path.Combine(root, "Plotters");
            if (File.Exists(Path.Combine(candidate, "PIA2", PreferredPdfPlotter))
                || File.Exists(Path.Combine(candidate, "PIA3", PreferredPdfPlotter)))
            {
                return candidate;
            }

            candidate = Path.Combine(root, "resources", "acad", "Plotters");
            if (File.Exists(Path.Combine(candidate, "PIA2", PreferredPdfPlotter))
                || File.Exists(Path.Combine(candidate, "PIA3", PreferredPdfPlotter)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string[] GetCandidateBaseDirectories()
    {
        var assemblyPath = Assembly.GetExecutingAssembly().Location;
        var assemblyDirectory = string.IsNullOrWhiteSpace(assemblyPath)
            ? AppDomain.CurrentDomain.BaseDirectory
            : Path.GetDirectoryName(assemblyPath) ?? AppDomain.CurrentDomain.BaseDirectory;

        return new[]
        {
            AppDomain.CurrentDomain.BaseDirectory,
            assemblyDirectory,
            Directory.GetCurrentDirectory()
        };
    }

    public static string GetPlottersDirectory() => GetAutoCadPlotterDirectory();

    private static string GetAutoCadPlotterDirectory()
    {
        var printerConfigDir = GetSystemVariableString("PrinterConfigDir");
        if (!string.IsNullOrWhiteSpace(printerConfigDir))
        {
            return printerConfigDir;
        }

        var roamableRoot = GetSystemVariableString("ROAMABLEROOTPREFIX");
        if (!string.IsNullOrWhiteSpace(roamableRoot))
        {
            return Path.Combine(roamableRoot, "Plotters");
        }

        return "";
    }

    private static string GetSystemVariableString(string name)
    {
        try
        {
            return CadApp.GetSystemVariable(name)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool CopyIfDifferent(string source, string target)
    {
        if (File.Exists(target))
        {
            var sourceInfo = new FileInfo(source);
            var targetInfo = new FileInfo(target);
            if (sourceInfo.Length == targetInfo.Length
                && sourceInfo.LastWriteTimeUtc <= targetInfo.LastWriteTimeUtc.AddSeconds(1))
            {
                return false;
            }
        }

        File.Copy(source, target, overwrite: true);
        return true;
    }
}
