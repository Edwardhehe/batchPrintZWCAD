using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
#if ZWCAD
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 天正单行 / 多行文字读取。
///
/// 天正（tangent）系列软件的文字命令 <c>ttext</c>、<c>dhwz</c>（单行）与 <c>tmtext</c>（多行）
/// 创建的是天正自定义实体 <c>TCH_TEXT</c> / <c>TCH_MTEXT</c>，不是 <c>DBText</c> / <c>MText</c>，
/// 因此常规取字分支都匹配不到；且天正未加载时它们以 <c>ACAD_PROXY_ENTITY</c> 代理实体存在，
/// DXF 组码里<b>没有</b>文字内容（仅 92/93/310 等私有二进制数据），靠组码 1 取字必然失败。
///
/// 天正软件已加载时，实体由 <c>tch_kernal.arx</c> 中的基类 <c>TDbText</c> 派生，
/// 通过其导出函数即可取到内容与定位点：
/// <list type="bullet">
/// <item><c>TDbText::GetText</c> → <c>const wchar_t*</c>，单行与多行文字共用此入口。</item>
/// <item><c>TDbText::GetLocation</c> → 插入点，按值返回的三个 double。</item>
/// <item><c>TDbText::GetHeight</c> → 字高，用于估算包围盒。</item>
/// </list>
///
/// 这些是 C++ 修饰名，且修饰形式随天正版本与位数变化（实测 32 位有 <c>QBEPB_WXZ</c> 与
/// <c>QBEPBDXZ</c> 两种字符串编码，64 位为 <c>QEBAPEB_WXZ</c>），因此不能硬编码某一个：
/// 本类在运行时遍历已加载模块，逐个尝试候选符号名，命中即缓存。
/// </summary>
internal static class TianzhengTextReader
{
    /// <summary>天正文字内核模块名（不区分大小写），已加载时才能取到文字。</summary>
    private static readonly string[] KernelModuleNames =
    {
        "tch_kernal",
        "tch3_kernal",
        "tel_kernal",
        "tch_common"
    };

    /// <summary>
    /// <c>TDbText::GetText</c> 的候选修饰名，按优先级排列。
    /// <c>QEBAPEAX_W@Z</c> 表示按值返回指针（64 位 <c>AE</c> 调用约定 + <c>PEAX</c> 返回类型），
    /// <c>QBEPB_WXZ</c> 为 32 位 Unicode，<c>QBEPBDXZ</c> 为 32 位 ANSI。
    /// </summary>
    private static readonly string[] GetTextExportNames =
    {
        "?GetText@TDbText@@QEBAPEB_WXZ",
        "?GetText@TDbText@@QBEPB_WXZ",
        "?GetText@TDbText@@QBEPBDXZ"
    };

    /// <summary><c>TDbText::GetLocation</c> 的候选修饰名，返回按值传递的三维点。</summary>
    private static readonly string[] GetLocationExportNames =
    {
        "?GetLocation@TDbText@@QEBA?AVTADSGePoint3d@@XZ",
        "?GetLocation@TDbText@@QEBF?ATADSGePoint3d@@XZ",
        "?GetLocation@TDbText@@QBFA?ATADSGePoint3d@@XZ"
    };

