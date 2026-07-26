using System;
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
        public string TargetPlotterDirectory { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public static InstallResult InstallBundledPlotter()
    {
        var result = new InstallResult();
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

            // PMP 可能包含用户或本插件动态注册的纸张，已有有效配置时不得覆盖。
            if (IsUsableFile(targetPc5) && IsUsableFile(targetPmp))
            {
                result.Installed = true;
                result.Message = "LA_pdf 打印机配置已存在，已保留现有 PC5/PMP。";
                return result;
            }

            File.Copy(sourcePmp, targetPmp, overwrite: true);
            InstallPc5(sourcePc5, targetPc5, targetPmp);

            result.Installed = File.Exists(targetPc5) && File.Exists(targetPmp);
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

    public static string InstallPngPlotter()
    {
        return InstallRasterPlotter(
            PreferredPngPlotter,
            PreferredPngPmp,
            new[] { "ZWCAD Virtual PNG Plotter.pc5", "ZWPLOT_PNG.pc5" },
            name => name.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0,
            PreferredPngPlotter);
    }

    public static string InstallJpgPlotter()
    {
        return InstallRasterPlotter(
            PreferredJpgPlotter,
            PreferredJpgPmp,
            new[] { "ZWCAD Virtual JPEG Plotter.pc5", "ZWPLOT_JPG.pc5" },
            name => name.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0,
            PreferredJpgPlotter);
    }

    private static string InstallRasterPlotter(
        string targetPlotterName,
        string targetPmpName,
        string[] preferredNames,
        Func<string, bool> fallbackPredicate,
        string excludedGeneratedName)
    {
        try
        {
            var targetRoot = GetAutoCadPlotterDirectory();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                return "";
            }

            var bundledRoot = FindBundledPlotterRoot();
            var bundledPmp = string.IsNullOrWhiteSpace(bundledRoot)
                ? ""
                : Path.Combine(bundledRoot, "PMP Files", PreferredPmp);
            if (string.IsNullOrWhiteSpace(bundledPmp) || !IsUsableFile(bundledPmp))
            {
                return "";
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
                return "";
            }

            // 软件自有 PNG/JPG PC5 继承对应栅格驱动，但纸张始终来自随插件发布的独立 PMP。
            // 覆盖范围仅限 LA_png/LA_jpg，可修复旧版曾错误复制的栅格配置，不影响用户其他设备。
            File.Copy(bundledPmp, targetPmp, overwrite: true);
            var encoding = System.Text.Encoding.GetEncoding(936);
            var text = File.ReadAllText(sourcePc5, encoding);
            text = Regex.Replace(text, @"(?im)^pmp_filepath=.*$", "pmp_filepath=" + targetPmp);
            File.WriteAllText(targetPc5, text, encoding);
            return IsUsableFile(targetPc5) && IsUsableFile(targetPmp) ? targetPlotterName : "";
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
            // ZWCAD 没有公开的全局 PC5 刷新入口，通过临时 PlotSettings 强制重读设备/纸张列表。
            using var settings = new ZwSoft.ZwCAD.DatabaseServices.PlotSettings(true);
            var validator = ZwSoft.ZwCAD.DatabaseServices.PlotSettingsValidator.Current;
            validator.SetPlotConfigurationName(settings, "None", null);
            validator.RefreshLists(settings);
        }
        catch
        {
            // 调用方会检查 LA_png/LA_jpg 是否出现在当前枚举中；失败时明确报错，不回退自带设备。
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

            var targetPmpDir = Path.Combine(targetRoot, "PMP Files");
            var sourcePmp = Path.Combine(targetPmpDir, PreferredPmp);
            var targetPmp = Path.Combine(targetPmpDir, PreferredDwfPmp);
            var targetPc5 = Path.Combine(targetRoot, PreferredDwfPlotter);
            // DWF 正留白会向 LA_dwf.pmp 注册扩大纸张；重复打开窗口时不能再用 LA_pdf.pmp 覆盖。
            if (IsUsableFile(targetPc5) && IsUsableFile(targetPmp))
            {
                return PreferredDwfPlotter;
            }

            if (!File.Exists(sourcePmp))
            {
                return "";
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
                return File.Exists(targetPc5) ? PreferredDwfPlotter : "";
            }

            var encoding = System.Text.Encoding.GetEncoding(936);
            var text = File.ReadAllText(sourcePc5, encoding);
            text = Regex.Replace(text, @"(?im)^pmp_filepath=.*$", "pmp_filepath=" + targetPmp);
            File.WriteAllText(targetPc5, text, encoding);
            return File.Exists(targetPc5) && File.Exists(targetPmp) ? PreferredDwfPlotter : "";
        }
        catch
        {
            return "";
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

    private static void InstallPc5(string source, string target, string targetPmp)
    {
        var encoding = System.Text.Encoding.GetEncoding(936);
        var text = File.ReadAllText(source, encoding).Replace("{PMP_PATH}", targetPmp);
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? "");
        File.WriteAllText(target, text, encoding);
    }
}
