using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text.RegularExpressions;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

public static class AcadPlotterInstaller
{
    public const string PreferredPdfPlotter = "LA_pdf.pc5";
    public const string PreferredPngPlotter = "LA_png.pc5";
    public const string PreferredJpgPlotter = "LA_jpg.pc5";
    public const string PreferredDwfPlotter = "LA_dwf.pc5";
    private const string PreferredPmp = "LA_pdf.pmp";
    private const string PreferredPngPmp = "LA_png.pmp";
    private const string PreferredJpgPmp = "LA_jpg.pmp";
    private const string PreferredDwfPmp = "LA_dwf.pmp";

    public sealed class InstallResult
    {
        public bool SourceFound { get; set; }
        public bool Installed { get; set; }
        /// <summary>本轮是否写过 PC5/PMP；用于决定是否刷新设备列表。</summary>
        public bool Written { get; set; }
        public string DeviceName { get; set; } = "";
        public string TargetPlotterDirectory { get; set; } = "";
        public string Message { get; set; } = "";
    }

    private static bool s_devicesRefreshedThisSession;

    public static InstallResult InstallBundledPlotter()
    {
        var result = new InstallResult
        {
            DeviceName = PreferredPdfPlotter
        };
        try
        {
            var sourceRoot = FindBundledPlotterRoot();
            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                result.Message = "未找到随插件附带的 LA_pdf 打印机配置。";
                return result;
            }

            result.SourceFound = true;
            var sourcePc5 = Path.Combine(sourceRoot, PreferredPdfPlotter);
            var sourcePmp = Path.Combine(sourceRoot, "PMP Files", PreferredPmp);
            if (!File.Exists(sourcePc5) || !File.Exists(sourcePmp))
            {
                result.Message = "LA_pdf 打印机配置不完整，需要 LA_pdf.pc5 和 PMP Files\\LA_pdf.pmp。";
                return result;
            }

            var targetRoot = GetAutoCadPlotterDirectory();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                result.Message = "未能定位 ZWCAD Plotters 目录。";
                return result;
            }

            result.TargetPlotterDirectory = targetRoot;
            var targetPmpDir = Path.Combine(targetRoot, "PMP Files");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(targetPmpDir);

            var targetPc5 = Path.Combine(targetRoot, PreferredPdfPlotter);
            var targetPmp = Path.Combine(targetPmpDir, PreferredPmp);

            // PMP 可能包含用户或本插件动态注册的纸张，已有有效配置时不得覆盖；仅校正 PC5↔PMP 关联。
            if (IsUsableFile(targetPc5) && IsUsableFile(targetPmp))
            {
                var linked = EnsurePc5PmpAttachment(targetPc5, targetPmp, forceRewrite: false);
                result.Installed = true;
                result.Written = linked;
                result.Message = linked
                    ? "LA_pdf 已可用，已修正 PC5↔PMP 关联。"
                    : "LA_pdf 打印机配置已存在，已保留现有 PC5/PMP。";
                return result;
            }

            File.Copy(sourcePmp, targetPmp, overwrite: true);
            InstallPc5(sourcePc5, targetPc5, targetPmp);

            result.Installed = File.Exists(targetPc5) && File.Exists(targetPmp);
            result.Written = result.Installed;
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

    private static bool IsUsableFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static InstallResult InstallPngPlotter()
    {
        return InstallRasterPlotter(
            PreferredPngPlotter,
            PreferredPngPmp,
            new[] { "ZWCAD Virtual PNG Plotter.pc5", "ZWPLOT_PNG.pc5" },
            name => name.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0,
            PreferredPngPlotter);
    }

    public static InstallResult InstallJpgPlotter()
    {
        return InstallRasterPlotter(
            PreferredJpgPlotter,
            PreferredJpgPmp,
            new[] { "ZWCAD Virtual JPEG Plotter.pc5", "ZWPLOT_JPG.pc5" },
            name => name.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0,
            PreferredJpgPlotter);
    }

