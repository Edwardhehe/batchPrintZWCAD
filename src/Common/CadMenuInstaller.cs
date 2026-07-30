using System;
using System.Reflection;
#if ZWCAD
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#elif ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// CAD 菜单安装器（兼容中望CAD / AutoCAD / AutoCAD Core）。
/// 通过 COM 反射操作菜单系统；重复加载时复用同名菜单并原地重建菜单项，
/// 避免“菜单组中存在弹出菜单”和菜单栏重复插入导致的 COM 异常。
/// </summary>
public static class CadMenuInstaller
{
    /// <summary>
    /// 批打印菜单名称。CAD 宿主在同一菜单组中不允许同名弹出菜单，
    /// 链式动态加载或重复 NETLOAD 时直接 Add 同名菜单会触发 COM 异常。
    /// </summary>
    private const string MenuName = "LA批量打印";
#if ZWCAD
    private const string PreferredMenuGroupName = "ZWCAD";
#else
    private const string PreferredMenuGroupName = "ACAD";
#endif

    /// <summary>
    /// 安装或刷新批量打印菜单。
    /// 支持重复调用：菜单已存在时复用并清空重建菜单项；不存在时创建新菜单。
    /// </summary>
    public static void Install()
    {
        try
        {
            ShowMenuBar();

#if ACAD_CORE
            // ACAD Core 控制台模式：通过反射获取 COM 对象
            var acadApplication = GetAcadApplication();
            var menuBar = acadApplication == null ? null : GetProperty(acadApplication, "MenuBar");
            var menuGroups = acadApplication == null ? null : GetProperty(acadApplication, "MenuGroups");
#else
            // 标准 AutoCAD / 中望CAD：直接访问 COM 属性
            var menuBar = CadApp.MenuBar;
            var menuGroups = CadApp.MenuGroups;
#endif
            if (menuBar == null || menuGroups == null)
            {
#if ZWCAD
                WriteMessage("\n批量打印插件已加载，但当前 CAD 未暴露菜单栏接口。");
#endif
                return;
            }

            // 优先按平台主菜单组名称获取；名称不可用时兼容回退到首个菜单组。
            var menuGroup = InvokeItem(menuGroups, PreferredMenuGroupName)
                ?? InvokeItem(menuGroups, 0);
            if (menuGroup == null)
            {
#if ZWCAD
                WriteMessage("\n批量打印插件已加载，但未取得默认菜单组。");
#endif
                return;
            }

            var menus = GetProperty(menuGroup, "Menus");
            if (menus == null)
            {
#if ZWCAD
                WriteMessage("\n批量打印插件已加载，但未取得菜单集合。");
#endif
                return;
            }

            var menu = GetOrCreateMenu(menus);
            if (menu == null)
            {
#if ZWCAD
                WriteMessage("\n批量打印插件已加载，但菜单创建失败。");
#endif
                return;
            }

            // 复用旧菜单时先清空旧菜单项，再按当前代码重建完整菜单。
            // 以进入时的 Count 为上界，避免删除失败造成死循环。
            var oldCount = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
            for (var i = 0; i < oldCount; i++)
            {
                var first = TryInvoke(menu, "Item", 0);
                if (first == null)
                {
                    break;
                }

                TryInvoke(first, "Delete");
            }

            // 打印功能菜单项
            AddMenuItem(menu, "新增图框", "ZBP_ADD_TITLE_BLOCK");
            AddMenuItem(menu, "图框库管理", "ZBP_MANAGE_LIBRARY");
            AddMenuItem(menu, "批量打印(选图框块)", "ZBP_SHOW_PANEL");
            AddMenuItem(menu, "批量打印(选矩形框)", "ZBP_RECTANGLE_BATCH_PLOT");
            AddMenuItem(menu, "单张打印", "ZBP_SINGLE_PLOT");
            AddSeparator(menu);
            // 工具类菜单项
            AddMenuItem(menu, "设置", "ZBP_SETTINGS");
            AddMenuItem(menu, "安装自动加载", "ZBP_INSTALL_AUTOLOAD");
            AddMenuItem(menu, "卸载自动加载", "ZBP_UNINSTALL_AUTOLOAD");
            AddMenuItem(menu, "打开配置目录", "ZBP_OPEN_CONFIG");
            AddMenuItem(menu, "刷新菜单", "ZBP_RELOAD_MENU");

            // 已在菜单栏上的弹出菜单不能重复插入，否则部分 CAD 宿主会抛出 COM 反射异常。
            if (!IsMenuOnMenuBar(menu))
            {
                var menuBarCount = Convert.ToInt32(GetProperty(menuBar, "Count") ?? 0);
                TryInvoke(menu, "InsertInMenuBar", menuBarCount + 1);
            }

            if (!IsMenuOnMenuBar(menu))
            {
                WriteMessage("\n批量打印插件已加载，但菜单未能插入菜单栏。");
                return;
            }

#if ZWCAD
            WriteMessage("\n批量打印菜单已加载。");
#endif
        }
        catch (Exception ex)
        {
            WriteMessage("\n批量打印菜单加载失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 获取已存在的批打印菜单；不存在时创建后再返回。
    /// 只有确认不存在同名菜单时才调用 Add，避免“菜单组中存在弹出菜单”异常。
    /// </summary>
    private static object? GetOrCreateMenu(object menus)
    {
        var menu = TryGetMenu(menus);
        if (menu != null)
        {
            return menu;
        }

        return TryInvoke(menus, "Add", MenuName);
    }

    /// <summary>
    /// 尝试按名称从菜单集合中查找批打印菜单；找不到或宿主 COM 查询失败时返回 null。
    /// </summary>
    private static object? TryGetMenu(object menus)
    {
        try
        {
            return menus.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, menus, new object[] { MenuName });
        }
        catch (TargetInvocationException)
        {
            // COM 反射会把宿主内部异常包装为 TargetInvocationException；按名称查询失败等价于菜单不存在。
            return null;
        }
        catch (ArgumentException)
        {
            // 部分宿主在 Item(name) 找不到对象时直接抛 ArgumentException。
            return null;
        }
    }

    /// <summary>
    /// 判断菜单是否已经插入到 CAD 菜单栏；状态不可读时按未插入处理。
    /// </summary>
    private static bool IsMenuOnMenuBar(object menu)
    {
        try
        {
            return Convert.ToBoolean(menu.GetType().InvokeMember("OnMenuBar", BindingFlags.GetProperty, null, menu, null));
        }
        catch (TargetInvocationException)
        {
            // OnMenuBar 在部分宿主或特定菜单状态下可能不可读。
            return false;
        }
        catch (ArgumentException)
        {
            // 兼容 COM 属性不存在或宿主返回参数异常的情况。
            return false;
        }
    }

    /// <summary>
    /// 设置 MENUBAR 系统变量为 1，确保菜单栏可见。
    /// </summary>
    private static void ShowMenuBar()
    {
        try
        {
            CadApp.SetSystemVariable("MENUBAR", 1);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 向菜单末尾追加一个菜单项。
    /// </summary>
    private static void AddMenuItem(object menu, string label, string commandName)
    {
        var count = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
        TryInvoke(menu, "AddMenuItem", count, label, CreateMenuMacro(commandName));
    }

    /// <summary>
    /// 向菜单末尾追加一个分隔线。
    /// </summary>
    private static void AddSeparator(object menu)
    {
        var count = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
        TryInvoke(menu, "AddSeparator", count);
    }

    /// <summary>
    /// 生成菜单宏：两次 ESC(0x03) 取消当前命令（等价 ^C^C），“_”前缀保证按英文命令名调用。
    /// </summary>
    private static string CreateMenuMacro(string commandName)
    {
        const char esc = (char)3;
        return new string(esc, 2) + "_" + commandName + " ";
    }

    /// <summary>
    /// 调用集合的 Item 方法获取指定索引或名称的元素。
    /// </summary>
    private static object? InvokeItem(object collection, object index)
    {
        return TryInvoke(collection, "Item", index);
    }

#if ACAD_CORE
    /// <summary>
    /// 在 ACAD Core 控制台模式下，通过反射加载 AcMgd/AcCoreMgd 程序集，
    /// 获取 AcadApplication COM 对象，用于访问菜单系统。
    /// </summary>
    private static object? GetAcadApplication()
    {
        var applicationTypeNames = new[]
        {
            "Autodesk.AutoCAD.ApplicationServices.Application, AcMgd",
            "Autodesk.AutoCAD.ApplicationServices.Core.Application, AcCoreMgd"
        };

        foreach (var typeName in applicationTypeNames)
        {
            var type = Type.GetType(typeName, throwOnError: false);
            if (type == null)
                continue;

            var acadApplication = GetStaticProperty(type, "AcadApplication");
            if (acadApplication != null)
                return acadApplication;
        }

        return null;
    }

    /// <summary>
    /// 通过反射获取类型的静态属性值，失败时返回 null。
    /// </summary>
    private static object? GetStaticProperty(Type target, string name)
    {
        try
        {
            return target.InvokeMember(name,
                BindingFlags.GetProperty | BindingFlags.Static | BindingFlags.Public,
                null, null, null);
        }
        catch
        {
            return null;
        }
    }
#endif

    /// <summary>
    /// 通过反射获取对象的实例属性值，失败时返回 null。
    /// </summary>
    private static object? GetProperty(object target, string name)
    {
        try
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 通过反射调用对象的方法，失败时返回 null。
    /// </summary>
    private static object? TryInvoke(object target, string name, params object[] args)
    {
        try
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 向 CAD 命令行输出消息，静默处理所有异常。
    /// </summary>
    private static void WriteMessage(string message)
    {
        try
        {
            CadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(message);
        }
        catch
        {
        }
    }
}
