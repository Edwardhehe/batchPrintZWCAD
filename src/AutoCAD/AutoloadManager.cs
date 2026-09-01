using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using Autodesk.AutoCAD.DatabaseServices;
using Microsoft.Win32;

namespace ZwcadBatchPlot;

/// <summary>
/// AutoCAD 自动加载管理器。
/// 菜单安装/卸载对齐 IFoxCAD：只写入当前正在运行的 AutoCAD 的
/// <c>UserRegistryProductRootKey\Applications</c>，不扫其它版本。
/// AutoCAD 2025+ 安装器仍可使用 ApplicationPlugins Bundle；菜单不再写 Bundle，避免 2025-2027 互相加载。
/// </summary>
public static class AutoloadManager
{
    /// <summary>注册表中的应用键名</summary>
    private const string AppKeyName = "AcadBatchPlot";
    /// <summary>注册表中显示的应用描述</summary>
    private const string AppDescription = "AutoCAD批量打印插件";
#if ACAD_CORE
    /// <summary>AutoCAD 注册表根路径</summary>
    private const string AcadRoot = @"Software\Autodesk\AutoCAD";
    /// <summary>ApplicationPlugins Bundle 目录名</summary>
    private const string BundleName = "AcadBatchPlot.bundle";
#endif

    /// <summary>当前 DLL 的完整路径</summary>
    public static string CurrentDllPath => Assembly.GetExecutingAssembly().Location;

    /// <summary>
    /// 安装自动加载：仅对当前正在运行的 AutoCAD 写入 LOADER / LOADCTRLS / MANAGED。
    /// </summary>
    /// <param name="dllPath">可选，指定要注册的 DLL 路径；为空则使用当前程序集路径</param>
    /// <returns>注册到的 Applications 根路径列表</returns>
    public static IReadOnlyList<string> Install(string? dllPath = null)
    {
        dllPath = string.IsNullOrWhiteSpace(dllPath) ? CurrentDllPath : Path.GetFullPath(dllPath);
        var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: true)
            ?? throw new InvalidOperationException("未找到当前AutoCAD自加载注册表位置。请先启动一次AutoCAD。");

        if (!WriteAutoloadKey(applicationsRoot, dllPath))
        {
            throw new InvalidOperationException("无法写入当前AutoCAD的自动加载注册表项。");
        }

