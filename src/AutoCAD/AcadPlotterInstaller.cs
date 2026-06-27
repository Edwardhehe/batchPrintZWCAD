using System;
using System.IO;
using System.Reflection;
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
            var sourceRoot = FindBundledPlotterRoot();
            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                result.Message = "未找到随插件附带的 LA_pdf 打印机配置。";
                return result;
            }

            result.SourceFound = true;
            var sourcePc3 = Path.Combine(sourceRoot, PreferredPdfPlotter);
            var sourcePmp = Path.Combine(sourceRoot, "PMP Files", PreferredPmp);
            if (!File.Exists(sourcePc3) || !File.Exists(sourcePmp))
            {
                result.Message = "LA_pdf 打印机配置不完整，需要 LA_pdf.pc3 和 PMP Files\\LA_pdf.pmp。";
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
            File.Copy(sourcePc3, targetPc3, overwrite: true);
            File.Copy(sourcePmp, targetPmp, overwrite: true);

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

    private static string? FindBundledPlotterRoot()
    {
        foreach (var root in GetCandidateBaseDirectories())
        {
            var candidate = Path.Combine(root, "Plotters");
            if (File.Exists(Path.Combine(candidate, PreferredPdfPlotter)))
            {
                return candidate;
            }

            candidate = Path.Combine(root, "resources", "acad", "Plotters");
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
}
