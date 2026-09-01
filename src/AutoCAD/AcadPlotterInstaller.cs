using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PiaNO;
using Autodesk.AutoCAD.PlottingServices;
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

    public sealed class PmpAttachmentResult
    {
        public bool Success { get; set; }
        public bool Changed { get; set; }
        public string ActivePlotterPath { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public static InstallResult InstallBundledPlotter()
    {
        var result = new InstallResult();
        try
        {
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

            // 不转换、不合并既有 LA PIA。每次都从随包 PIA2 基准重新生成，
            // 只读取当前 CAD 自带绘图仪的驱动路径，并覆盖插件自有 LA_pdf。
            if (!TryInstallForcedPia2PdfPlotter(
                    targetRoot,
                    targetPc3,
                    targetPmp,
                    out var installMessage))
            {
                result.Message = installMessage;
                return result;
            }
            const bool pia2WasWritten = true;

            result.SourceFound = true;
            if (!IsReadablePia2PlotterFile(targetPc3)
                || !IsReadablePia2PlotterFile(targetPmp))
            {
                result.Message = "LA_pdf 生成后不是 PIA2，已停止安装。";
                return result;
            }

            if (!EnsurePmpAttachment(
                    targetPc3,
                    targetPmp,
                    forceRewrite: pia2WasWritten,
                    out var attachmentMessage))
            {
                result.Message = "LA_pdf PIA2 关联失败: " + attachmentMessage;
                return result;
            }

            var activeAttachment = EnsureActivePdfPmpAttachment(
                targetPc3,
                targetPmp,
                forceRewrite: pia2WasWritten);
            result.Installed = activeAttachment.Success;
            result.Message = activeAttachment.Success
                ? "LA_pdf 已由随包模板重新生成并固定为 PIA2；"
                  + activeAttachment.Message
                : "LA_pdf 实际打印机关联失败: " + activeAttachment.Message;
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
        return InstallRasterPlotter(PreferredPngPlotter, PreferredPngPmp);
    }

    public static string InstallJpgPlotter()
    {
        return InstallRasterPlotter(PreferredJpgPlotter, PreferredJpgPmp);
    }

    private static string InstallRasterPlotter(
        string targetPlotterName,
        string targetPmpName)
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

            var metadataSource = FindReadOnlyMetadataSource(targetRoot, targetPlotterName);
            return TryWriteLaPia2PairFromTemplate(
                       targetPlotterName,
                       targetPc3,
                       targetPmp,
                       metadataSource,
                       out _)
                ? targetPlotterName
                : "";
        }
        catch
        {
            return "";
        }
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

            var sourcePc3 = FindReadOnlyMetadataSource(targetRoot, PreferredDwfPlotter);
            return TryWriteLaPia2PairFromTemplate(
                       PreferredDwfPlotter,
                       targetPc3,
                       targetPmp,
                       sourcePc3,
                       out _)
                ? PreferredDwfPlotter
                : "";
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
    /// AutoCAD 2027 的 PIA2 会长期缓存 PC3/PMP 的介质目录。
    /// 单张打印复用 PMP 中已有的任意纸张时，即使文件没有新增节点，也必须重写关联并刷新设备；
    /// 2019 虽然也是 PIA2，但不需要承担 2027 的额外刷新开销。
    /// </summary>
    public static bool RequiresRefreshForReusedCustomPaper(string pc3Path)
    {
        if (!IsPia2PlotterFile(pc3Path))
            return false;

        var acadVersion = GetSystemVariableString("ACADVER");
        var match = System.Text.RegularExpressions.Regex.Match(acadVersion, @"(?<version>\d+(?:\.\d+)?)");
        return match.Success
               && double.TryParse(
                   match.Groups["version"].Value,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var release)
               && release >= 26d;
    }

    /// <summary>
    /// 解析 AutoCAD 按设备名实际加载的 PC3 完整路径。
    /// 2027 迁移旧配置后可能出现多个同名 LA_pdf.pc3；打印 API 只接收设备名时，
    /// 不能再假设实际设备就是 ROAMABLEROOTPREFIX\Plotters 下的同名文件。
    /// </summary>
    public static string ResolveActivePlotterPath(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return "";

        try
        {
            using var config = PlotConfigManager.SetCurrentConfig(Path.GetFileName(deviceName));
            var resolved = config?.FullPath ?? "";
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                return Path.GetFullPath(resolved);
        }
        catch
        {
            // 设备列表尚未刷新时回退到当前配置目录，随后由调用方刷新并重试打印。
        }

        var plottersDirectory = GetAutoCadPlotterDirectory();
        return string.IsNullOrWhiteSpace(plottersDirectory)
            ? ""
            : Path.Combine(plottersDirectory, Path.GetFileName(deviceName));
    }

    /// <summary>
    /// 切换插件自有 PDF/DWF 绘图仪的 TrueType 输出方式。PIA2/PIA3 均同时维护
    /// truetype_as_text；PIA3 额外维护 All_As_Geometry，且只允许写当前 Plotters 目录内的 LA 文件。
    /// </summary>
    public static PlotTextGeometryModeResult ApplyTextGeometryMode(string deviceName, bool convertToGeometry)
    {
        var result = new PlotTextGeometryModeResult();
        var fileName = Path.GetFileName(deviceName ?? "");
        if (string.Equals(fileName, PreferredPngPlotter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, PreferredJpgPlotter, StringComparison.OrdinalIgnoreCase))
        {
            result.Success = true;
            result.Message = "PNG/JPG 已按像素输出，无需转换文字。";
            return result;
        }

        if (!string.Equals(fileName, PreferredPdfPlotter, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fileName, PreferredDwfPlotter, StringComparison.OrdinalIgnoreCase))
        {
            result.Success = !convertToGeometry;
            result.Message = convertToGeometry
                ? "文字转图形仅支持插件自有 LA_pdf/LA_dwf 绘图仪。"
                : "当前绘图仪不需要恢复插件文字输出设置。";
            return result;
        }

        try
        {
            var pc3Path = ResolveActivePlotterPath(fileName);
            if (!IsValidPlotterFile(pc3Path)
                || !IsAllowedLaPlotterPath(pc3Path)
                || !string.Equals(Path.GetFileName(pc3Path), fileName, StringComparison.OrdinalIgnoreCase))
            {
                result.Message = "拒绝修改非插件目录或无效的绘图仪配置：" + pc3Path;
                return result;
            }

            var attachedPmp = ReadAttachedPmpPath(pc3Path);
            result.Changed = ApplyTextGeometryToPiaFile(pc3Path, convertToGeometry, updateAllAsGeometry: true);
            if (IsValidPlotterFile(attachedPmp)
                && IsAllowedLaPlotterPath(attachedPmp)
                && Path.GetFileNameWithoutExtension(attachedPmp).StartsWith("LA_", StringComparison.OrdinalIgnoreCase))
            {
                result.Changed |= ApplyTextGeometryToPiaFile(attachedPmp, convertToGeometry, updateAllAsGeometry: false);
            }

            if (result.Changed)
            {
                PlotConfigManager.RefreshList(RefreshCode.RefreshPC3DevicesList);
            }

            result.Success = true;
            result.Message = convertToGeometry ? "TrueType 文字将按图形输出。" : "TrueType 文字将按文字输出。";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = "切换文字输出模式失败：" + ex.Message;
            return result;
        }
    }

    private static bool ApplyTextGeometryToPiaFile(
        string path,
        bool convertToGeometry,
        bool updateAllAsGeometry)
    {
        var raw = File.ReadAllText(path);
        if (PlotTextGeometryFileUpdater.TryUpdatePia3(
                raw,
                convertToGeometry,
                updateAllAsGeometry,
                out var updatedPia3,
                out var changed))
        {
            if (changed)
            {
                File.WriteAllText(path, updatedPia3);
            }
            return changed;
        }

        var config = new PlotterConfiguration(path);
        var pia2Changed = config.TruetypeAsText == convertToGeometry;
        if (pia2Changed)
        {
            config.TruetypeAsText = !convertToGeometry;
        }

        if (updateAllAsGeometry)
        {
            try
            {
                var currentAllAsGeometry = config.GetCustomValue<bool>("All_As_Geometry");
                if (currentAllAsGeometry != convertToGeometry)
                {
                    config.SetCustomValue("All_As_Geometry", convertToGeometry);
                    pia2Changed = true;
                }
            }
            catch
            {
                // 部分旧 PIA2 没有该自定义节点；truetype_as_text 仍是驱动的主控制字段。
            }
        }

        if (pia2Changed)
        {
            config.Saves(path);
        }
        return pia2Changed;
    }

    /// <summary>
    /// 读取 PC3 当前关联的 PMP。介质缓存必须跟踪实际加载 PC3 的 PMP，
    /// 不能用推测目录的时间戳代替，否则同名 PC3 会让缓存长期保存错误介质目录。
    /// </summary>
    public static string ReadAttachedPmpPath(string pc3Path)
    {
        if (!IsValidPlotterFile(pc3Path))
            return "";

        try
        {
            var raw = File.ReadAllText(pc3Path);
            if (TryReadPia3Json(raw, out var root))
                return root["data"]?["meta"]?["user_defined_model_pathname"]?.Value<string>() ?? "";

            return new PlotterConfiguration(pc3Path).ModelPath ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 同时修正程序维护的 PC3 与 AutoCAD 实际解析到的同名 PC3。
    /// 只允许修改打印机配置搜索路径内或当前 CAD 用户配置根目录内、文件名为 LA_pdf.pc3 的插件自有配置，
    /// 不删除重复文件，也不改 PrinterConfigPath 等用户全局设置。
    /// </summary>
    public static PmpAttachmentResult EnsureActivePdfPmpAttachment(
        string configuredPc3Path,
        string pmpPath,
        bool forceRewrite)
    {
        var result = new PmpAttachmentResult();
        try
        {
            if (!string.Equals(
                    Path.GetFileName(configuredPc3Path),
                    PreferredPdfPlotter,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(pmpPath),
                    PreferredPmp,
                    StringComparison.OrdinalIgnoreCase)
                || !IsReadablePia2PlotterFile(configuredPc3Path)
                || !IsReadablePia2PlotterFile(pmpPath))
            {
                result.Message = "仅允许用 PIA2 LA_pdf.pc3/LA_pdf.pmp 同步 LA 系列配置。";
                return result;
            }

            var activePath = ResolveActivePlotterPath(PreferredPdfPlotter);
            result.ActivePlotterPath = activePath;
            var targets = new[] { configuredPc3Path, activePath }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targets.Count == 0)
            {
                result.Message = "未找到 LA_pdf.pc3。";
                return result;
            }

            foreach (var target in targets)
            {
                if (!string.Equals(
                        Path.GetFileName(target),
                        PreferredPdfPlotter,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Message = "拒绝修改非 LA_pdf.pc3 文件: " + target;
                    return result;
                }

                if (!IsAllowedLaPlotterPath(target))
                {
                    result.Message =
                        "AutoCAD 实际加载的 LA_pdf.pc3 不在打印机配置搜索路径或当前 CAD 用户配置目录内，已停止修改: "
                        + target;
                    return result;
                }

                var before = File.Exists(target) ? File.ReadAllBytes(target) : Array.Empty<byte>();
                var targetWasReplaced = false;
                if (!string.Equals(
                        Path.GetFullPath(target),
                        Path.GetFullPath(configuredPc3Path),
                        StringComparison.OrdinalIgnoreCase))
                {
                    // AutoCAD 可能解析到迁移目录中的旧同名 LA_pdf。
                    // 不检查也不转换旧内容，直接用刚生成的 PIA2 LA_pdf 替换该插件自有副本。
                    File.Copy(configuredPc3Path, target, overwrite: true);
                    targetWasReplaced = true;
                }

                if (!EnsurePmpAttachment(
                        target,
                        pmpPath,
                        forceRewrite || targetWasReplaced,
                        out var targetMessage))
                {
                    result.Message = target + ": " + targetMessage;
                    return result;
                }

                var after = File.ReadAllBytes(target);
                result.Changed |= !before.SequenceEqual(after);
            }

            result.Success = true;
            result.Message =
                $"实际PC3={activePath}; PMP={Path.GetFullPath(pmpPath)}; 已同步={result.Changed}";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            return result;
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
        if (!string.Equals(
                Path.GetFileName(pc3Path),
                PreferredPdfPlotter,
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.GetFileName(pmpPath),
                PreferredPmp,
                StringComparison.OrdinalIgnoreCase))
        {
            message = "仅允许更新 LA_pdf.pc3/LA_pdf.pmp 的关联字段。";
            return false;
        }

        if (!IsReadablePia2PlotterFile(pc3Path)
            || !IsReadablePia2PlotterFile(pmpPath))
        {
            message = "LA_pdf.pc3 或 LA_pdf.pmp 不是可读取的 PIA2 文件。";
            return false;
        }

        try
        {
            var fullPmpPath = Path.GetFullPath(pmpPath);
            var expectedBase = Path.GetFileNameWithoutExtension(fullPmpPath);
            // 实际加载的 LA_pdf.pc3 可能位于 2027 迁移生成的嵌套目录，并保留着 2024 驱动。
            // 驱动来源必须跟随本次权威 PMP 所在的当前 CAD Plotters 根目录，不能取 PC3 的旧同级文件。
            var pmpDirectory = Path.GetDirectoryName(fullPmpPath) ?? "";
            var authoritativePlottersDirectory =
                string.Equals(Path.GetFileName(pmpDirectory), "PMP Files", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(pmpDirectory) ?? ""
                    : "";
            var sourceDriverPath = ReadDriverPath(
                Path.Combine(authoritativePlottersDirectory, "DWG To PDF.pc3"));
            if (string.IsNullOrWhiteSpace(sourceDriverPath))
            {
                var plottersDirectory = Path.GetDirectoryName(pc3Path) ?? "";
                sourceDriverPath = ReadDriverPath(Path.Combine(plottersDirectory, "DWG To PDF.pc3"));
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

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(directory))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 以随包 PIA2 为唯一结构来源生成 LA 文件；当前 CAD PC3 只读取驱动路径，
    /// 不读取、不复制、不转换既有 LA PIA，也不写回 DWG To PDF/PublishToWeb/DWF。
    /// </summary>
    private static bool TryWriteLaPia2PairFromTemplate(
        string deviceName,
        string targetPc3,
        string targetPmp,
        string metadataSourcePc3,
        out string message)
    {
        message = "";
        if (!IsSupportedLaPlotter(deviceName))
        {
            message = "拒绝生成非 LA 系列绘图仪。";
            return false;
        }

        var templatePc3 = GetBundledPia2Template(deviceName, out var templatePmp);
        if (!IsReadablePia2PlotterFile(templatePc3)
            || !IsReadablePia2PlotterFile(templatePmp))
        {
            message = "缺少或无法读取随包 PIA2 模板: " + deviceName;
            return false;
        }

        var sourceDriverPath = ReadDriverPath(metadataSourcePc3);
        if (string.IsNullOrWhiteSpace(sourceDriverPath))
        {
            message = "无法从当前 CAD 自带绘图仪读取驱动路径，已停止替换 LA 配置。";
            return false;
        }

        var token = Guid.NewGuid().ToString("N");
        var tempPc3 = targetPc3 + ".new-" + token;
        var tempPmp = targetPmp + ".new-" + token;
        var backupPc3 = targetPc3 + ".backup-" + token;
        var backupPmp = targetPmp + ".backup-" + token;

        try
        {
            // PC3/PMP 的全部结构和纸张表均以资源文件为准，不在代码中重新生成。
            File.Copy(templatePc3, tempPc3, overwrite: true);
            File.Copy(templatePmp, tempPmp, overwrite: true);

            // 资源模板中的低版本硬编码路径不能带入用户环境；
            // 只修正驱动路径、PMP 完整路径和 basename。
            EnsurePia2MetaFile(
                tempPc3,
                Path.GetFullPath(targetPmp),
                Path.GetFileNameWithoutExtension(targetPmp),
                sourceDriverPath,
                forceRewrite: true);
            EnsurePia2MetaFile(
                tempPmp,
                Path.GetFullPath(targetPmp),
                Path.GetFileNameWithoutExtension(targetPmp),
                sourceDriverPath,
                forceRewrite: true);

            if (!IsReadablePia2PlotterFile(tempPc3)
                || !IsReadablePia2PlotterFile(tempPmp)
                || !HasCompleteBundledMediaCatalog(deviceName, tempPmp))
                throw new InvalidDataException("临时 LA PC3/PMP 未通过完整 PIA2 解析校验。");

            if (File.Exists(targetPc3))
                File.Copy(targetPc3, backupPc3, overwrite: true);
            if (File.Exists(targetPmp))
                File.Copy(targetPmp, backupPmp, overwrite: true);
            File.Copy(tempPc3, targetPc3, overwrite: true);
            File.Copy(tempPmp, targetPmp, overwrite: true);

            if (!IsReadablePia2PlotterFile(targetPc3)
                || !IsReadablePia2PlotterFile(targetPmp)
                || !HasCompleteBundledMediaCatalog(deviceName, targetPmp))
                throw new InvalidDataException("最终 LA PC3/PMP 未通过完整 PIA2 解析校验。");

            message = $"LA {deviceName} 已直接由固定 PIA2 模板生成。";
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(backupPc3))
                    File.Copy(backupPc3, targetPc3, overwrite: true);
                if (File.Exists(backupPmp))
                    File.Copy(backupPmp, targetPmp, overwrite: true);
            }
            catch
            {
                // 恢复失败时仍停止，不得转而修改用户的系统绘图仪。
            }

            message = "LA PIA2 模板生成失败，已停止且未修改其他绘图仪: " + ex.Message;
            return false;
        }
        finally
        {
            DeleteTemporaryFile(tempPc3);
            DeleteTemporaryFile(tempPmp);
            DeleteTemporaryFile(backupPc3);
            DeleteTemporaryFile(backupPmp);
        }
    }

    /// <summary>
    /// 新安装同样只以随包 PIA2 为结构来源，当前 DWG To PDF.pc3 仅提供驱动路径。
    /// 误删该文件时回退到 Plotters 目录下其他含有 PDF 驱动的 pc3，避免插件完全不可用。
    /// </summary>
    private static bool TryInstallForcedPia2PdfPlotter(
        string plottersDirectory,
        string targetPc3,
        string targetPmp,
        out string message)
    {
        var sourcePc3 = Path.Combine(plottersDirectory, "DWG To PDF.pc3");
        if (!File.Exists(sourcePc3))
        {
            sourcePc3 = FindPdfDriverPc3(plottersDirectory)
                ?? sourcePc3; // 实在找不到时仍用原名，让 TryWriteLaPia2PairFromTemplate 给出明确错误
        }

        return TryWriteLaPia2PairFromTemplate(
            PreferredPdfPlotter,
            targetPc3,
            targetPmp,
            sourcePc3,
            out message);
    }

    /// <summary>
    /// 在 Plotters 目录中搜索任意包含 PDF 驱动路径的 pc3 文件，作为 DWG To PDF 误删后的回退。
    /// </summary>
    private static string? FindPdfDriverPc3(string plottersDirectory)
    {
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(plottersDirectory, "*.pc3", SearchOption.TopDirectoryOnly))
            {
                var driverPath = ReadDriverPath(candidate);
                if (!string.IsNullOrWhiteSpace(driverPath)
                    && driverPath.IndexOf("pdf", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // 目录不可读时不做回退，后续仍由 TryWriteLaPia2PairFromTemplate 报明确错误。
        }

        return null;
    }

    private static bool IsSupportedLaPlotter(string deviceName)
    {
        return string.Equals(deviceName, PreferredPdfPlotter, StringComparison.OrdinalIgnoreCase)
               || string.Equals(deviceName, PreferredPngPlotter, StringComparison.OrdinalIgnoreCase)
               || string.Equals(deviceName, PreferredJpgPlotter, StringComparison.OrdinalIgnoreCase)
               || string.Equals(deviceName, PreferredDwfPlotter, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetBundledPia2Template(string deviceName, out string pmpPath)
    {
        pmpPath = "";
        var sourceRoot = FindBundledPlotterRoot();
        if (string.IsNullOrWhiteSpace(sourceRoot))
            return "";

        var pc3Path = Path.Combine(sourceRoot, "PIA2", deviceName);
        pmpPath = Path.Combine(
            sourceRoot,
            "PIA2",
            "PMP Files",
            Path.GetFileNameWithoutExtension(deviceName) + ".pmp");
        return pc3Path;
    }

    /// <summary>
    /// 验证生成结果仍完整包含资源模板的全部基准介质。
    /// </summary>
    private static bool HasCompleteBundledMediaCatalog(string deviceName, string targetPmp)
    {
        if (!IsSupportedLaPlotter(deviceName))
            return false;

        _ = GetBundledPia2Template(deviceName, out var templatePmp);
        if (!TryReadPia2MediaDimensions(templatePmp, out var required)
            || !TryReadPia2MediaDimensions(targetPmp, out var actual)
            || required.Count == 0)
        {
            return false;
        }

        return required.All(expected =>
            actual.Any(candidate =>
                Math.Abs(candidate.Width - expected.Width) <= 0.01d
                && Math.Abs(candidate.Height - expected.Height) <= 0.01d));
    }

    private static bool TryReadPia2MediaDimensions(
        string pmpPath,
        out List<(double Width, double Height)> result)
    {
        result = new List<(double Width, double Height)>();
        if (!IsReadablePia2PlotterFile(pmpPath))
            return false;

        try
        {
            var config = new PlotterConfiguration(pmpPath);
            var descriptions = config["udm"]?["media"]?["description"];
            if (descriptions == null)
                return false;

            foreach (var description in descriptions.ChildNodes)
            {
                if (!description.NodeMap.TryGetValue("caps_type", out var capsType)
                    || !string.Equals(capsType, "2", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var width = ReadPia2Number(description, "media_bounds_urx");
                var height = ReadPia2Number(description, "media_bounds_ury");
                if (width > 0d && height > 0d)
                    result.Add((width, height));
            }

            return true;
        }
        catch
        {
            result.Clear();
            return false;
        }
    }

    private static bool IsReadablePia2PlotterFile(string path)
    {
        if (!IsPia2PlotterFile(path))
            return false;

        try
        {
            _ = new PlotterConfiguration(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FindReadOnlyMetadataSource(string plottersDirectory, string deviceName)
    {
        if (string.IsNullOrWhiteSpace(plottersDirectory) || !Directory.Exists(plottersDirectory))
            return "";

        if (string.Equals(deviceName, PreferredPdfPlotter, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(plottersDirectory, "DWG To PDF.pc3");

        var sources = Directory.EnumerateFiles(plottersDirectory, "*.pc3", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith("LA_", StringComparison.OrdinalIgnoreCase));
        if (string.Equals(deviceName, PreferredPngPlotter, StringComparison.OrdinalIgnoreCase))
        {
            return sources.FirstOrDefault(path =>
                       string.Equals(
                           Path.GetFileName(path),
                           "PublishToWeb PNG.pc3",
                           StringComparison.OrdinalIgnoreCase))
                   ?? "";
        }

        if (string.Equals(deviceName, PreferredJpgPlotter, StringComparison.OrdinalIgnoreCase))
        {
            return sources.FirstOrDefault(path =>
                       string.Equals(
                           Path.GetFileName(path),
                           "PublishToWeb JPG.pc3",
                           StringComparison.OrdinalIgnoreCase))
                   ?? "";
        }

        return sources
                   .OrderBy(path =>
                       string.Equals(
                           Path.GetFileName(path),
                           "DWF6 ePlot.pc3",
                           StringComparison.OrdinalIgnoreCase)
                           ? 0
                           : 1)
                   .FirstOrDefault(path =>
                       Path.GetFileName(path).IndexOf("DWF", StringComparison.OrdinalIgnoreCase) >= 0
                       && Path.GetFileName(path).IndexOf("DWFx", StringComparison.OrdinalIgnoreCase) < 0)
               ?? "";
    }

    private static double ReadPia2Number(PiaNode node, string key)
    {
        return node.NodeMap.TryGetValue(key, out var value)
               && double.TryParse(
                   value,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out var number)
            ? number
            : 0d;
    }

    private static void DeleteTemporaryFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 临时文件清理由系统后续回收；不得因此回写或删除用户绘图仪配置。
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
        // 覆盖 1L～4L；A1+3 等图幅的总长为基础图幅 4 倍，3L 上限会因长宽比不足产生留白。
        var multipliers = Enumerable.Range(8, 25).Select(unit => unit / 8d);

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
            if (File.Exists(Path.Combine(candidate, "PIA2", PreferredPdfPlotter)))
            {
                return candidate;
            }

            candidate = Path.Combine(root, "resources", "acad", "Plotters");
            if (File.Exists(Path.Combine(candidate, "PIA2", PreferredPdfPlotter)))
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

    /// <summary>
    /// 解析插件应写入的 Plotters 目录。
    /// 读取选项中的全部打印机配置搜索路径，不要求目标必须是第一项；
    /// 优先使用仍存在的默认 <c>ROAMABLEROOTPREFIX\Plotters</c>，再回退到列表中其它存在的目录。
    /// </summary>
    private static string GetAutoCadPlotterDirectory()
    {
        var configuredDirectories = GetExistingPrinterConfigDirectories();
        var roamableRoot = GetSystemVariableString("ROAMABLEROOTPREFIX");
        var preferredPlotters = string.IsNullOrWhiteSpace(roamableRoot)
            ? ""
            : Path.Combine(roamableRoot, "Plotters");

        if (!string.IsNullOrWhiteSpace(preferredPlotters))
        {
            foreach (var directory in configuredDirectories)
            {
                if (PathsEqual(directory, preferredPlotters))
                    return Path.GetFullPath(directory);
            }

            foreach (var directory in configuredDirectories)
            {
                if (IsPathInsideDirectory(directory, roamableRoot))
                    return directory;
            }
        }

        if (configuredDirectories.Count > 0)
            return configuredDirectories[0];

        // Core Console 无 COM，或搜索路径暂时读不到时，回退到当前 CAD 用户默认 Plotters。
        if (!string.IsNullOrWhiteSpace(preferredPlotters) && Directory.Exists(preferredPlotters))
            return Path.GetFullPath(preferredPlotters);

        return "";
    }

    /// <summary>
    /// 枚举选项里已配置且磁盘上存在的打印机配置搜索路径，保留用户设定顺序。
    /// </summary>
    private static IReadOnlyList<string> GetExistingPrinterConfigDirectories()
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var printerConfigPath = ReadPrinterConfigPathFromPreferences();
        foreach (var configuredPath in ExpandPrinterConfigPaths(printerConfigPath))
        {
            if (!Directory.Exists(configuredPath))
                continue;

            try
            {
                var fullPath = Path.GetFullPath(configuredPath);
                if (seen.Add(fullPath))
                    directories.Add(fullPath);
            }
            catch
            {
                // 单个无效路径不影响检查其余搜索目录。
            }
        }

        return directories;
    }

    /// <summary>
    /// 判断 LA 绘图仪文件是否允许由插件修改：位于用户配置的任一打印机搜索目录内，
    /// 或位于当前 CAD 用户配置根目录内即可，不要求必须是搜索路径第一项。
    /// </summary>
    private static bool IsAllowedLaPlotterPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var currentCadRoot = GetSystemVariableString("ROAMABLEROOTPREFIX");
        if (IsPathInsideDirectory(path, currentCadRoot))
            return true;

        foreach (var directory in GetExistingPrinterConfigDirectories())
        {
            if (IsPathInsideDirectory(path, directory))
                return true;
        }

        return false;
    }

    private static string ReadPrinterConfigPathFromPreferences()
    {
        try
        {
            var acadApplication = typeof(CadApp).InvokeMember(
                "AcadApplication",
                BindingFlags.GetProperty | BindingFlags.Static | BindingFlags.Public,
                null,
                null,
                null);
            if (acadApplication == null)
                return "";

            var preferences = acadApplication.GetType().InvokeMember(
                "Preferences",
                BindingFlags.GetProperty,
                null,
                acadApplication,
                null);
            if (preferences == null)
                return "";

            var files = preferences.GetType().InvokeMember(
                "Files",
                BindingFlags.GetProperty,
                null,
                preferences,
                null);
            if (files == null)
                return "";

            return files.GetType().InvokeMember(
                       "PrinterConfigPath",
                       BindingFlags.GetProperty,
                       null,
                       files,
                       null)?.ToString()
                   ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<string> ExpandPrinterConfigPaths(string configuredPaths)
    {
        if (string.IsNullOrWhiteSpace(configuredPaths))
            yield break;

        var roamableRoot = GetSystemVariableString("ROAMABLEROOTPREFIX");
        foreach (var raw in configuredPaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = raw.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(roamableRoot))
            {
                path = System.Text.RegularExpressions.Regex.Replace(
                    path,
                    "%RoamableRootFolder%",
                    _ => roamableRoot,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            path = Environment.ExpandEnvironmentVariables(path);
            if (!string.IsNullOrWhiteSpace(path))
                yield return path;
        }
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
