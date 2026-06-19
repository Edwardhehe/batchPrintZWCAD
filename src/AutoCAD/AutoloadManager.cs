using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace ZwcadBatchPlot;

public static class AutoloadManager
{
    private const string AppKeyName = "AcadBatchPlot";
    private const string AppDescription = "AutoCAD批量打印插件";
    private const string AcadRoot = @"Software\Autodesk\AutoCAD";

    public static string CurrentDllPath => Assembly.GetExecutingAssembly().Location;

    public static IReadOnlyList<string> Install(string? dllPath = null)
    {
        dllPath = string.IsNullOrWhiteSpace(dllPath) ? CurrentDllPath : Path.GetFullPath(dllPath);
        var roots = GetApplicationRoots().ToList();
        if (roots.Count == 0)
        {
            throw new InvalidOperationException("未找到AutoCAD自加载注册表位置。请先启动一次AutoCAD。");
        }

        foreach (var applicationsRoot in roots)
        {
            using var key = Registry.CurrentUser.CreateSubKey(applicationsRoot + "\\" + AppKeyName);
            if (key == null)
            {
                continue;
            }

            key.SetValue("DESCRIPTION", AppDescription, RegistryValueKind.String);
            key.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
            key.SetValue("LOADER", dllPath, RegistryValueKind.String);
            key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
        }

        return roots;
    }

    public static int Uninstall()
    {
        var removed = 0;
        foreach (var applicationsRoot in GetApplicationRoots())
        {
            using var parent = Registry.CurrentUser.OpenSubKey(applicationsRoot, writable: true);
            if (parent == null)
            {
                continue;
            }

            try
            {
                parent.DeleteSubKeyTree(AppKeyName, throwOnMissingSubKey: false);
                removed++;
            }
            catch
            {
            }
        }

        return removed;
    }

    public static bool IsInstalled(out string dllPath)
    {
        dllPath = "";
        foreach (var applicationsRoot in GetApplicationRoots())
        {
            using var key = Registry.CurrentUser.OpenSubKey(applicationsRoot + "\\" + AppKeyName);
            var loader = key?.GetValue("LOADER")?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(loader))
            {
                dllPath = loader;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetApplicationRoots()
    {
        using var root = Registry.CurrentUser.OpenSubKey(AcadRoot);
        if (root == null)
        {
            yield break;
        }

        // AutoCAD registry structure:
        // HKCU\Software\Autodesk\AutoCAD\R{version}\ACAD-{product}:{locale}\Applications
        foreach (var version in root.GetSubKeyNames().OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!version.StartsWith("R", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var versionKey = root.OpenSubKey(version);
            if (versionKey == null)
            {
                continue;
            }

            foreach (var profile in versionKey.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (!profile.StartsWith("ACAD-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var applicationsPath = AcadRoot + "\\" + version + "\\" + profile + "\\Applications";
                using var applications = Registry.CurrentUser.OpenSubKey(applicationsPath);
                if (applications != null)
                {
                    yield return applicationsPath;
                }
            }
        }
    }
}
