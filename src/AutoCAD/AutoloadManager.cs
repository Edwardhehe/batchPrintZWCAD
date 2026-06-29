using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using Microsoft.Win32;

namespace ZwcadBatchPlot;

public static class AutoloadManager
{
    private const string AppKeyName = "AcadBatchPlot";
    private const string AppDescription = "AutoCAD批量打印插件";
    private const string AcadRoot = @"Software\Autodesk\AutoCAD";
    private const string BundleName = "AcadBatchPlot.bundle";

    public static string CurrentDllPath => Assembly.GetExecutingAssembly().Location;

    public static IReadOnlyList<string> Install(string? dllPath = null)
    {
#if ACAD_CORE
        var bundlePath = InstallCoreBundle(dllPath);
        return new[] { bundlePath };
#else
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
#endif
    }

    public static int Uninstall()
    {
#if ACAD_CORE
        var removed = 0;
        var bundlePath = GetBundlePath();
        if (Directory.Exists(bundlePath))
        {
            removed += DisableAndDeleteCoreBundle(bundlePath);
        }

        removed += RemoveRegistryAutoloadEntries();
        return removed;
#else
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
#endif
    }

    public static bool IsInstalled(out string dllPath)
    {
#if ACAD_CORE
        var bundlePath = GetBundlePath();
        var packageContents = Path.Combine(bundlePath, "PackageContents.xml");
        dllPath = packageContents;
        return File.Exists(packageContents);
#else
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
#endif
    }

#if ACAD_CORE
    private static string InstallCoreBundle(string? dllPath)
    {
        dllPath = string.IsNullOrWhiteSpace(dllPath) ? CurrentDllPath : Path.GetFullPath(dllPath);
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("当前插件 DLL 不存在，无法安装自动加载。", dllPath);
        }

        RemoveRegistryAutoloadEntries();

        var sourceDir = Path.GetDirectoryName(dllPath)
            ?? throw new InvalidOperationException("无法定位当前插件目录，不能安装自动加载。");
        var bundlePath = GetBundlePath();
        var installFolderName = BuildInstallFolderName();
        var contentsPath = Path.Combine(bundlePath, "Contents", installFolderName);
        Directory.CreateDirectory(contentsPath);

