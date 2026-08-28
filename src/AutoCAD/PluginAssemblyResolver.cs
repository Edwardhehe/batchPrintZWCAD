#if ACAD_CORE
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;

namespace ZwcadBatchPlot;

/// <summary>
/// AutoCAD 2025+ 使用默认 AssemblyLoadContext，不会稳定探测插件目录中的依赖，
/// 且天正等插件可能已加载另一份 Newtonsoft.Json，导致 0x80131621。
/// 在程序集加载时注册解析：优先从插件目录加载，失败则复用已加载的同名程序集。
/// </summary>
internal static class PluginAssemblyResolver
{
    private static int _registered;

    /// <summary>模块初始化时注册，确保早于任何 JsonConvert 调用。</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Design", "CA2255", Justification = "AutoCAD 以插件方式加载本程序集，必须在 Initialize 之前挂上依赖解析。")]
    [ModuleInitializer]
    internal static void Init() => Register();

    /// <summary>注册依赖解析。可重复调用。</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        var pluginAssembly = typeof(PluginAssemblyResolver).Assembly;
        var context = AssemblyLoadContext.GetLoadContext(pluginAssembly) ?? AssemblyLoadContext.Default;
        context.Resolving += OnResolving;
        AppDomain.CurrentDomain.AssemblyResolve += OnAppDomainResolve;
    }

    /// <summary>AppDomain 回退解析。</summary>
    private static Assembly? OnAppDomainResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            return Resolve(new AssemblyName(args.Name));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ALC Resolving 回调。</summary>
    private static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        return Resolve(name);
    }

    /// <summary>
    /// 解析插件私有依赖。不拦截系统/AutoCAD 程序集，避免干扰宿主。
    /// </summary>
    private static Assembly? Resolve(AssemblyName requested)
    {
        var simpleName = requested.Name;
        if (string.IsNullOrWhiteSpace(simpleName) || IsHostAssembly(simpleName))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(typeof(PluginAssemblyResolver).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            var path = Path.Combine(dir, simpleName + ".dll");
            if (File.Exists(path))
            {
                try
                {
                    return Assembly.LoadFrom(path);
                }
                catch
                {
                    // 同名不同版本已被宿主或其他插件加载时，LoadFrom 会报 0x80131621。
                }
            }
        }

        return FindLoaded(simpleName);
    }

    /// <summary>查找当前进程中已加载的同名程序集。</summary>
    private static Assembly? FindLoaded(string simpleName)
    {
        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
            {
                return loaded;
            }
        }

        return null;
    }

    /// <summary>系统与 AutoCAD 宿主程序集由默认上下文解析，这里不要插手。</summary>
    private static bool IsHostAssembly(string simpleName)
    {
        return simpleName.StartsWith("System", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Autodesk.", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("accore", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("acdb", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("acmgd", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("netstandard", StringComparison.OrdinalIgnoreCase)
            || simpleName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase);
    }
}
#endif
