using System;
using System.Reflection;

namespace ZwcadBatchPlot;

/// <summary>
/// WPF 的 pack URI 资源查找（合并字典 Source、LoadComponent 等）内部通过
/// Assembly.Load("程序集短名") 定位宿主插件程序集；CAD 宿主的探测路径通常不包含
/// 插件目录，导致资源解析抛"未能加载文件或程序集"。这里注册一个兜底解析器：
/// 按简单名返回进程中已加载（NETLOAD）的插件程序集。只查已加载项，不主动加载文件，
/// 不干扰宿主程序集的正常解析。
/// </summary>
internal static class LoadedAssemblyResolver
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requested = new AssemblyName(args.Name);
            var simpleName = requested.Name;
            if (string.IsNullOrWhiteSpace(simpleName))
            {
                return null;
            }

            // 插件自身与私有依赖（PianNoCN 等）才会走到这里；宿主程序集在 Fusion 阶段已解析成功。
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                {
                    return loaded;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
