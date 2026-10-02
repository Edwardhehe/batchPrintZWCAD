using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.PlottingServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    private static readonly object PlotWarmUpGate = new();
    private static readonly HashSet<string> PlotWarmUpDoneKeys = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WarmUpRun> PlotWarmUpRunning = new(StringComparer.OrdinalIgnoreCase);

    /** WarmUpRun：分步预热运行句柄；同步预热接管时置 Cancelled，防止空闲分段在批打中途插空执行。 */
    private sealed class WarmUpRun
    {
        public bool Cancelled;
    }

    /**
     * WarmUpPlotPipeline：同步预热（PlotMany 兜底路径）。整段跑完所有预热分段；
     * 若窗体的分步预热尚未完成，立即接管并取消仍在排队的空闲分段。
     * 每个设备+样式组合整个会话只做一次。
     */
    internal static void WarmUpPlotPipeline(string deviceName, string? styleSheet)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        var key = WarmUpKey(deviceName, styleSheet);
        lock (PlotWarmUpGate)
        {
            if (!PlotWarmUpDoneKeys.Add(key))
            {
                return;
            }

            if (PlotWarmUpRunning.TryGetValue(key, out var run))
            {
                run.Cancelled = true;
                PlotWarmUpRunning.Remove(key);
            }
        }

        RunWarmUpStages(BuildWarmUpStages(deviceName, styleSheet));
    }

    /**
     * BeginWarmUpPlotPipeline：分步预热（窗体空闲时调用）。每个空闲时钟只执行一小段，
     * 段与段之间让出 UI——打开批打窗体后不再出现一次性的 UI 卡顿。
     */
    internal static void BeginWarmUpPlotPipeline(string deviceName, string? styleSheet, Action<Action> schedule)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return;
        }

        var key = WarmUpKey(deviceName, styleSheet);
        WarmUpRun run;
        lock (PlotWarmUpGate)
        {
            if (PlotWarmUpDoneKeys.Contains(key) || PlotWarmUpRunning.ContainsKey(key))
            {
                return;
            }

            run = new WarmUpRun();
            PlotWarmUpRunning[key] = run;
        }

        var stages = BuildWarmUpStages(deviceName, styleSheet);
        var index = 0;
        void Step()
        {
            lock (PlotWarmUpGate)
            {
                if (run.Cancelled)
                {
                    PlotWarmUpRunning.Remove(key);
                    return;
                }
            }

            if (index >= stages.Count)
            {
                lock (PlotWarmUpGate)
                {
                    PlotWarmUpRunning.Remove(key);
                    PlotWarmUpDoneKeys.Add(key);
                }

                return;
            }

            try
            {
                stages[index]();
            }
            catch
            {
                // 任一分段失败都不阻塞后续分段；正式首张会照旧自行加载。
            }

            index++;
            schedule(Step);
        }

        schedule(Step);
    }

    private static string WarmUpKey(string deviceName, string? styleSheet)
    {
        return deviceName + "|" + (styleSheet ?? "");
    }

    /** BuildWarmUpStages：预热分段。每段都很短，可在两次 UI 空闲之间完成。 */
    private static List<Action> BuildWarmUpStages(string deviceName, string? styleSheet)
    {
        return new List<Action>
        {
            // 关闭自动打印日志（若宿主支持），首张不再有写 plot.log 的停顿；探测失败维持原状。
            TryDisableAutomaticPlotLog,
            // 触发发布栈加载；不 BeginPlot，立即释放。
            () => { using var engine = PlotFactory.CreatePublishEngine(); },
            // 设备绑定 + 介质枚举，结果直接进介质名缓存。
            () => WarmUpMediaCatalog(deviceName),
            // 绑定设备并加载打印样式表。
            () => WarmUpDeviceAndStyle(deviceName, styleSheet),
            // 驱动级预热：实际绘图驱动在首次 BeginDocument 才加载，用近空白小图逼出来。
            () => TryWarmUpDriverWithBlankPlot(deviceName, styleSheet),
        };
    }

    private static void RunWarmUpStages(IEnumerable<Action> stages)
    {
        foreach (var stage in stages)
        {
            try
            {
                stage();
            }
            catch
            {
            }
        }
    }

    private static void WarmUpMediaCatalog(string deviceName)
    {
        try
        {
            var validator = PlotSettingsValidator.Current;
            using var settings = new PlotSettings(false);
            validator.SetPlotConfigurationName(settings, deviceName, null);
            validator.RefreshLists(settings);
            TrySetPlotPaperUnits(validator, settings, PlotPaperUnit.Millimeters);
            // 介质名只由设备（PC3/PMP）决定，与模型/布局无关：一次枚举同时填充两个缓存键。
            SetCachedMediaNames(deviceName, true, GetMediaNames(validator, settings, deviceName, false));
        }
        catch
        {
        }
    }

    private static void WarmUpDeviceAndStyle(string deviceName, string? styleSheet)
    {
        try
        {
            var validator = PlotSettingsValidator.Current;
            using var settings = new PlotSettings(false);
            validator.SetPlotConfigurationName(settings, deviceName, null);
            validator.RefreshLists(settings);
            if (!string.IsNullOrWhiteSpace(styleSheet))
            {
                validator.SetCurrentStyleSheet(settings, styleSheet);
            }
        }
        catch
        {
        }
    }

    /**
     * TryWarmUpDriverWithBlankPlot：向临时目录打一张 1mm 空白窗口的小图并删除。
     * 走与正式出图完全相同的 RunPlot 管线，把首次 BeginDocument 的驱动加载、
     * 输出校验（PdfSharp 等）全部提前到批打之前；预热图几乎无图形生成量，耗时极短。
     */
    private static void TryWarmUpDriverWithBlankPlot(string deviceName, string? styleSheet)
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            "LA_plot_warmup_" + Guid.NewGuid().ToString("N") + GetWarmUpExtension(deviceName));
        var oldWorkingDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = doc.Database;
        BatchPlotHostProgress.Begin();
        try
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var layoutId = LayoutManager.Current.GetLayoutId("Model");
                using var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                var validator = PlotSettingsValidator.Current;
                using var settings = new PlotSettings(layout.ModelType);
                settings.CopyFrom(layout);
                validator.SetPlotConfigurationName(settings, deviceName, null);
                validator.RefreshLists(settings);
                // 栅格设备只接受 Pixels，强制 Millimeters 会抛 eInvalidInput。
                if (!IsRasterPlotDevice(deviceName))
                {
                    TrySetPlotPaperUnits(validator, settings, PlotPaperUnit.Millimeters);
                }

                // 先写窗口再切打印类型：部分版本布局在无窗口时切 Window 会抛 eInvalidInput。
                validator.SetPlotWindowArea(settings, new Extents2d(new Point2d(0, 0), new Point2d(1, 1)));
                validator.SetPlotType(settings, ZwSoft.ZwCAD.DatabaseServices.PlotType.Window);
                validator.SetPlotCentered(settings, true);
                if (!string.IsNullOrWhiteSpace(styleSheet))
                {
                    validator.SetCurrentStyleSheet(settings, styleSheet);
                }

                var info = new PlotInfo
                {
                    Layout = layoutId,
                    OverrideSettings = settings
                };
                new PlotInfoValidator
                {
                    MediaMatchingPolicy = MatchingPolicy.MatchEnabled
                }.Validate(info);

                RunPlot(info, "LA打印预热", tempPath, "预热");
                tr.Commit();
            }

            try
            {
                if (File.Exists(tempPath))
                {
                    ValidatePlotOutput(tempPath);
                }
            }
            catch
            {
            }

            WaitForPlotIdle();
        }
        catch
        {
            // 预热图任一环节失败都直接放弃，正式批打不受影响。
        }
        finally
        {
            BatchPlotHostProgress.End();
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    /** GetWarmUpExtension：按设备名推断预热临时文件的扩展名。 */
    private static string GetWarmUpExtension(string deviceName)
    {
        var name = deviceName.ToUpperInvariant();
        if (name.Contains("PNG"))
        {
            return ".png";
        }

        if (name.Contains("JPG") || name.Contains("JPEG"))
        {
            return ".jpg";
        }

        if (name.Contains("DWF"))
        {
            return ".dwf";
        }

        return ".pdf";
    }

    /**
     * TryDisableAutomaticPlotLog：等价 acedSetEnv("AutomaticPlotLog","0")。
     * AutomaticPlotLog 是环境变量而非系统变量，.NET API 未直接暴露；
     * 按导出修饰名探测宿主模块的 acedSetEnv（x64），不可用则静默跳过。
     */
    private static void TryDisableAutomaticPlotLog()
    {
        try
        {
            foreach (var moduleName in new[] { "zwcad.exe", "ZwCore.dll", "accore.dll" })
            {
                var module = Native.GetModuleHandle(moduleName);
                if (module == IntPtr.Zero)
                {
                    continue;
                }

                // 两个候选覆盖不同版本的 C++ 修饰差异（第二参数与首参同类型时编译器记作 0）。
                foreach (var entry in new[] { "?acedSetEnv@@YAHPEB_W0@Z", "?acedSetEnv@@YAHPEB_WPEB_W@Z" })
                {
                    var address = Native.GetProcAddress(module, entry);
                    if (address == IntPtr.Zero)
                    {
                        continue;
                    }

                    var setEnv = Marshal.GetDelegateForFunctionPointer<Native.SetEnvDelegate>(address);
                    setEnv("AutomaticPlotLog", "0");
                    return;
                }
            }
        }
        catch
        {
            // 只影响首张写日志这一小段开销，打印功能不受影响。
        }
    }

    private static class Native
    {
        internal delegate int SetEnvDelegate(
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string value);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string name);
    }
}
