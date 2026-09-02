using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.PlottingServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

/**
 * @file PlotterService.SystemVariables.cs（ZWCAD）
 * @description 出图前临时覆写透明度系统变量，结束后还原。
 *
 * 主要功能：
 * - PlotTransparencyOverride.Apply：设置 PLOTTRANSPARENCYOVERRIDE
 *
 * 核心代码：
 * - 2=打印透明度，1=不打印；避免跟随页面设置被覆盖
 * - Dispose 时还原旧值，保证用户会话不被污染
 *
 * 注意：必须与 using 配合；捕获 SetSystemVariable 异常以免中断出图。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /**
     * PlotTransparencyOverride：
     * 按本次设置覆盖 CAD 的打印透明度系统变量，打印结束后还原。
     * 0=跟随页面设置，1=不打印透明度，2=打印透明度。
     */
    private sealed class PlotTransparencyOverride : IDisposable
    {
        private readonly object? _oldValue;
        private readonly bool _restore;
        private bool _disposed;

        /** Apply：按是否打印透明度写入系统变量（2=打印，1=不打印）。 */
        public static PlotTransparencyOverride Apply(bool enabled)
        {
            return new PlotTransparencyOverride(enabled);
        }

        /** PlotTransparencyOverride：构造时覆写 PLOTTRANSPARENCYOVERRIDE。 */
        private PlotTransparencyOverride(bool enabled)
        {
            try
            {
                _oldValue = CadApp.GetSystemVariable("PLOTTRANSPARENCYOVERRIDE");
                CadApp.SetSystemVariable("PLOTTRANSPARENCYOVERRIDE", enabled ? 2 : 1);
                _restore = true;
            }
            catch
            {
            }
        }

        /** Dispose：还原透明度系统变量旧值。 */
        public void Dispose()
        {
            if (_disposed || !_restore)
            {
                return;
            }

            _disposed = true;
            try
            {
                CadApp.SetSystemVariable("PLOTTRANSPARENCYOVERRIDE", _oldValue);
            }
            catch
            {
            }
        }
    }
}
