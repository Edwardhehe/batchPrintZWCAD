using System;
using System.Reflection;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

public static class CadMenuInstaller
{
    private const string MenuName = "批量打印";

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

            var existing = FindNamedItem(menuBar, MenuName);
            if (existing != null)
            {
                if (!force)
                {
                    return;
                }

                TryInvoke(existing, "Delete");
            }

            var menuGroup = InvokeItem(menuGroups, 0);
            if (menuGroup == null)
            {
                WriteMessage("\n批量打印插件已加载，但未取得默认菜单组。");
                return;
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

            AddMenuItem(menu, "新增图框", "_ZBP_INTERNAL_ADD_TITLE_BLOCK ");
            AddMenuItem(menu, "图框库管理", "_ZBP_INTERNAL_MANAGE_LIBRARY ");
            AddMenuItem(menu, "批量打印", "_ZBP_INTERNAL_SHOW_PANEL ");
            AddSeparator(menu);
            AddMenuItem(menu, "设置", "_ZBP_INTERNAL_SETTINGS ");
            AddMenuItem(menu, "打开配置目录", "_ZBP_INTERNAL_OPEN_CONFIG ");
            AddMenuItem(menu, "刷新菜单", "_ZBP_INTERNAL_RELOAD_MENU ");

            var menuCount = Convert.ToInt32(GetProperty(menuBar, "Count") ?? 0);
            TryInvoke(menu, "InsertInMenuBar", menuCount);

            InstallToolbar(menuGroup, force);
            WriteMessage("\n批量打印菜单/工具条已加载。");
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

    private static void InstallToolbar(object menuGroup, bool force)
    {
        var toolbars = GetProperty(menuGroup, "Toolbars");
        if (toolbars == null)
        {
            return;
        }

        var existing = FindNamedItem(toolbars, MenuName);
        if (existing != null)
        {
            if (!force)
            {
                TrySetProperty(existing, "Visible", true);
                return;
            }

            TryInvoke(existing, "Delete");
        }

        var toolbar = TryInvoke(toolbars, "Add", MenuName);
        if (toolbar == null)
        {
            return;
        }

        AddToolbarButton(toolbar, "新增图框", "学习图框块", "_ZBP_INTERNAL_ADD_TITLE_BLOCK ");
        AddToolbarButton(toolbar, "图框库管理", "管理图框信息库", "_ZBP_INTERNAL_MANAGE_LIBRARY ");
        AddToolbarButton(toolbar, "批量打印", "打开批量打印窗口", "_ZBP_INTERNAL_SHOW_PANEL ");
        AddToolbarButton(toolbar, "设置", "打开批量打印设置", "_ZBP_INTERNAL_SETTINGS ");
        TrySetProperty(toolbar, "Visible", true);
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

    private static void AddToolbarButton(object toolbar, string name, string help, string command)
    {
        var count = Convert.ToInt32(GetProperty(toolbar, "Count") ?? 0);
        TryInvoke(toolbar, "AddToolbarButton", count, name, help, "^C^C" + command, false);
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