        foreach (var pattern in new[] { "*.dll", "*.json", "*.config" })
        {
            foreach (var file in Directory.GetFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(contentsPath, Path.GetFileName(file)), overwrite: true);
            }
        }

        CopyDirectoryIfExists(Path.Combine(sourceDir, "runtimes"), Path.Combine(contentsPath, "runtimes"));
        CopyDirectoryIfExists(Path.Combine(sourceDir, "Plotters"), Path.Combine(contentsPath, "Plotters"));

        File.WriteAllText(Path.Combine(bundlePath, "PackageContents.xml"), BuildPackageContents(Path.GetFileName(dllPath), installFolderName));
        File.WriteAllText(Path.Combine(contentsPath, "AcadBatchPlot.mnu"), BuildMenuFile());
        return bundlePath;
    }

    private static string GetBundlePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Autodesk", "ApplicationPlugins", BundleName);
    }

    private static int DisableAndDeleteCoreBundle(string bundlePath)
    {
        var removed = 0;
        var packageContents = Path.Combine(bundlePath, "PackageContents.xml");
        if (File.Exists(packageContents))
        {
            TryDeleteFile(packageContents);
            if (!File.Exists(packageContents))
            {
                removed++;
            }
        }

        TryDeleteFile(Path.Combine(bundlePath, "Contents", "AcadBatchPlot.mnu"));
        TryDeleteDirectory(bundlePath);

        if (!Directory.Exists(bundlePath))
        {
            return Math.Max(removed, 1);
        }

        return removed;
    }

    private static string BuildInstallFolderName()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        return "v" + version + "-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
    }

    private static string BuildPackageContents(string dllName, string installFolderName)
    {
        var escapedInstallFolderName = SecurityElement.Escape(installFolderName);
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<ApplicationPackage SchemaVersion=""1.0"" AutodeskProduct=""AutoCAD"" Name=""LA批量打印"" AppVersion=""1.11.2"" ProductCode=""{{7f2f2f2d-78d1-4df0-8c5d-acadba7c0011}}"" Description=""AutoCAD批量打印插件"">
  <CompanyDetails Name=""lihao"" />
  <Components>
    <ComponentEntry AppName=""AcadBatchPlot"" AppType="".Net"" ModuleName=""./Contents/{escapedInstallFolderName}/{SecurityElement.Escape(dllName)}"" LoadOnAutoCADStartup=""True"" LoadOnCommandInvocation=""True"">
      <RuntimeRequirements OS=""Win64"" Platform=""AutoCAD*"" SeriesMin=""R25.0"" />
      <Commands GroupName=""AcadBatchPlot"">
        <Command Global=""ZBP_SHOW_PANEL"" Local=""ZBP_SHOW_PANEL"" />
        <Command Global=""ZBP_RECTANGLE_BATCH_PLOT"" Local=""ZBP_RECTANGLE_BATCH_PLOT"" />
        <Command Global=""ZBP_SINGLE_PLOT"" Local=""ZBP_SINGLE_PLOT"" />
        <Command Global=""ZBP_ADD_TITLE_BLOCK"" Local=""ZBP_ADD_TITLE_BLOCK"" />
        <Command Global=""ZBP_MANAGE_LIBRARY"" Local=""ZBP_MANAGE_LIBRARY"" />
        <Command Global=""ZBP_SETTINGS"" Local=""ZBP_SETTINGS"" />
        <Command Global=""ZBP_OPEN_CONFIG"" Local=""ZBP_OPEN_CONFIG"" />
        <Command Global=""ZBP_RELOAD_MENU"" Local=""ZBP_RELOAD_MENU"" />
      </Commands>
    </ComponentEntry>
    <ComponentEntry AppName=""AcadBatchPlotMenu"" AppType=""Mnu"" ModuleName=""./Contents/{escapedInstallFolderName}/AcadBatchPlot.mnu"">
      <RuntimeRequirements OS=""Win64"" Platform=""AutoCAD*"" SeriesMin=""R25.0"" />
    </ComponentEntry>
  </Components>
</ApplicationPackage>
";
    }

    private static string BuildMenuFile()
    {
        return @"***MENUGROUP=ACADBATCHPLOT
***POP1
**LA_BATCH_PLOT
ID_LA_BATCH_PLOT [LA批量打印]
ID_ZBP_ADD_TITLE_BLOCK [新增图框]ZBP_ADD_TITLE_BLOCK
ID_ZBP_MANAGE_LIBRARY [图框库管理]ZBP_MANAGE_LIBRARY
ID_ZBP_SHOW_PANEL [批量打印(选图框块)]ZBP_SHOW_PANEL
ID_ZBP_RECTANGLE_BATCH_PLOT [批量打印(选矩形框)]ZBP_RECTANGLE_BATCH_PLOT
ID_ZBP_SINGLE_PLOT [单张打印]ZBP_SINGLE_PLOT
[--]
ID_ZBP_SETTINGS [设置]ZBP_SETTINGS
ID_ZBP_UNINSTALL_AUTOLOAD [卸载自动加载]ZBP_UNINSTALL_AUTOLOAD
ID_ZBP_OPEN_CONFIG [打开配置目录]ZBP_OPEN_CONFIG
ID_ZBP_RELOAD_MENU [刷新菜单]ZBP_RELOAD_MENU
";
    }

    private static void CopyDirectoryIfExists(string sourceDir, string targetDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = GetRelativePath(sourceDir, file);
            var targetPath = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: true);
        }
    }

    private static string GetRelativePath(string basePath, string path)
    {
        var baseUri = new Uri(AppendDirectorySeparatorChar(Path.GetFullPath(basePath)));
        var pathUri = new Uri(Path.GetFullPath(path));
        return Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString())
            .Replace('/', Path.DirectorySeparatorChar);
    }

    private static string AppendDirectorySeparatorChar(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static int RemoveRegistryAutoloadEntries()
    {
        var removed = 0;
        using var root = Registry.CurrentUser.OpenSubKey(AcadRoot);
        if (root == null)
        {
            return removed;
        }

        foreach (var version in root.GetSubKeyNames())
        {
            using var versionKey = root.OpenSubKey(version);
            if (versionKey == null)
            {
                continue;
            }

            foreach (var profile in versionKey.GetSubKeyNames())
            {
                var applicationsPath = AcadRoot + "\\" + version + "\\" + profile + "\\Applications";
                using var applications = Registry.CurrentUser.OpenSubKey(applicationsPath, writable: true);
                if (applications == null)
                {
                    continue;
                }

                try
                {
                    applications.DeleteSubKeyTree(AppKeyName, throwOnMissingSubKey: false);
                    removed++;
                }
                catch
                {
                }
            }
        }

        return removed;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            TryDeleteFile(file);
        }

        foreach (var directory in Directory.GetDirectories(path, "*", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch
            {
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
#endif

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
