using System;
using System.Reflection;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

public static class CadMenuInstaller
{
    private const string MenuName = "批量打印";
    private const string LegacyMenuName = "ZW批量打印";

    public static void Install(bool force = false)
    {
        try
        {
            ShowMenuBar();

            var menuBar = CadApp.MenuBar;
            var menuGroups = CadApp.MenuGroups;
            if (menuBar == null || menuGroups == null)
            {
                WriteMessage("\n批量打印插件已加载，但当前 CAD 未暴露菜单栏接口。");
                return;
            }

            var menuGroup = InvokeItem(menuGroups, 0);
            if (menuGroup == null)
            {
                WriteMessage("\n批量打印插件已加载，但未取得默认菜单组。");
                return;
            }

            RemoveToolbar(menuGroup, MenuName);
            RemoveToolbar(menuGroup, LegacyMenuName);
            RemoveLegacyMenu(menuBar);

            var existing = FindNamedItem(menuBar, MenuName);
            if (existing != null)
            {
                if (!force)
                {
                    TrySetProperty(existing, "Visible", true);
                    return;
                }

                TryInvoke(existing, "Delete");
            }

            var menus = GetProperty(menuGroup, "Menus");
            if (menus == null)
            {
                WriteMessage("\n批量打印插件已加载，但未取得菜单集合。");
                return;
            }

            var menu = TryInvoke(menus, "Add", MenuName);
            if (menu == null)
            {
                WriteMessage("\n批量打印插件已加载，但菜单创建失败。");
                return;
            }

            AddMenuItem(menu, "新增图框", "ZBP_ADD_TITLE_BLOCK ");
            AddMenuItem(menu, "图框库管理", "ZBP_MANAGE_LIBRARY ");
            AddMenuItem(menu, "批量打印(选图框块)", "ZBP_SHOW_PANEL ");
            AddMenuItem(menu, "批量打印(选矩形框)", "ZBP_RECTANGLE_BATCH_PLOT ");
            AddMenuItem(menu, "单张打印", "ZBP_SINGLE_PLOT ");
            AddSeparator(menu);
            AddMenuItem(menu, "设置", "ZBP_SETTINGS ");
            AddMenuItem(menu, "安装自动加载", "ZBP_INSTALL_AUTOLOAD ");
            AddMenuItem(menu, "卸载自动加载", "ZBP_UNINSTALL_AUTOLOAD ");
            AddMenuItem(menu, "打开配置目录", "ZBP_OPEN_CONFIG ");
            AddMenuItem(menu, "刷新菜单", "ZBP_RELOAD_MENU ");

            var menuCount = Convert.ToInt32(GetProperty(menuBar, "Count") ?? 0);
            TryInvoke(menu, "InsertInMenuBar", menuCount);

            WriteMessage("\n批量打印菜单已加载。");
        }
        catch (Exception ex)
        {
            WriteMessage("\n批量打印菜单加载失败: " + ex.Message);
        }
    }

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

    private static void RemoveLegacyMenu(object menuBar)
    {
        var oldMenu = FindNamedItem(menuBar, LegacyMenuName);
        if (oldMenu != null)
        {
            TryInvoke(oldMenu, "Delete");
        }
    }

    private static void RemoveToolbar(object? menuGroup, string name)
    {
        if (menuGroup == null)
        {
            return;
        }

        var toolbars = GetProperty(menuGroup, "Toolbars");
        if (toolbars == null)
        {
            return;
        }

        var existing = FindNamedItem(toolbars, name);
        if (existing != null)
        {
            TryInvoke(existing, "Delete");
        }
    }

    private static void AddMenuItem(object menu, string label, string command)
    {
        var count = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
        TryInvoke(menu, "AddMenuItem", count, label, "^C^C" + command);
    }

    private static void AddSeparator(object menu)
    {
        var count = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
        TryInvoke(menu, "AddSeparator", count);
    }

    private static object? FindNamedItem(object collection, string name)
    {
        var count = Convert.ToInt32(GetProperty(collection, "Count") ?? 0);
        for (var i = 0; i < count; i++)
        {
            var item = InvokeItem(collection, i);
            if (item == null)
            {
                continue;
            }

            var itemName = GetProperty(item, "Name")?.ToString();
            if (string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private static object? InvokeItem(object collection, int index)
    {
        return TryInvoke(collection, "Item", index);
    }

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
