using System;
using System.Reflection;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

/// <summary>
/// 中望CAD 菜单安装器，通过 COM 反射操作中望CAD 菜单系统，
/// 实现菜单的创建、删除、更新等操作。
/// </summary>
public static class CadMenuInstaller
{
    private const string MenuName = "LA批量打印";

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
            // 确保菜单栏可见
            ShowMenuBar();

            var menuBar = CadApp.MenuBar;
            var menuGroups = CadApp.MenuGroups;
            if (menuBar == null || menuGroups == null)
            {
                WriteMessage("\n批量打印插件已加载，但当前 CAD 未暴露菜单栏接口。");
                return;
            }

            // 获取默认菜单组（索引 0）
            var menuGroup = InvokeItem(menuGroups, 0);
            if (menuGroup == null)
            {
                WriteMessage("\n批量打印插件已加载，但未取得默认菜单组。");
                return;
            }

            // 清理可能残留的同名工具栏
            RemoveToolbar(menuGroup, MenuName);

            // 菜单已存在时的处理
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

            // 获取菜单集合，创建新菜单
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

            // 添加打印功能菜单项（中望CAD 命令前需加 ^C^C 取消当前命令）
            AddMenuItem(menu, "新增图框", "ZBP_ADD_TITLE_BLOCK ");
            AddMenuItem(menu, "图框库管理", "ZBP_MANAGE_LIBRARY ");
            AddMenuItem(menu, "批量打印(选图框块)", "ZBP_SHOW_PANEL ");
            AddMenuItem(menu, "批量打印(选矩形框)", "ZBP_RECTANGLE_BATCH_PLOT ");
            AddMenuItem(menu, "单张打印", "ZBP_SINGLE_PLOT ");
            AddSeparator(menu);
            // 添加工具类菜单项
            AddMenuItem(menu, "设置", "ZBP_SETTINGS ");
            AddMenuItem(menu, "安装自动加载", "ZBP_INSTALL_AUTOLOAD ");
            AddMenuItem(menu, "卸载自动加载", "ZBP_UNINSTALL_AUTOLOAD ");
            AddMenuItem(menu, "打开配置目录", "ZBP_OPEN_CONFIG ");
            AddMenuItem(menu, "刷新菜单", "ZBP_RELOAD_MENU ");

            // 将菜单插入菜单栏末尾
            var menuCount = Convert.ToInt32(GetProperty(menuBar, "Count") ?? 0);
            TryInvoke(menu, "InsertInMenuBar", menuCount);

            WriteMessage("\n批量打印菜单已加载。");
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
    /// 删除指定名称的工具栏。
    /// </summary>
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

    /// <summary>
    /// 向菜单末尾追加一个菜单项。中望CAD 命令前需加 ^C^C 前缀取消当前命令。
    /// </summary>
    private static void AddMenuItem(object menu, string label, string command)
    {
        var count = Convert.ToInt32(GetProperty(menu, "Count") ?? 0);
        TryInvoke(menu, "AddMenuItem", count, label, "^C^C" + command);
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

    /// <summary>
    /// 调用集合的 Item 方法获取指定索引的元素。
    /// </summary>
    private static object? InvokeItem(object collection, int index)
    {
        return TryInvoke(collection, "Item", index);
    }

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
    /// 向中望CAD 命令行输出消息，静默处理所有异常。
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