    private static InstallResult InstallRasterPlotter(
        string targetPlotterName,
        string targetPmpName,
        string[] preferredNames,
        Func<string, bool> fallbackPredicate,
        string excludedGeneratedName)
    {
        var result = new InstallResult
        {
            DeviceName = targetPlotterName
        };
        try
        {
            var targetRoot = GetAutoCadPlotterDirectory();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                result.Message = "未能定位 ZWCAD Plotters 目录。";
                return result;
            }

            result.TargetPlotterDirectory = targetRoot;
            var bundledRoot = FindBundledPlotterRoot();
            var bundledPmp = string.IsNullOrWhiteSpace(bundledRoot)
                ? ""
                : Path.Combine(bundledRoot, "PMP Files", PreferredPmp);
            if (string.IsNullOrWhiteSpace(bundledPmp) || !IsUsableFile(bundledPmp))
            {
                result.Message = "未找到随包 PMP。";
                return result;
            }

            var targetPmpDirectory = Path.Combine(targetRoot, "PMP Files");
            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(targetPmpDirectory);
            var targetPc5 = Path.Combine(targetRoot, targetPlotterName);
            var targetPmp = Path.Combine(targetPmpDirectory, targetPmpName);

            var sources = Directory.EnumerateFiles(targetRoot, "*.pc5", SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    excludedGeneratedName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            string? sourcePc5 = null;
            foreach (var preferredName in preferredNames)
            {
                sourcePc5 = sources.FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), preferredName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(sourcePc5))
                {
                    break;
                }
            }

            sourcePc5 ??= sources.FirstOrDefault(path => fallbackPredicate(Path.GetFileName(path)));
            if (string.IsNullOrWhiteSpace(sourcePc5))
            {
                result.Message = "未找到可用的栅格绘图仪源 PC5。";
                return result;
            }

