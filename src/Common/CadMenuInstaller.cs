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
/// CAD 菜单安装器（兼容中望CAD / AutoCAD / AutoCAD Core），
/// 通过 COM 反射操作菜单系统，实现菜单的创建、删除、更新。
/// </summary>
public static class CadMenuInstaller
{
    private const string MenuName = "LA批量打印";
#if ZWCAD
    private const string PreferredMenuGroupName = "ZWCAD";
#else
    private const string PreferredMenuGroupName = "ACAD";
#endif

    /// <summary>
    /// 安装或刷新批量打印菜单。
    /// 首次调用时创建菜单及所有子项；菜单已存在时默认仅设为可见，
    /// 传入 force=true 则先删除再重建。
    /// </summary>
    /// <param name="force">是否强制重建菜单</param>
    public static void Install(bool force = false)
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

            // 普通安装只恢复已显示菜单；需要重建时单次清理菜单栏及菜单组残留。
            var existing = FindNamedItem(menuBar, MenuName);
            if (existing != null && !force)
            {
                TrySetProperty(existing, "Visible", true);
                return;
            }

            if (existing != null)
                TryInvoke(existing, "Delete");

            var residual = FindNamedItem(menus, MenuName);
            if (residual != null)
                TryInvoke(residual, "Delete");

            if (FindNamedItem(menus, MenuName) != null)
            {
                WriteMessage("\n批量打印菜单残留清理失败，已停止重建。");
                return;
            }

            var menu = TryInvoke(menus, "Add", MenuName);
            if (menu == null)
            {
#if ZWCAD
                WriteMessage("\n批量打印插件已加载，但菜单创建失败。");
#endif
                return;
            }

            // 中望CAD 命令前需加 ^C^C 取消当前命令
#if ZWCAD
            const string cmdPrefix = "^C^C";
#else
            const string cmdPrefix = "";
#endif

            // 添加打印功能菜单项
            AddMenuItem(menu, "新增图框", cmdPrefix + "ZBP_ADD_TITLE_BLOCK ");
            AddMenuItem(menu, "图框库管理", cmdPrefix + "ZBP_MANAGE_LIBRARY ");
            AddMenuItem(menu, "批量打印(选图框块)", cmdPrefix + "ZBP_SHOW_PANEL ");
            AddMenuItem(menu, "批量打印(选矩形框)", cmdPrefix + "ZBP_RECTANGLE_BATCH_PLOT ");
            AddMenuItem(menu, "单张打印", cmdPrefix + "ZBP_SINGLE_PLOT ");
            AddSeparator(menu);
            // 添加工具类菜单项
            AddMenuItem(menu, "设置", cmdPrefix + "ZBP_SETTINGS ");
            AddMenuItem(menu, "安装自动加载", cmdPrefix + "ZBP_INSTALL_AUTOLOAD ");
            AddMenuItem(menu, "卸载自动加载", cmdPrefix + "ZBP_UNINSTALL_AUTOLOAD ");
            AddMenuItem(menu, "打开配置目录", cmdPrefix + "ZBP_OPEN_CONFIG ");
            AddMenuItem(menu, "刷新菜单", cmdPrefix + "ZBP_RELOAD_MENU ");

            // 插入后必须确认菜单栏确实返回同名菜单，避免把 NETLOAD 成功误报为菜单成功。
            var menuCount = Convert.ToInt32(GetProperty(menuBar, "Count") ?? 0);
            TryInvoke(menu, "InsertInMenuBar", menuCount);
            if (FindNamedItem(menuBar, MenuName) == null)
            {
                TryInvoke(menu, "Delete");
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
    private static void AddMenuItem(object menu, string label, string command)
    {
        var count = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
        TryInvoke(menu, "AddMenuItem", count, label, command);
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
    /// 在集合中按名称查找项。遍历集合的每个元素，
    /// 通过 Name 属性进行大小写不敏感匹配。
    /// </summary>
    private static object? FindNamedItem(object collection, string name)
    {
        var count = Convert.ToInt32(GetProperty(collection, "Count") ?? 0);
        for (var i = 0; i < count; i++)
        {
            var item = InvokeItem(collection, i);
            if (item == null)
                continue;

            var itemName = GetProperty(item, "Name")?.ToString();
            if (string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase))
                return item;
        }

        return null;
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
    /// 通过反射设置对象的实例属性值，失败时静默忽略。
    /// </summary>
    private static void TrySetProperty(object target, string name, object value)
    {
        try
        {
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
        }
        catch
        {
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