    /// <summary><c>TDbText::GetHeight</c> 的候选修饰名。</summary>
    private static readonly string[] GetHeightExportNames =
    {
        "?GetHeight@TDbText@@QEBANH@Z",
        "?GetHeight@TDbText@@QBANH@Z"
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetTextFn(IntPtr text);

    /// <summary>返回 <c>TADSGePoint3d</c>（三个 double）。x64 下按隐藏返回指针传出。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetLocationFn(IntPtr text, IntPtr result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate double GetHeightFn(IntPtr text);

    private static readonly object Sync = new();
    private static bool _resolved;
    private static GetTextFn? _getText;
    private static GetLocationFn? _getLocation;
    private static GetHeightFn? _getHeight;

    /// <summary>
    /// 读取天正文字的显示文字与定位点。
    ///
    /// 注意：需要 CAD 宿主中已加载天正软件（<c>tch_kernal.arx</c> 已进入进程）。
    /// 天正未加载时图元为代理实体，其文字只存在于天正私有二进制数据中，
    /// 不存在任何公开接口可读取，此时返回 false，由调用方按普通图元处理。
    /// </summary>
    /// <param name="entity">候选实体。</param>
    /// <param name="text">显示文字。</param>
    /// <param name="point">插入点或包围盒中心，用于字段区域判定。</param>
    /// <returns>识别为天正文字且读到非空文字时返回 true。</returns>
    public static bool TryGetText(Entity entity, out string text, out Point3d point)
    {
        text = "";
        point = Point3d.Origin;
        if (entity == null || entity.ObjectId.IsNull)
        {
            return false;
        }

        // 天正未加载时实体是代理实体，没有可调用的 C++ 对象，直接放弃。
        if (entity is ProxyEntity)
        {
            return false;
        }

        if (!IsTianzhengTextEntity(entity))
        {
            return false;
        }

        EnsureResolved();
        if (_getText == null)
        {
            return false;
        }

        var unmanaged = IntPtr.Zero;
        try
        {
            unmanaged = entity.UnmanagedObject;
            if (unmanaged == IntPtr.Zero)
            {
                return false;
            }

            var buffer = _getText(unmanaged);
            if (buffer == IntPtr.Zero)
            {
                return false;
            }

            var value = Marshal.PtrToStringUni(buffer);
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            text = value;
            point = ReadLocation(unmanaged) ?? GetExtentsCenter(entity);
            return true;
        }
        catch
        {
            // 天正内核版本差异可能让调用失败；扫描流程不应因此中断。
            text = "";
            point = Point3d.Origin;
            return false;
        }
    }

    /// <summary>
    /// 判断实体是否为天正文字。天正软件加载后，真正的 <c>TCH_TEXT</c> / <c>TCH_MTEXT</c> 才有
    /// 可调用的 C++ 对象，此时 DXF 类名即天正自定义类名，可据此识别。
    /// </summary>
    private static bool IsTianzhengTextEntity(Entity entity)
    {
        var dxfName = GetDxfName(entity);
        if (string.IsNullOrEmpty(dxfName))
        {
            return false;
        }

        return dxfName.Equals("TCH_TEXT", StringComparison.OrdinalIgnoreCase)
            || dxfName.Equals("TCH_MTEXT", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDxfName(Entity entity)
    {
        try
        {
            return entity.GetRXClass()?.DxfName ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>读取插入点。x64 下 <c>double[3]</c> 经隐藏返回指针传出，按指针读取。</summary>
    private static Point3d? ReadLocation(IntPtr unmanaged)
    {
        if (_getLocation == null)
        {
            return null;
        }

        try
        {
            // MSVC x64 下大于 8 字节的返回类型经隐藏返回指针传出，故显式传入缓冲区；
            // 返回值为非空时以它为准（被调用方可能自行分配了返回存储）。
            var buffer = Marshal.AllocHGlobal(3 * sizeof(double));
            try
            {
                var result = _getLocation(unmanaged, buffer);
                var readFrom = result == IntPtr.Zero ? buffer : result;
                var xyz = new double[3];
                Marshal.Copy(readFrom, xyz, 0, 3);
                var x = xyz[0];
                var y = xyz[1];
                var z = xyz[2];
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
                {
                    return null;
                }

                return new Point3d(x, y, z);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>天正文字字高，供调用方估算包围盒；取不到时返回 0。</summary>
    public static double GetTextHeight(Entity entity)
    {
        if (entity == null || entity is ProxyEntity)
        {
            return 0d;
        }

        EnsureResolved();
        if (_getHeight == null)
        {
            return 0d;
        }

        try
        {
            var unmanaged = entity.UnmanagedObject;
            return unmanaged == IntPtr.Zero ? 0d : _getHeight(unmanaged);
        }
        catch
        {
            return 0d;
        }
    }

    private static void EnsureResolved()
    {
        if (_resolved)
        {
            return;
        }

        lock (Sync)
        {
            if (_resolved)
            {
                return;
            }

            foreach (var module in EnumerateTianzhengModules())
            {
                _getText ??= Resolve<GetTextFn>(module, GetTextExportNames);
                _getLocation ??= Resolve<GetLocationFn>(module, GetLocationExportNames);
                _getHeight ??= Resolve<GetHeightFn>(module, GetHeightExportNames);
            }

            _resolved = true;
        }
    }

    /// <summary>
    /// 枚举已加载的天正内核模块。优先匹配已知模块名，失败时退回到按名字包含关系全量扫描，
    /// 以兼容不同版本下天正模块文件名带位数后缀（如 <c>tch_kernal</c> / <c>tch3_kernal</c>）的情况。
    /// </summary>
    private static IEnumerable<IntPtr> EnumerateTianzhengModules()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in KernelModuleNames)
        {
            var handle = GetModuleHandle(name + ".arx");
            if (handle != IntPtr.Zero && seen.Add(handle.ToString()))
            {
                yield return handle;
            }
        }

        ProcessModuleCollection modules;
        try
        {
            modules = Process.GetCurrentProcess().Modules;
        }
        catch
        {
            yield break;
        }

        foreach (ProcessModule module in modules)
        {
            var moduleName = module.ModuleName ?? "";
            if (moduleName.Length == 0
                || !moduleName.EndsWith(".arx", StringComparison.OrdinalIgnoreCase)
                || !IsTianzhengModuleName(moduleName))
            {
                continue;
            }

            var handle = GetModuleHandle(moduleName);
            if (handle != IntPtr.Zero && seen.Add(handle.ToString()))
            {
                yield return handle;
            }
        }
    }

    private static bool IsTianzhengModuleName(string moduleName)
    {
        foreach (var name in KernelModuleNames)
        {
            if (moduleName.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static TDelegate? Resolve<TDelegate>(IntPtr module, string[] exportNames)
        where TDelegate : class
    {
        foreach (var exportName in exportNames)
        {
            var proc = GetProcAddress(module, exportName);
            if (proc == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                return (TDelegate)(object)Marshal.GetDelegateForFunctionPointer(proc, typeof(TDelegate));
            }
            catch
            {
                // 该修饰名对应的调用约定与本平台不符，继续尝试下一个候选。
            }
        }

        return null;
    }

    private static Point3d GetExtentsCenter(Entity entity)
    {
        try
        {
            var extents = entity.GeometricExtents;
            return new Point3d(
                (extents.MinPoint.X + extents.MaxPoint.X) / 2d,
                (extents.MinPoint.Y + extents.MaxPoint.Y) / 2d,
                0);
        }
        catch
        {
            return Point3d.Origin;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
}