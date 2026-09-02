using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

/**
 * @file PlotterService.SystemVariables.cs（AutoCAD）
 * @description 出图前临时覆写 CAD 系统变量，结束后还原。
 *
 * 主要功能：
 * - PlotSystemVariables.Apply：设置后台打印、合并发布、PDF SHX、透明度覆盖
 *
 * 核心代码：
 * - BACKGROUNDPLOT=0 / PUBLISHCOLLATE=0：保证同步出图可等待完成
 * - PLOTTRANSPARENCYOVERRIDE：2=打透明度，1=不打，避免被页面设置盖住
 *
 * 注意：必须 using/Dispose，异常路径也要还原，否则影响用户会话。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** PlotSystemVariables：出图期临时系统变量集合；Dispose 时全部还原。 */
    private sealed class PlotSystemVariables : IDisposable
    {
        private readonly List<(string Name, object? Value)> _oldValues = new();
        private bool _disposed;

        /** Apply：应用 BACKGROUNDPLOT/PUBLISHCOLLATE/PDFSHX/PLOTTRANSPARENCYOVERRIDE。 */
        public static PlotSystemVariables Apply(bool plotTransparency)
        {
            var variables = new PlotSystemVariables();
            variables.Set("BACKGROUNDPLOT", 0);
            variables.Set("PUBLISHCOLLATE", 0);
            variables.Set("PDFSHX", 0);
            // 2=打印透明度，1=不打印；避免页面设置或 PLOTTRANSPARENCYOVERRIDE 覆盖本次选项。
            variables.Set("PLOTTRANSPARENCYOVERRIDE", plotTransparency ? 2 : 1);
            return variables;
        }

        /** Set：记录旧值后写入系统变量。 */
        private void Set(string name, object value)
        {
            try
            {
                var oldValue = CadApp.GetSystemVariable(name);
                CadApp.SetSystemVariable(name, value);
                _oldValues.Add((name, oldValue));
            }
            catch
            {
            }
        }

        /** Dispose：按记录还原所有系统变量。 */
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var i = _oldValues.Count - 1; i >= 0; i--)
            {
                try
                {
                    CadApp.SetSystemVariable(_oldValues[i].Name, _oldValues[i].Value);
                }
                catch
                {
                }
            }
        }
    }
}
