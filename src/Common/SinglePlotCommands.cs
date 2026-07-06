using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands
{
    private static void SinglePlotCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var editor = doc.Editor;
        string? customPmpPath = null;
        string? customPaperName = null;
        try
        {
            var first = editor.GetPoint(new PromptPointOptions("\n选择图纸外框第一个角点: "));
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var second = editor.GetCorner(new PromptCornerOptions("\n选择图纸外框对角点: ", first.Value));
            if (second.Status != PromptStatus.OK)
            {
                return;
            }

            // GetPoint 和 GetCorner 返回的是当前 UCS（用户坐标系）下的坐标
            // 打印引擎 SetPlotWindowArea 需要的是 DCS（显示坐标系）
            //
            // 正确的做法（等价于 ObjectARX 的 acedTrans(UCS→DCS)）：
            //   UCS 两个对角点 → 展开为 4 个角点 → × UCS→DCS 矩阵 → 取一次 DCS 包围盒
            //
            // 错误的做法（会导致旋转 UCS 下窗口偏大）：
            //   UCS → WCS 包围盒（第一次放大，丢了旋转信息）→ WCS→DCS（第二次放大）
            var ucsP1 = first.Value;
            var ucsP2 = second.Value;

            // 构建 UCS → DCS 变换矩阵，等价于 acedTrans(point, UCS, DCS)
            var ucsToDcs = BuildUcsToDcsMatrix(editor);

            // UCS 矩形四个角点一步到位变换到 DCS，不在中间环节取包围盒
            var corners = new[]
            {
                new Point3d(ucsP1.X, ucsP1.Y, 0).TransformBy(ucsToDcs),
                new Point3d(ucsP2.X, ucsP1.Y, 0).TransformBy(ucsToDcs),
                new Point3d(ucsP1.X, ucsP2.Y, 0).TransformBy(ucsToDcs),
                new Point3d(ucsP2.X, ucsP2.Y, 0).TransformBy(ucsToDcs)
            };

            // 仅在最终的 DCS 取一次轴对齐包围盒，这是 CAD API 必须的
            var minX = corners.Min(p => p.X);
            var minY = corners.Min(p => p.Y);
            var maxX = corners.Max(p => p.X);
            var maxY = corners.Max(p => p.Y);
            var width = maxX - minX;
            var height = maxY - minY;
            if (width <= 1e-6 || height <= 1e-6)
            {
                MessageBox.Show("选择的图纸外框宽度或高度无效。", "单张打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 确保 LA_pdf 打印机已安装（必须在 PMP 修改之前，否则会被覆盖）
            var installResult = AcadPlotterInstaller.InstallBundledPlotter();
            if (!installResult.Installed)
            {
                editor.WriteMessage($"\nLA_pdf 打印机未安装: {installResult.Message}");
                MessageBox.Show("LA_pdf 打印机配置不完整，无法打印: " + installResult.Message,
                    "单张打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            var candidates = PaperSizeDetector.DetectCandidates(width, height);
            if (candidates.Count == 0)
            {
                // 推测比例，弹出自定义比例对话框
                var guessedScale = PaperSizeDetector.GuessScale(width, height);
                using var scaleForm = new CustomScaleForm(width, height, guessedScale);
                if (ShowModalDialog(scaleForm) != DialogResult.OK)
                {
                    return;
                }

                var scale = scaleForm.SelectedScale;
                var paperW = scaleForm.PaperWidthMm;
                var paperH = scaleForm.PaperHeightMm;

                // 向 LA_pdf.pmp 注册自定义纸张（自动适配 PIA 3.0 / PIA 2.0 / ZWCAD INI）
                try
                {
                    var plottersDir = AcadPlotterInstaller.GetPlottersDirectory();
                    customPmpPath = Path.Combine(plottersDir, "PMP Files", "LA_pdf.pmp");
                    if (File.Exists(customPmpPath))
                    {
                        customPaperName = PmpCustomPaper.RegisterCustomPaper(customPmpPath, paperW, paperH);
                        editor.WriteMessage($"\n自定义纸张注册: pmp={customPmpPath}, paperName={customPaperName ?? "(null)"}, paperW={paperW:0.##}, paperH={paperH:0.##}");
                    }
                }
                catch
                {
                    // PMP 注册失败不阻塞，继续用 UserDefined 名称尝试
                }

                candidates = new List<PaperDetection>
                {
                    new()
                    {
                        PaperName = customPaperName ?? "UserDefined",
                        PaperWidthMm = paperW,
                        PaperHeightMm = paperH,
                        ScaleValue = scale,
                        ScaleText = $"1:{scale}",
                        Note = $"自定义纸张 {paperW:0.##} x {paperH:0.##} mm"
                    }
                };
            }

            var sourceFile = string.IsNullOrWhiteSpace(doc.Database.Filename)
                ? doc.Name
                : doc.Database.Filename;

            using var form = new SinglePlotForm(sourceFile, width, height, candidates);
            if (ShowModalDialog(form) != DialogResult.OK)
            {
                return;
            }

            var paper = form.SelectedPaper;
            var outputPath = form.OutputPath;

            var settings = AppSettingsStore.Load();
            var (deviceName, styleSheet) = ResolveSinglePlotOptions(settings);
            var layoutName = LayoutManager.Current.CurrentLayout;
            var isPaperSpace = !doc.Database.TileMode;
            var baseName = Path.GetFileNameWithoutExtension(sourceFile);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "Drawing";
            }

            var job = new PlotJob
            {
                IsManualWindow = true,
                IsDcsWindow = true,
                SourceFile = sourceFile,
                SpaceName = layoutName,
                IsPaperSpace = isPaperSpace,
                DrawingNumber = baseName,
                Title = baseName,
                PaperName = paper.PaperName,
                ScaleText = paper.ScaleText,
                SizeText = $"{width:0.##} x {height:0.##}",
                PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                DetectionNote = "单张打印：用户框选图纸外框",
                PaperWidthMm = paper.PaperWidthMm,
                PaperHeightMm = paper.PaperHeightMm,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY,
                OutputPath = outputPath
            };

            if (form.IsPreview)
            {
                SaveLastPlotOptions(settings, deviceName, styleSheet);
                PlotterService.Preview(job, deviceName, styleSheet, doc);
                editor.WriteMessage("\n单张打印预览已打开。");
            }
            else
            {
                SaveLastPlotOptions(settings, deviceName, styleSheet);
                PlotterService.Plot(job, deviceName, styleSheet, doc, settings);
                editor.WriteMessage($"\n单张打印完成: {outputPath}");
                RevealFileInExplorer(outputPath);
                MessageBox.Show(
                    $"单张打印完成。\n纸张: {paper.PaperName} {paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm\n文件: {outputPath}",
                    "单张打印",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\n单张打印失败: " + ex.Message);
            MessageBox.Show("单张打印失败: " + ex.Message, "单张打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (customPmpPath != null && customPaperName != null)
            {
                PmpCustomPaper.RemoveCustomPaper(customPmpPath, customPaperName);
            }
        }
    }

    /// <summary>
    /// 选择单张打印使用的 PDF 打印机和打印样式表。
    /// 优先使用捆绑的 LA_pdf 打印机和 monochrome.ctb 样式。
    /// </summary>
    private static (string DeviceName, string StyleSheet) ResolveSinglePlotOptions(AppSettings settings)
    {
        using var plotSettings = new PlotSettings(true);
        var validator = PlotSettingsValidator.Current;
        var devices = validator.GetPlotDeviceList()
            .Cast<object>()
            .Select(value => value?.ToString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var device = FindPlotOption(devices, AcadPlotterInstaller.PreferredPdfPlotter)
            ?? FindPlotOption(devices, settings.LastPlotDevice)
            ?? devices.FirstOrDefault(value => value.IndexOf("PDF", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? throw new InvalidOperationException("没有找到可用的 PDF 打印机。");

        var styles = validator.GetPlotStyleSheetList()
            .Cast<object>()
            .Select(value => value?.ToString() ?? "")
            .Where(value => value.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var style = FindPlotOption(styles, settings.LastStyleSheet)
            ?? styles.FirstOrDefault(value => value.IndexOf("monochrome", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? "";
        return (device, style);
    }

    private static void SaveLastPlotOptions(AppSettings settings, string deviceName, string styleSheet)
    {
        // 单张打印没有独立样式下拉框，仍然把实际使用的设备/CTB 写回统一设置，供其它模块下次默认选中。
        settings.LastPlotDevice = deviceName;
        settings.LastStyleSheet = styleSheet;
        AppSettingsStore.Save(settings);
    }

    private static string? FindPlotOption(System.Collections.Generic.IEnumerable<string> values, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return null;
        }

        return values.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            ?? values.FirstOrDefault(value => value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