            // 软件自有 PNG/JPG PC5 继承对应栅格驱动，但纸张始终来自随插件发布的独立 PMP。
            // 覆盖范围仅限 LA_png/LA_jpg，可修复旧版曾错误复制的栅格配置，不影响用户其他设备。
            File.Copy(bundledPmp, targetPmp, overwrite: true);
            var encoding = System.Text.Encoding.GetEncoding(936);
            var text = File.ReadAllText(sourcePc5, encoding);
            var fullPmpPath = Path.GetFullPath(targetPmp);
            text = Regex.Replace(text, @"(?im)^pmp_filepath=.*$", "pmp_filepath=" + fullPmpPath);
            if (!Regex.IsMatch(text, @"(?im)^pmp_filepath="))
                text = text.TrimEnd() + "\r\npmp_filepath=" + fullPmpPath + "\r\n";
            File.WriteAllText(targetPc5, text, encoding);
            result.SourceFound = true;
            result.Installed = IsUsableFile(targetPc5) && IsUsableFile(targetPmp);
            result.Written = result.Installed;
            result.Message = result.Installed
                ? targetPlotterName + " 已安装。"
                : targetPlotterName + " 安装后不可用。";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            return result;
        }
    }

    public static void RefreshPlotterDevices(bool force = false)
    {
        if (!force && s_devicesRefreshedThisSession)
            return;

        try
        {
            // ZWCAD 没有公开的全局 PC5 刷新入口，通过临时 PlotSettings 强制重读设备/纸张列表。
            using var settings = new ZwSoft.ZwCAD.DatabaseServices.PlotSettings(true);
            var validator = ZwSoft.ZwCAD.DatabaseServices.PlotSettingsValidator.Current;
            validator.SetPlotConfigurationName(settings, "None", null);
            validator.RefreshLists(settings);
            s_devicesRefreshedThisSession = true;
        }
        catch
        {
            // 调用方会检查 LA_png/LA_jpg 是否出现在当前枚举中；失败时明确报错，不回退自带设备。
        }
    }

    /// <summary>
    /// 本轮写过配置，或本会话尚未做过安装类刷新时，才刷新设备列表。
    /// </summary>
    public static void RefreshPlotterDevicesIfNeeded(bool configWritten)
    {
        RefreshPlotterDevices(force: configWritten || !s_devicesRefreshedThisSession);
    }

    /// <summary>
    /// 切换插件自有 PDF/DWF 绘图仪的 TrueType 输出方式。只修改当前 CAD 配置目录中的
    /// LA_pdf/LA_dwf，不触碰用户自建 PC5；PNG/JPG 本身是像素输出，无需处理。
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
            var plottersDirectory = GetAutoCadPlotterDirectory();
            var pc5Path = Path.Combine(plottersDirectory, fileName);
            if (string.IsNullOrWhiteSpace(plottersDirectory) || !IsUsableFile(pc5Path))
            {
                result.Message = "未找到插件绘图仪配置：" + pc5Path;
                return result;
            }

            var encoding = System.Text.Encoding.GetEncoding(936);
            var original = File.ReadAllText(pc5Path, encoding);
            var updated = PlotTextGeometryFileUpdater.UpdateZwcadPc5(
                original,
                convertToGeometry,
                out var changed);
            if (changed)
            {
                File.WriteAllText(pc5Path, updated, encoding);
                result.Changed = true;
                RefreshPlotterDevices(force: true);
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

    public static InstallResult InstallDwfPlotter()
    {
        var result = new InstallResult
        {
            DeviceName = PreferredDwfPlotter
        };
        try
        {
            var targetRoot = GetAutoCadPlotterDirectory();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                result.Message = "未能定位 ZWCAD Plotters 目录。";
                return result;
            }

            result.TargetPlotterDirectory = targetRoot;
            var targetPmpDir = Path.Combine(targetRoot, "PMP Files");
            var sourcePmp = Path.Combine(targetPmpDir, PreferredPmp);
            var targetPmp = Path.Combine(targetPmpDir, PreferredDwfPmp);
            var targetPc5 = Path.Combine(targetRoot, PreferredDwfPlotter);
            // DWF 正留白会向 LA_dwf.pmp 注册扩大纸张；重复打开窗口时不能再用 LA_pdf.pmp 覆盖。
            if (IsUsableFile(targetPc5) && IsUsableFile(targetPmp))
            {
                var linked = EnsurePc5PmpAttachment(targetPc5, targetPmp, forceRewrite: false);
                result.Installed = true;
                result.Written = linked;
                result.Message = linked
                    ? "LA_dwf 已可用，已修正 PC5↔PMP 关联。"
                    : "LA_dwf 已存在，已保留现有 PC5/PMP。";
                return result;
            }

            if (!File.Exists(sourcePmp))
            {
                result.Message = "缺少 LA_pdf.pmp，无法生成 LA_dwf。";
                return result;
            }

            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(targetPmpDir);
            File.Copy(sourcePmp, targetPmp, overwrite: true);

            var sourcePc5 = Directory.GetFiles(targetRoot, "*.pc5")
                .Where(path => !string.Equals(Path.GetFileName(path), PreferredDwfPlotter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => string.Equals(Path.GetFileName(path), "DWF6 ePlot.pc5", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault(path => Path.GetFileName(path).IndexOf("DWF", StringComparison.OrdinalIgnoreCase) >= 0
                                        && Path.GetFileName(path).IndexOf("DWFx", StringComparison.OrdinalIgnoreCase) < 0);
            if (string.IsNullOrWhiteSpace(sourcePc5))
            {
                result.Installed = File.Exists(targetPc5);
                result.Written = result.Installed;
                result.Message = result.Installed
                    ? "LA_dwf 已可用。"
                    : "未找到 DWF 源 PC5。";
                return result;
            }

            var encoding = System.Text.Encoding.GetEncoding(936);
            var text = File.ReadAllText(sourcePc5, encoding);
            var fullPmpPath = Path.GetFullPath(targetPmp);
            text = Regex.Replace(text, @"(?im)^pmp_filepath=.*$", "pmp_filepath=" + fullPmpPath);
            if (!Regex.IsMatch(text, @"(?im)^pmp_filepath="))
                text = text.TrimEnd() + "\r\npmp_filepath=" + fullPmpPath + "\r\n";
            File.WriteAllText(targetPc5, text, encoding);
            result.Installed = File.Exists(targetPc5) && File.Exists(targetPmp);
            result.Written = result.Installed;
            result.Message = result.Installed
                ? "LA_dwf 已安装。"
                : "LA_dwf 安装后不可用。";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = ex.Message;
            return result;
        }
    }

    private static string? FindBundledPlotterRoot()
    {
        foreach (var root in GetCandidateBaseDirectories())
        {
            var candidate = Path.Combine(root, "Plotters");
            if (File.Exists(Path.Combine(candidate, PreferredPdfPlotter)))
            {
                return candidate;
            }

            candidate = Path.Combine(root, "resources", "zwcad", "Plotters");
            if (File.Exists(Path.Combine(candidate, PreferredPdfPlotter)))
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
    /// 读取选项中的全部打印机配置搜索路径（及系统变量回退），不要求必须是第一项；
    /// 优先使用仍存在的默认 <c>ROAMABLEROOTPREFIX\Plotters</c>（覆盖建筑版 ZwArch/ZWCADA 等产品根），
    /// 再回退到列表中其它存在的目录。
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

        if (!string.IsNullOrWhiteSpace(preferredPlotters) && Directory.Exists(preferredPlotters))
            return Path.GetFullPath(preferredPlotters);

        return "";
    }

    /// <summary>
    /// 枚举选项/系统变量里已配置且磁盘上存在的打印机配置搜索路径，保留设定顺序。
    /// </summary>
    private static IReadOnlyList<string> GetExistingPrinterConfigDirectories()
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPaths in new[]
                 {
                     ReadPreferenceFilesPath("PrinterConfigPath"),
                     GetSystemVariableString("PrinterConfigDir")
                 })
        {
            foreach (var configuredPath in ExpandSupportPaths(rawPaths))
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
        }

        return directories;
    }

    /// <summary>
    /// 枚举打印机说明文件（PMP）搜索路径；当前安装仍写入 Plotters\PMP Files，
    /// 并以 PC5 内绝对 <c>pmp_filepath</c> 完成附着，此列表用于诊断与后续扩展。
    /// </summary>
    public static IReadOnlyList<string> GetExistingPrinterDescDirectories()
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPaths in new[]
                 {
                     ReadPreferenceFilesPath("PrinterDescPath"),
                     GetSystemVariableString("PrinterDescDir")
                 })
        {
            foreach (var configuredPath in ExpandSupportPaths(rawPaths))
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
                    // 忽略单个无效路径。
                }
            }
        }

        return directories;
    }

    /// <summary>
    /// 通过 COM Preferences.Files 读取打印机支持路径；失败时返回空串，由系统变量回退。
    /// </summary>
    private static string ReadPreferenceFilesPath(string propertyName)
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
                       propertyName,
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

    private static IEnumerable<string> ExpandSupportPaths(string configuredPaths)
    {
        if (string.IsNullOrWhiteSpace(configuredPaths))
            yield break;

        var roamableRoot = GetSystemVariableString("ROAMABLEROOTPREFIX");
        foreach (var raw in configuredPaths.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = raw.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(roamableRoot))
            {
                path = Regex.Replace(
                    path,
                    "%RoamableRootFolder%",
                    _ => roamableRoot,
                    RegexOptions.IgnoreCase);
            }

            path = Environment.ExpandEnvironmentVariables(path);
            if (!string.IsNullOrWhiteSpace(path))
                yield return path;
        }
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

    /// <summary>
    /// 确保 PC5 的 <c>pmp_filepath</c> 指向目标 PMP；已正确则返回 false。
    /// </summary>
    private static bool EnsurePc5PmpAttachment(string pc5Path, string pmpPath, bool forceRewrite)
    {
        if (!IsUsableFile(pc5Path) || !IsUsableFile(pmpPath))
            return false;

        var encoding = System.Text.Encoding.GetEncoding(936);
        var original = File.ReadAllText(pc5Path, encoding);
        var fullPmpPath = Path.GetFullPath(pmpPath);
        var match = Regex.Match(original, @"(?im)^pmp_filepath=(.*)$");
        if (match.Success
            && !forceRewrite
            && PathsEqual(match.Groups[1].Value.Trim(), fullPmpPath))
        {
            return false;
        }

        string updated;
        if (match.Success)
        {
            updated = Regex.Replace(
                original,
                @"(?im)^pmp_filepath=.*$",
                "pmp_filepath=" + fullPmpPath);
        }
        else
        {
            updated = original.TrimEnd() + "\r\npmp_filepath=" + fullPmpPath + "\r\n";
        }

        if (string.Equals(original, updated, StringComparison.Ordinal))
            return false;

        File.WriteAllText(pc5Path, updated, encoding);
        return true;
    }

    private static void InstallPc5(string source, string target, string targetPmp)
    {
        var encoding = System.Text.Encoding.GetEncoding(936);
        var fullPmpPath = Path.GetFullPath(targetPmp);
        var text = File.ReadAllText(source, encoding).Replace("{PMP_PATH}", fullPmpPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
        File.WriteAllText(target, text, encoding);
    }
}
