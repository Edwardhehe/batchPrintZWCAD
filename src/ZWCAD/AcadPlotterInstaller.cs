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

    public static string InstallPngPlotter()
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
            var targetPmp = Path.Combine(targetPmpDir, PreferredPngPmp);
            if (!File.Exists(sourcePmp))
            {
                return "";
            }

            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(targetPmpDir);
            File.Copy(sourcePmp, targetPmp, overwrite: true);

            var sourcePc5 = Directory.GetFiles(targetRoot, "*.pc5")
                .Where(path => !string.Equals(Path.GetFileName(path), PreferredPngPlotter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => string.Equals(Path.GetFileName(path), "ZWCAD Virtual PNG Plotter.pc5", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault(path => Path.GetFileName(path).IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0);
            var targetPc5 = Path.Combine(targetRoot, PreferredPngPlotter);
            if (string.IsNullOrWhiteSpace(sourcePc5))
            {
                return File.Exists(targetPc5) ? PreferredPngPlotter : "";
            }

            var encoding = System.Text.Encoding.GetEncoding(936);
            var text = File.ReadAllText(sourcePc5, encoding);
            text = Regex.Replace(text, @"(?im)^pmp_filepath=.*$", "pmp_filepath=" + targetPmp);
            File.WriteAllText(targetPc5, text, encoding);
            return File.Exists(targetPc5) && File.Exists(targetPmp) ? PreferredPngPlotter : "";
        }
        catch
        {
            return "";
        }
    }

    public static string InstallJpgPlotter()
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
            var targetPmp = Path.Combine(targetPmpDir, PreferredJpgPmp);
            if (!File.Exists(sourcePmp))
            {
                return "";
            }

            Directory.CreateDirectory(targetRoot);
            Directory.CreateDirectory(targetPmpDir);
            File.Copy(sourcePmp, targetPmp, overwrite: true);

            var sourcePc5 = Directory.GetFiles(targetRoot, "*.pc5")
                .Where(path => !string.Equals(Path.GetFileName(path), PreferredJpgPlotter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => string.Equals(Path.GetFileName(path), "ZWCAD Virtual JPEG Plotter.pc5", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault(path => Path.GetFileName(path).IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
                                        || Path.GetFileName(path).IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0);
            var targetPc5 = Path.Combine(targetRoot, PreferredJpgPlotter);
            if (string.IsNullOrWhiteSpace(sourcePc5))
            {
                return File.Exists(targetPc5) ? PreferredJpgPlotter : "";
            }

            var encoding = System.Text.Encoding.GetEncoding(936);
            var text = File.ReadAllText(sourcePc5, encoding);
            text = Regex.Replace(text, @"(?im)^pmp_filepath=.*$", "pmp_filepath=" + targetPmp);
            File.WriteAllText(targetPc5, text, encoding);
            return File.Exists(targetPc5) && File.Exists(targetPmp) ? PreferredJpgPlotter : "";
        }
        catch
        {
            return "";
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
            var targetPc5 = Path.Combine(targetRoot, PreferredDwfPlotter);
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
