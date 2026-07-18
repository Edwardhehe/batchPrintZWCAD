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
    public const string PreferredPngPlotter = "LA_png.pc3";
    public const string PreferredJpgPlotter = "LA_jpg.pc3";
    public const string PreferredDwfPlotter = "LA_dwf.pc3";
    private const string PreferredPmp = "LA_pdf.pmp";
    private const string PreferredPngPmp = "LA_png.pmp";
    private const string PreferredJpgPmp = "LA_jpg.pmp";
    private const string PreferredDwfPmp = "LA_dwf.pmp";

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
            // 所有 AutoCAD 版本都先读取当前用户配置目录的 DWG To PDF.pc3，
            // 由文件本身决定生成 PIA2 还是 PIA3，不按插件目标版本硬编码格式。
            if (!string.IsNullOrWhiteSpace(targetRootFromCad))
            {
                result.TargetPlotterDirectory = targetRootFromCad;
                var generatedPmpDir = Path.Combine(targetRootFromCad, "PMP Files");
                Directory.CreateDirectory(targetRootFromCad);
                Directory.CreateDirectory(generatedPmpDir);

                var generatedPc3 = Path.Combine(targetRootFromCad, PreferredPdfPlotter);
                var generatedPmp = Path.Combine(generatedPmpDir, PreferredPmp);
                if (IsValidPlotterFile(generatedPc3) && IsValidPlotterFile(generatedPmp))
                {
                    EnsurePmpAttachment(generatedPc3, generatedPmp, forceRewrite: false, out _);
                    result.SourceFound = true;
                    result.Installed = true;
                    result.Message = "LA_pdf 打印机配置已存在，已保留现有 PC3/PMP。";
                    return result;
                }

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

            // PMP 可能包含用户或本插件动态注册的纸张，已有有效配置时不得覆盖。
            if (IsValidPlotterFile(targetPc3) && IsValidPlotterFile(targetPmp))
            {
                EnsurePmpAttachment(targetPc3, targetPmp, forceRewrite: false, out _);
                result.Installed = true;
                result.Message = "LA_pdf 打印机配置已存在，已保留现有 PC3/PMP。";
                return result;
            }

            if (ShouldPreferBundledPia2Plotter())
            {
                // PIA2 也优先以当前用户配置目录的 DWG To PDF.pc3 为母版；
                // 随包文件只在当前配置确实缺失时兜底。
                var currentDwgToPdf = Path.Combine(targetRoot, "DWG To PDF.pc3");
                var pia2Seed = IsPia2PlotterFile(currentDwgToPdf) ? currentDwgToPdf : sourcePc3;
                InstallPia2FromCurrentDwgToPdf(pia2Seed, targetPc3, targetPmp);
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

    public static string InstallPngPlotter()
    {
        return InstallRasterPlotter(
            PreferredPngPlotter,
            PreferredPngPmp,
            new[] { "PublishToWeb PNG.pc3" },
            name => name.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
                    && name.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) < 0);
    }

    public static string InstallJpgPlotter()
    {
        return InstallRasterPlotter(
            PreferredJpgPlotter,
            PreferredJpgPmp,
            new[] { "PublishToWeb JPG.pc3" },
            name => name.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string InstallRasterPlotter(
        string targetPlotterName,
        string targetPmpName,
        IEnumerable<string> preferredNames,
        Func<string, bool> fallbackPredicate)
    {
        try
        {
            var targetRoot = GetAutoCadPlotterDirectory();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                return "";
            }

            var targetPmpDirectory = Path.Combine(targetRoot, "PMP Files");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(targetPmpDirectory);
            var targetPc3 = Path.Combine(targetRoot, targetPlotterName);
            var targetPmp = Path.Combine(targetPmpDirectory, targetPmpName);

            var sources = Directory.EnumerateFiles(targetRoot, "*.pc3", SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    targetPlotterName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var preferredName in preferredNames)
            {
                var preferredSource = sources.FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), preferredName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(preferredSource))
                {
                    return GenerateRasterPlotter(preferredSource, targetPc3, targetPmp, targetPlotterName);
                }
            }

            var fallbackSource = sources.FirstOrDefault(path => fallbackPredicate(Path.GetFileName(path)));
            return string.IsNullOrWhiteSpace(fallbackSource)
                ? ""
                : GenerateRasterPlotter(fallbackSource, targetPc3, targetPmp, targetPlotterName);
        }
        catch
        {
            return "";
        }
    }

    private static string GenerateRasterPlotter(
        string sourcePc3,
        string targetPc3,
        string targetPmp,
        string targetPlotterName)
    {
        // 栅格 PC3 必须继承 PNG/JPG 自身的驱动与图像参数；仅将软件纸张写入独立 PMP。
        // 目标仅为本插件的 LA_png/LA_jpg，不读取或改写用户其他绘图器设置。
        var raw = File.ReadAllText(sourcePc3);
        if (TryReadPia3Json(raw, out var pia3Root))
        {
            InstallPia3RasterFromSource(pia3Root, targetPc3, targetPmp);
        }
        else
        {
            InstallPia2RasterFromSource(sourcePc3, targetPc3, targetPmp, ReadDriverPath(sourcePc3));
        }

        return IsValidPlotterFile(targetPc3) && IsValidPlotterFile(targetPmp)
            ? targetPlotterName
            : "";
    }

    public static void RefreshPlotterDevices()
    {
        try
        {
            // 新用户首次安装时 PC3 在 CAD 启动后才生成，必须刷新全局设备列表才能在当前会话使用。
            Autodesk.AutoCAD.PlottingServices.PlotConfigManager.RefreshList(
                Autodesk.AutoCAD.PlottingServices.RefreshCode.RefreshPC3DevicesList);
        }
        catch
        {
            // 调用方随后会按当前枚举结果校验；刷新失败时不得回退到 CAD 自带设备。
        }
    }

    public static (double X, double Y) GetRasterDpi(string deviceName)
    {
        try
        {
            var plottersDirectory = GetAutoCadPlotterDirectory();
            var pc3Path = string.IsNullOrWhiteSpace(plottersDirectory)
                ? ""
                : Path.Combine(plottersDirectory, Path.GetFileName(deviceName));
            if (!File.Exists(pc3Path))
            {
                return (100d, 100d);
            }

            var raw = File.ReadAllText(pc3Path);
            if (TryReadPia3Json(raw, out var pia3Root))
            {
                return ReadPia3RasterDpi(pia3Root);
            }

            return ReadPia2RasterDpi(new PlotterConfiguration(pc3Path));
        }
        catch
        {
            // AutoCAD 自带的 PublishToWeb 默认为 100 DPI；读取失败时仅用于单位换算，不修改任何 PC3。
            return (100d, 100d);
        }
    }

    public static string InstallDwfPlotter()
    {
        try
        {
            var targetRoot = GetAutoCadPlotterDirectory();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                return "";
            }

            var targetPc3 = Path.Combine(targetRoot, PreferredDwfPlotter);
            var targetPmpDir = Path.Combine(targetRoot, "PMP Files");
            var targetPmp = Path.Combine(targetPmpDir, PreferredDwfPmp);
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(targetPmpDir);

            var sourcePc3 = Directory.GetFiles(targetRoot, "*.pc3")
                .Where(path => !string.Equals(Path.GetFileName(path), PreferredDwfPlotter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => string.Equals(Path.GetFileName(path), "DWF6 ePlot.pc3", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault(path => Path.GetFileName(path).IndexOf("DWF", StringComparison.OrdinalIgnoreCase) >= 0
                                        && Path.GetFileName(path).IndexOf("DWFx", StringComparison.OrdinalIgnoreCase) < 0);
            if (string.IsNullOrWhiteSpace(sourcePc3))
            {
                return File.Exists(targetPc3) ? PreferredDwfPlotter : "";
            }

            var raw = File.ReadAllText(sourcePc3);
            if (TryReadPia3Json(raw, out var pia3Root))
            {
                InstallPia3FromCurrentDwgToPdf(pia3Root, targetPc3, targetPmp);
            }
            else
            {
                InstallPia2FromSource(sourcePc3, targetPc3, targetPmp, "");
            }

            return File.Exists(targetPc3) && File.Exists(targetPmp) ? PreferredDwfPlotter : "";
        }
        catch
        {
            return "";
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

    private static bool IsPia2PlotterFile(string path)
    {
        if (!File.Exists(path))
            return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[32];
            var read = fs.Read(header, 0, header.Length);
            var text = System.Text.Encoding.ASCII.GetString(header, 0, read);
            return text.StartsWith("PIAFILEVERSION_2.0", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 确认 LA_pdf.pc3/PMP 指向当前 PMP。
    /// forceRewrite 用于 PMP 新增纸张后重写配置，从而使 AutoCAD 放弃已加载的设备缓存。
    /// 只更新 PMP 关联字段，不重建纸张节点、不修改驱动或其他打印机设置。
    /// </summary>
    public static bool EnsurePmpAttachment(
        string pc3Path,
        string pmpPath,
        bool forceRewrite,
        out string message)
    {
        message = "";
        if (!IsValidPlotterFile(pc3Path) || !IsValidPlotterFile(pmpPath))
        {
            message = "LA_pdf.pc3 或 LA_pdf.pmp 不存在/无效。";
            return false;
        }

        try
        {
            var fullPmpPath = Path.GetFullPath(pmpPath);
            var expectedBase = Path.GetFileNameWithoutExtension(fullPmpPath);
            var plottersDirectory = Path.GetDirectoryName(pc3Path) ?? "";
            var sourceDriverPath = ReadDriverPath(Path.Combine(plottersDirectory, "DWG To PDF.pc3"));
            var raw = File.ReadAllText(pc3Path);
            if (TryReadPia3Json(raw, out var root))
            {
                if (!EnsurePia3MetaFile(
                        pc3Path, root, fullPmpPath, expectedBase, sourceDriverPath, forceRewrite, out message))
                    return false;

                var pmpRaw = File.ReadAllText(pmpPath);
                if (!TryReadPia3Json(pmpRaw, out var pmpRoot))
                {
                    message = "LA_pdf.pc3 为 PIA3，但 LA_pdf.pmp 不是对应的 PIA3 格式。";
                    return false;
                }

                if (!EnsurePia3MetaFile(
                        pmpPath, pmpRoot, fullPmpPath, expectedBase, sourceDriverPath, forceRewrite, out message))
                    return false;

                message = $"PMP={fullPmpPath}; 驱动继承自当前 DWG To PDF.pc3";
                return true;
            }

            EnsurePia2MetaFile(pc3Path, fullPmpPath, expectedBase, sourceDriverPath, forceRewrite);
            EnsurePia2MetaFile(pmpPath, fullPmpPath, expectedBase, sourceDriverPath, forceRewrite);
            message = $"PMP={fullPmpPath}; 驱动继承自当前 DWG To PDF.pc3";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private static bool EnsurePia3MetaFile(
        string path,
        JObject root,
        string pmpPath,
        string expectedBase,
        string sourceDriverPath,
        bool forceRewrite,
        out string message)
    {
        message = "";
        var meta = root["data"]?["meta"] as JObject;
        if (meta == null)
        {
            message = Path.GetFileName(path) + " 缺少 PIA3 meta 节点。";
            return false;
        }

        var currentPath = meta["user_defined_model_pathname"]?.Value<string>() ?? "";
        var currentBase = meta["user_defined_model_basename"]?.Value<string>() ?? "";
        var currentDriver = meta["driver_pathname"]?.Value<string>() ?? "";
        var driverDiffers = !string.IsNullOrWhiteSpace(sourceDriverPath)
            && !string.Equals(currentDriver, sourceDriverPath, StringComparison.OrdinalIgnoreCase);
        var needsWrite = forceRewrite
            || !PathsEqual(currentPath, pmpPath)
            || !string.Equals(currentBase, expectedBase, StringComparison.OrdinalIgnoreCase)
            || driverDiffers;
        if (!needsWrite)
            return true;

        meta["user_defined_model_pathname"] = pmpPath;
        meta["user_defined_model_basename"] = expectedBase;
        if (!string.IsNullOrWhiteSpace(sourceDriverPath))
            meta["driver_pathname"] = sourceDriverPath;
        File.WriteAllText(path, "PIAFILEVERSION_3.0,json\n" + root.ToString(Formatting.Indented));
        return true;
    }

    private static void EnsurePia2MetaFile(
        string path,
        string pmpPath,
        string expectedBase,
        string sourceDriverPath,
        bool forceRewrite)
    {
        var config = new PlotterConfiguration(path);
        var driverDiffers = !string.IsNullOrWhiteSpace(sourceDriverPath)
            && !string.Equals(config.DriverPath ?? "", sourceDriverPath, StringComparison.OrdinalIgnoreCase);
        var needsWrite = forceRewrite
            || !PathsEqual(config.ModelPath ?? "", pmpPath)
            || !string.Equals(config.ModelBase ?? "", expectedBase, StringComparison.OrdinalIgnoreCase)
            || driverDiffers;
        if (!needsWrite)
            return;

        config.ModelPath = pmpPath;
        config.ModelBase = expectedBase;
        if (!string.IsNullOrWhiteSpace(sourceDriverPath))
            config.DriverPath = sourceDriverPath;
        config.Saves(path);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
        // 继承当前用户 DWG To PDF.pc3 内部记录的驱动标识；不要跨版本扫描其他安装目录。
        InstallPia2FromSource(sourcePc3, targetPc3, targetPmp, ReadDriverPath(sourcePc3));
    }

    private static void InstallPia2FromSource(string sourcePc3, string targetPc3, string targetPmp, string driverPath)
    {

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

    private static void InstallPia2RasterFromSource(string sourcePc3, string targetPc3, string targetPmp, string driverPath)
    {
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

        var dpi = ReadPia2RasterDpi(pc3);
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
        // AutoCAD 的 PNG/JPG 驱动不接受 PDF 型毫米介质，必须按源 PC3 的 DPI 生成像素型 PMP。
        AddPia2RasterMediaContainers(pmp, dpi.X, dpi.Y);
        pmp.Saves(targetPmp);
    }

    private static void InstallPia3RasterFromSource(JObject sourceRoot, string targetPc3, string targetPmp)
    {
        var pc3Root = (JObject)sourceRoot.DeepClone();
        NormalizePia3RootMeta(pc3Root, targetPmp);
        var dpi = ReadPia3RasterDpi(pc3Root);
        File.WriteAllText(targetPc3, "PIAFILEVERSION_3.0,json\n" + pc3Root.ToString(Formatting.Indented));

        var pmpRoot = (JObject)sourceRoot.DeepClone();
        var data = EnsureObject(pmpRoot, "data");
        data.Remove("media");
        data.Remove("io");
        data.Remove("res_color_mem");
        data.Remove("custom");
        NormalizePia3RootMeta(pmpRoot, targetPmp);
        AddPia3RasterUserMedia(data, dpi.X, dpi.Y);
        File.WriteAllText(targetPmp, "PIAFILEVERSION_3.0,json\n" + pmpRoot.ToString(Formatting.Indented));
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

    private sealed class RasterPaperSpec
    {
        public string Name { get; set; } = "";
        public int WidthPixels { get; set; }
        public int HeightPixels { get; set; }
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

    private static IEnumerable<RasterPaperSpec> RasterUserPapers(double dpiX, double dpiY)
    {
        foreach (var paper in StandardUserPapers())
        {
            var landscape = CreateRasterPaper(paper.Name, paper.Width, paper.Height, dpiX, dpiY);
            yield return landscape;

            // 树格驱动的 PMP 以像素画布记录方向，横竖两个介质都写入可避免旋转后再次匹配失败。
            var portrait = CreateRasterPaper(paper.Name, paper.Height, paper.Width, dpiX, dpiY);
            if (portrait.WidthPixels != landscape.WidthPixels || portrait.HeightPixels != landscape.HeightPixels)
            {
                yield return portrait;
            }
        }
    }

    private static RasterPaperSpec CreateRasterPaper(
        string name,
        double widthMm,
        double heightMm,
        double dpiX,
        double dpiY)
    {
        return new RasterPaperSpec
        {
            Name = name,
            WidthPixels = Math.Max(1, (int)Math.Round(widthMm / 25.4d * dpiX, MidpointRounding.AwayFromZero)),
            HeightPixels = Math.Max(1, (int)Math.Round(heightMm / 25.4d * dpiY, MidpointRounding.AwayFromZero))
        };
    }

    private static (double X, double Y) ReadPia2RasterDpi(PlotterConfiguration config)
    {
        var resolution = config["res_color_mem"]?["resolution"];
        return (
            ReadPositiveNumber(resolution?.NodeMap, "effective_resolution_x", "addr_resolution_x", "phys_resolution_x"),
            ReadPositiveNumber(resolution?.NodeMap, "effective_resolution_y", "addr_resolution_y", "phys_resolution_y"));
    }

    private static (double X, double Y) ReadPia3RasterDpi(JObject root)
    {
        var resolution = root["data"]?["res_color_mem"]?["resolution"] as JObject;
        return (
            ReadPositiveNumber(resolution, "effective_resolution_x", "addr_resolution_x", "phys_resolution_x"),
            ReadPositiveNumber(resolution, "effective_resolution_y", "addr_resolution_y", "phys_resolution_y"));
    }

    private static double ReadPositiveNumber(
        IReadOnlyDictionary<string, string>? values,
        params string[] keys)
    {
        if (values != null)
        {
            foreach (var key in keys)
            {
                if (values.TryGetValue(key, out var raw)
                    && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                    && value > 0d)
                {
                    return value;
                }
            }
        }

        return 100d;
    }

    private static double ReadPositiveNumber(JObject? values, params string[] keys)
    {
        if (values != null)
        {
            foreach (var key in keys)
            {
                var value = values.Value<double?>(key);
                if (value > 0d)
                {
                    return value.Value;
                }
            }
        }

        return 100d;
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

    private static void AddPia2RasterMediaContainers(PlotterConfiguration config, double dpiX, double dpiY)
    {
        const string caps = "{\n"
            + "abilities=\"500505500500505555000005550000000550000500000500555\n"
            + "caps_state=\"000000000000000000000000000000000000000000000000000\n"
            + "ui_owner=\"11111111111111111111110\n"
            + "size_max_x=320000.0\n"
            + "size_max_y=320000.0\n"
            + "max_roll_height=320000.0\n}";

        config.Add("mod", "").Add("media", caps);
        config.Add("del", "").Add("media", caps);
        var udm = config.Add("udm", "");
        udm.Add("calibration", "{\n_x=1.0\n_y=1.0\n}");
        var media = udm.Add("media", caps);
        var size = media.Add("size");
        var description = media.Add("description");
        var index = 0;
        foreach (var paper in RasterUserPapers(dpiX, dpiY))
        {
            var id = index.ToString(CultureInfo.InvariantCulture);
            size.Add(id, CreatePia2RasterSizeText(id, paper));
            description.Add(id, CreatePia2RasterDescriptionText(id, paper));
            index++;
        }

        config.Add("hidden", "").Add("media", caps);
    }

    private static string CreatePia2RasterSizeText(string id, RasterPaperSpec paper)
    {
        var width = paper.WidthPixels;
        var height = paper.HeightPixels;
        return id + "{\n"
            + "caps_type=2\n"
            + $"name=\"UserDefinedRaster ({FormatPixels(width)} x {FormatPixels(height)}Pixels)\n"
            + $"localized_name=\"{paper.Name} ({FormatPixels(width)} x {FormatPixels(height)} Pixels)\n"
            + $"media_description_name=\"{RasterMediaDescriptionName(paper)}\n"
            + "media_group=16\n"
            + $"landscape_mode={(width >= height ? "TRUE" : "FALSE")}\n}}";
    }

    private static string CreatePia2RasterDescriptionText(string id, RasterPaperSpec paper)
    {
        var printableWidth = Math.Max(0, paper.WidthPixels - 1);
        var printableHeight = Math.Max(0, paper.HeightPixels - 1);
        return id + "{\n"
            + "caps_type=2\n"
            + $"name=\"{RasterMediaDescriptionName(paper)}\n"
            + $"media_bounds_urx={FormatNumber(paper.WidthPixels)}\n"
            + $"media_bounds_ury={FormatNumber(paper.HeightPixels)}\n"
            + "printable_bounds_llx=0.0\n"
            + "printable_bounds_lly=0.0\n"
            + $"printable_bounds_urx={FormatNumber(printableWidth)}\n"
            + $"printable_bounds_ury={FormatNumber(printableHeight)}\n"
            + $"printable_area={FormatNumber((double)printableWidth * printableHeight)}\n"
            + "dimensional=FALSE\n}";
    }

    private static void AddPia3RasterUserMedia(JObject data, double dpiX, double dpiY)
    {
        var mediaCaps = CreateRasterMediaCaps();
        data["mod"] = new JObject { ["media"] = mediaCaps.DeepClone() };
        data["del"] = new JObject { ["media"] = mediaCaps.DeepClone() };
        data["hidden"] = new JObject { ["media"] = mediaCaps.DeepClone() };

        var descriptions = new JObject();
        var sizes = new JObject();
        var index = 0;
        foreach (var paper in RasterUserPapers(dpiX, dpiY))
        {
            var id = index.ToString(CultureInfo.InvariantCulture);
            descriptions[id] = CreatePia3RasterDescription(paper);
            sizes[id] = CreatePia3RasterSize(paper);
            index++;
        }

        var udmMedia = (JObject)mediaCaps.DeepClone();
        udmMedia["description"] = descriptions;
        udmMedia["size"] = sizes;
        data["udm"] = new JObject
        {
            ["calibration"] = new JObject { ["_x"] = 1.0, ["_y"] = 1.0 },
            ["media"] = udmMedia
        };
    }

    private static JObject CreateRasterMediaCaps() => new()
    {
        ["abilities"] = "500505500500505555000005550000000550000500000500555",
        ["caps_state"] = "000000000000000000000000000000000000000000000000000",
        ["ui_owner"] = "11111111111111111111110",
        ["size_max_x"] = 320000.0,
        ["size_max_y"] = 320000.0,
        ["max_roll_height"] = 320000.0
    };

    private static JObject CreatePia3RasterSize(RasterPaperSpec paper) => new()
    {
        ["caps_type"] = 2,
        ["name"] = $"UserDefinedRaster ({FormatPixels(paper.WidthPixels)} x {FormatPixels(paper.HeightPixels)}Pixels)",
        ["localized_name"] = $"{paper.Name} ({FormatPixels(paper.WidthPixels)} x {FormatPixels(paper.HeightPixels)} Pixels)",
        ["media_description_name"] = RasterMediaDescriptionName(paper),
        ["media_group"] = 16,
        ["landscape_mode"] = paper.WidthPixels >= paper.HeightPixels
    };

    private static JObject CreatePia3RasterDescription(RasterPaperSpec paper)
    {
        var printableWidth = Math.Max(0, paper.WidthPixels - 1);
        var printableHeight = Math.Max(0, paper.HeightPixels - 1);
        return new JObject
        {
            ["caps_type"] = 2,
            ["name"] = RasterMediaDescriptionName(paper),
            ["media_bounds_urx"] = paper.WidthPixels,
            ["media_bounds_ury"] = paper.HeightPixels,
            ["printable_bounds_llx"] = 0.0,
            ["printable_bounds_lly"] = 0.0,
            ["printable_bounds_urx"] = printableWidth,
            ["printable_bounds_ury"] = printableHeight,
            ["printable_area"] = (double)printableWidth * printableHeight,
            ["dimensional"] = false
        };
    }

    private static string RasterMediaDescriptionName(RasterPaperSpec paper)
    {
        var width = paper.WidthPixels;
        var height = paper.HeightPixels;
        var orientation = width >= height ? "Landscape" : "Portrait";
        return $"UserDefinedRaster {orientation} {FormatPixels(width)}W x {FormatPixels(height)}H - "
            + $"(0, 0) x ({Math.Max(0, width - 1)}, {Math.Max(0, height - 1)}) ={(long)width * height} Pixels";
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

    private static string FormatPixels(int value) => value.ToString("0.00", CultureInfo.InvariantCulture);

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
            if (TryReadPia3Json(raw, out var root))
                return root["data"]?["meta"]?["driver_pathname"]?.Value<string>() ?? "";

            return new PlotterConfiguration(pc3Path).DriverPath ?? "";
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