        return new[] { applicationsRoot };
    }

    /// <summary>
    /// 卸载自动加载：仅删除当前正在运行的 AutoCAD 下的注册表子键。
    /// </summary>
    /// <returns>成功清理的注册表项数量</returns>
    public static int Uninstall()
    {
        var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
        if (applicationsRoot == null)
        {
            return 0;
        }

        using var parent = Registry.CurrentUser.OpenSubKey(applicationsRoot, writable: true);
        if (parent == null)
        {
            return 0;
        }

        try
        {
            parent.DeleteSubKeyTree(AppKeyName, throwOnMissingSubKey: false);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 检测当前 AutoCAD 是否已安装自动加载。
    /// </summary>
    /// <param name="dllPath">输出已注册的 DLL 路径</param>
    /// <returns>是否已安装自动加载</returns>
    public static bool IsInstalled(out string dllPath)
    {
        dllPath = "";
        var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
        if (applicationsRoot == null)
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(applicationsRoot + "\\" + AppKeyName);
        var loader = key?.GetValue("LOADER")?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(loader))
        {
            return false;
        }

        dllPath = loader;
        return true;
    }

#if ACAD_CORE
    /// <summary>
    /// Core 模式安装：将 DLL 及依赖文件复制到 %AppData%/Autodesk/ApplicationPlugins/ 下的 Bundle 目录，
    /// 并生成 PackageContents.xml 和菜单 .mnu 文件，使 AutoCAD 2025-2027 启动时自动加载。
    /// </summary>
    private static string InstallCoreBundle(string? dllPath)
    {
        dllPath = string.IsNullOrWhiteSpace(dllPath) ? CurrentDllPath : Path.GetFullPath(dllPath);
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException("当前插件 DLL 不存在，无法安装自动加载。", dllPath);
        }

        // 先清理可能残留的传统注册表自加载项
        RemoveRegistryAutoloadEntries();

        var sourceDir = Path.GetDirectoryName(dllPath)
            ?? throw new InvalidOperationException("无法定位当前插件目录，不能安装自动加载。");
        // Bundle 目录：%AppData%/Autodesk/ApplicationPlugins/AcadBatchPlot.bundle/
        var bundlePath = GetBundlePath();
        // 每次安装使用带版本号和时间戳的子文件夹，便于区分不同版本
        var installFolderName = BuildInstallFolderName();
        var contentsPath = Path.Combine(bundlePath, "Contents", installFolderName);
        Directory.CreateDirectory(contentsPath);

        // 复制 DLL、JSON、config 文件
        foreach (var pattern in new[] { "*.dll", "*.json", "*.config" })
        {
            foreach (var file in Directory.GetFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(contentsPath, Path.GetFileName(file)), overwrite: true);
            }
        }

        // 复制运行时依赖目录和绘图仪配置目录
        CopyDirectoryIfExists(Path.Combine(sourceDir, "runtimes"), Path.Combine(contentsPath, "runtimes"));
        CopyDirectoryIfExists(Path.Combine(sourceDir, "Plotters"), Path.Combine(contentsPath, "Plotters"));

        // 生成 PackageContents.xml（AutoCAD Bundle 描述文件）和菜单 .mnu 文件
        File.WriteAllText(Path.Combine(bundlePath, "PackageContents.xml"), BuildPackageContents(Path.GetFileName(dllPath), installFolderName));
        File.WriteAllText(Path.Combine(contentsPath, "AcadBatchPlot.mnu"), BuildMenuFile());
        return bundlePath;
    }

    /// <summary>
    /// 获取 Bundle 安装路径：%AppData%/Autodesk/ApplicationPlugins/AcadBatchPlot.bundle/
    /// </summary>
    private static string GetBundlePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Autodesk", "ApplicationPlugins", BundleName);
    }

    /// <summary>
    /// 禁用并删除 Core Bundle：先删除 PackageContents.xml 使 AutoCAD 不再加载，
    /// 再删除 .mnu 文件并尝试清理整个 Bundle 目录。
    /// </summary>
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

    /// <summary>
    /// 构建安装文件夹名：v{版本号}-{时间戳}，确保每次安装使用独立目录。
    /// </summary>
    private static string BuildInstallFolderName()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
        return "v" + version + "-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
    }

    /// <summary>
    /// 生成 Bundle 描述文件 PackageContents.xml。
    /// 包含组件定义（.Net 程序集 + .mnu 菜单文件）、自动加载配置及所有注册命令列表。
    /// </summary>
    private static string BuildPackageContents(string dllName, string installFolderName)
    {
        var escapedInstallFolderName = SecurityElement.Escape(installFolderName);
        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<ApplicationPackage SchemaVersion=""1.0"" AutodeskProduct=""AutoCAD"" Name=""LA批量打印"" AppVersion=""1.15.6.4"" ProductCode=""{{7f2f2f2d-78d1-4df0-8c5d-acadba7c0011}}"" Description=""AutoCAD批量打印插件"">
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

    /// <summary>
    /// 生成 AutoCAD 菜单 .mnu 文件，定义批量打印菜单栏及其所有命令项。
    /// </summary>
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

    /// <summary>递归复制整个目录及其所有文件到目标位置。</summary>
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

    /// <summary>计算相对路径，使用 URI 机制处理路径分隔符差异。</summary>
    private static string GetRelativePath(string basePath, string path)
    {
        var baseUri = new Uri(AppendDirectorySeparatorChar(Path.GetFullPath(basePath)));
        var pathUri = new Uri(Path.GetFullPath(path));
        return Uri.UnescapeDataString(baseUri.MakeRelativeUri(pathUri).ToString())
            .Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>确保路径以目录分隔符结尾。</summary>
    private static string AppendDirectorySeparatorChar(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// 清理传统注册表自加载项：遍历所有 AutoCAD 版本/配置文件的 Applications 路径，
    /// 删除 AppKeyName 子键。
    /// </summary>
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

    /// <summary>安全删除目录：先清空所有子文件，再从深层到浅层逐层删除空目录。</summary>
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

    /// <summary>安全删除文件：先清除只读属性再删除。</summary>
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

    /// <summary>
    /// 获取当前正在运行的 AutoCAD 的 Applications 路径（IFoxCAD <c>GetAcAppKey</c> 写法）。
    /// </summary>
    /// <param name="createIfMissing">为 true 时创建尚不存在的 Applications</param>
    /// <returns>HKCU 相对路径；无法定位当前 CAD 时返回 null</returns>
    private static string? GetCurrentCadApplicationsRoot(bool createIfMissing)
    {
        var productRoot = HostApplicationServices.Current?.UserRegistryProductRootKey;
        if (string.IsNullOrWhiteSpace(productRoot))
        {
            return null;
        }

        productRoot = NormalizeHkcuRelativePath(productRoot!);
        if (string.IsNullOrWhiteSpace(productRoot))
        {
            return null;
        }

        var applicationsPath = productRoot + "\\Applications";
        if (createIfMissing)
        {
            using var created = Registry.CurrentUser.CreateSubKey(applicationsPath);
            return created != null ? applicationsPath : null;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(applicationsPath);
        return existing != null ? applicationsPath : null;
    }

    /// <summary>
    /// 在指定 Applications 根下写入当前插件的 DemandLoad 键值。
    /// </summary>
    /// <param name="applicationsRoot">Applications 注册表路径</param>
    /// <param name="dllPath">LOADER 指向的 DLL</param>
    /// <returns>是否写入成功</returns>
    private static bool WriteAutoloadKey(string applicationsRoot, string dllPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(applicationsRoot + "\\" + AppKeyName);
        if (key == null)
        {
            return false;
        }

        key.SetValue("DESCRIPTION", AppDescription, RegistryValueKind.String);
        key.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
        key.SetValue("LOADER", dllPath, RegistryValueKind.String);
        key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
        return true;
    }

    /// <summary>
    /// 将 <c>UserRegistryProductRootKey</c> 规范为 HKCU 相对路径。
    /// </summary>
    /// <param name="path">CAD 返回的产品根路径</param>
    /// <returns>不含 HKCU 前缀的相对路径</returns>
    private static string NormalizeHkcuRelativePath(string path)
    {
        var normalized = path.Replace('/', '\\').Trim('\\');
        const string hkcuPrefix = @"HKEY_CURRENT_USER\";
        if (normalized.StartsWith(hkcuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(hkcuPrefix.Length).Trim('\\');
        }

        return normalized;
    }
}
