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
using Autodesk.AutoCAD.Runtime;
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
using ZwSoft.ZwCAD.Runtime;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands : IExtensionApplication
{
    private static BatchPlotForm? _batchPlotForm;
    private static RectangleBatchPlotForm? _rectangleBatchPlotForm;

    public void Initialize()
    {
        if (IsCoreConsole())
        {
            return;
        }

        AcadPlotterInstaller.InstallBundledPlotter();
        CadMenuInstaller.Install(force: true);
    }

    public void Terminate()
    {
    }

    private static bool IsCoreConsole()
    {
        try
        {
            var processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            return string.Equals(processName, "accoreconsole", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    [CommandMethod("ZBP_ADD_TITLE_BLOCK")]
    public void AddTitleBlock()
    {
        AddTitleBlockCore();
    }

    [CommandMethod("_ZBP_INTERNAL_ADD_TITLE_BLOCK")]
    public void AddTitleBlockLegacy()
    {
        AddTitleBlockCore();
    }

    [CommandMethod("ZBP_SHOW_PANEL", CommandFlags.Session)]
    public void ShowBatchPlotWindow()
    {
        ShowBatchPlotWindowCore();
    }

    [CommandMethod("_ZBP_INTERNAL_SHOW_PANEL", CommandFlags.Session)]
    public void ShowBatchPlotWindowLegacy()
    {
        ShowBatchPlotWindowCore();
    }

    [CommandMethod("ZBP_SINGLE_PLOT", CommandFlags.Session)]
    public void SinglePlot()
    {
        SinglePlotCore();
    }

    [CommandMethod("_ZBP_INTERNAL_SINGLE_PLOT", CommandFlags.Session)]
    public void SinglePlotLegacy()
    {
        SinglePlotCore();
    }

    [CommandMethod("ZBP_RECTANGLE_BATCH_PLOT", CommandFlags.Session)]
    public void RectangleBatchPlot()
    {
        ShowRectangleBatchPlotCore();
    }

    [CommandMethod("_ZBP_INTERNAL_RECTANGLE_BATCH_PLOT", CommandFlags.Session)]
    public void RectangleBatchPlotLegacy()
    {
        ShowRectangleBatchPlotCore();
    }

    [CommandMethod("ZBP_OPEN_CONFIG")]
    public void OpenConfigDirectory()
    {
        Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
        System.Diagnostics.Process.Start(TitleBlockLibraryStore.DefaultDirectory);
    }

    [CommandMethod("_ZBP_INTERNAL_OPEN_CONFIG")]
    public void OpenConfigDirectoryLegacy()
    {
        OpenConfigDirectory();
    }

    [CommandMethod("ZBP_MANAGE_LIBRARY", CommandFlags.Session)]
    public void ManageLibrary()
    {
        using var form = new TitleBlockLibraryManagerForm();
        ShowModalDialog(form);
    }

    [CommandMethod("_ZBP_INTERNAL_MANAGE_LIBRARY", CommandFlags.Session)]
    public void ManageLibraryLegacy()
    {
        ManageLibrary();
    }

    [CommandMethod("ZBP_SETTINGS", CommandFlags.Session)]
    public void ShowSettings()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        using var form = new SettingsForm(doc);
        if (ShowModalDialog(form) == DialogResult.OK && form.RequestPickDirectoryCellSizes && doc != null)
        {
            var settings = AppSettingsStore.Load();
            var ok = DirectoryTableGenerator.PromptCellSizes(doc, settings, out _, out var message);
            MessageBox.Show(message, "批量打印设置", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }

    [CommandMethod("_ZBP_INTERNAL_SETTINGS", CommandFlags.Session)]
    public void ShowSettingsLegacy()
    {
        ShowSettings();
    }

    [CommandMethod("ZBP_RELOAD_MENU")]
    public void ReloadMenu()
    {
        CadMenuInstaller.Install(force: true);
    }

    [CommandMethod("_ZBP_INTERNAL_RELOAD_MENU")]
    public void ReloadMenuLegacy()
    {
        ReloadMenu();
    }

    [CommandMethod("ZBP_INSTALL_AUTOLOAD")]
    public void InstallAutoload()
    {
        try
        {
            var roots = AutoloadManager.Install();
            MessageBox.Show(
                "已安装自动加载。\n\n下次启动AutoCAD会自动加载批量打印插件。\n\n写入位置:\n" + string.Join("\n", roots),
                "批量打印",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("安装自动加载失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    [CommandMethod("_ZBP_INTERNAL_INSTALL_AUTOLOAD")]
    public void InstallAutoloadLegacy()
    {
        InstallAutoload();
    }

    [CommandMethod("ZBP_UNINSTALL_AUTOLOAD")]
    public void UninstallAutoload()
    {
        try
        {
            var removed = AutoloadManager.Uninstall();
            MessageBox.Show(
                removed > 0
                    ? "已卸载自动加载。\n\n当前已加载的插件会在本次CAD会话继续可用，关闭CAD后不会再自动加载。"
                    : "没有找到已安装的自动加载项。",
                "批量打印",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("卸载自动加载失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    [CommandMethod("_ZBP_INTERNAL_UNINSTALL_AUTOLOAD")]
    public void UninstallAutoloadLegacy()
    {
        UninstallAutoload();
    }

    private static void AddTitleBlockCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            AddBlockLog("No active document.");
            return;
        }

        var editor = doc.Editor;
        AddBlockLog("Add title block command started.");

        try
        {
            var blockOptions = new PromptEntityOptions("\n选择要加入图框库的图框块: ");
            blockOptions.SetRejectMessage("\n请选择普通块参照。");
            blockOptions.AddAllowedClass(typeof(BlockReference), exactMatch: false);
            var blockResult = editor.GetEntity(blockOptions);
            AddBlockLog("Block prompt status: " + blockResult.Status);
            if (blockResult.Status != PromptStatus.OK)
            {
                return;
            }

            string blockName;
            Matrix3d blockTransform;
            Matrix3d inverse;
            string? nestedBlockName = null;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockRef = (BlockReference)tr.GetObject(blockResult.ObjectId, OpenMode.ForRead);
                blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                blockTransform = blockRef.BlockTransform;

                // Detect dynamic block with visibility states: use the visible inner block's name.
                if (TryGetVisibleNestedBlock(tr, blockRef, out var innerName, out var innerTransform))
                {
                    nestedBlockName = innerName;
                    blockName = innerName;
                    blockTransform = innerTransform * blockRef.BlockTransform;
                    AddBlockLog($"Dynamic block detected: outer={CadTextExtractor.GetBlockName(blockRef, tr)}, inner={innerName}");
                }

                tr.Commit();
            }

            AddBlockLog("Selected block: " + blockName);
            inverse = blockTransform.Inverse();

            var printStatus = TryGetOptionalRegion(
                editor,
                "\n框选图框打印外边界第一个角点，或回车使用块外包框: ",
                "\n框选图框打印外边界对角点: ",
                inverse,
                out var printRegion);

            if (printStatus == OptionalRegionStatus.Cancel)
            {
                AddBlockLog("Print boundary selection cancelled.");
                return;
            }

            var hasPrintRegion = printStatus == OptionalRegionStatus.Selected;
            Extents3d blockExtents = default;
            if (!hasPrintRegion && !TryGetBlockExtents(doc.Database, blockResult.ObjectId, out blockExtents))
            {
                AddBlockLog("Block geometric extents are invalid; requiring an explicit print boundary.");
                MessageBox.Show(
                    "AutoCAD 无法取得该图框块的有效外包框，请手动框选打印边界。",
                    "批量打印",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                if (!TryGetRegion(
                        editor,
                        "\n框选图框打印外边界第一个角点: ",
                        "\n框选图框打印外边界对角点: ",
                        inverse,
                        out printRegion))
                {
                    AddBlockLog("Required print boundary selection cancelled.");
                    return;
                }

                hasPrintRegion = true;
            }

            AddBlockLog("Has print region: " + hasPrintRegion);

            if (!TryGetRegion(editor, "\n框选图名区域第一个角点: ", "\n框选图名区域对角点: ", inverse, out var titleRegion))
            {
                AddBlockLog("Title region selection cancelled.");
                return;
            }

            if (!TryGetRegion(editor, "\n框选图号区域第一个角点: ", "\n框选图号区域对角点: ", inverse, out var numberRegion))
            {
                AddBlockLog("Drawing number region selection cancelled.");
                return;
            }

            var printExtents = hasPrintRegion
                ? TransformRegion(printRegion, blockTransform)
                : blockExtents;
            var referenceFrame = hasPrintRegion
                ? printRegion
                : TransformExtents(blockExtents, inverse);

            var detected = PaperSizeDetector.Detect(
                printExtents.MaxPoint.X - printExtents.MinPoint.X,
                printExtents.MaxPoint.Y - printExtents.MinPoint.Y);

            AddBlockLog($"Detected paper: {detected.PaperName}, {detected.PaperWidthMm:0.##} x {detected.PaperHeightMm:0.##}");

            using var paperForm = new PaperSizeSelectionForm(detected);
            var paperResult = ShowModalDialog(paperForm);
            AddBlockLog("Paper dialog result: " + paperResult);
            if (paperResult != DialogResult.OK)
            {
                return;
            }

            var now = DateTime.Now;
            var definition = new TitleBlockDefinition
            {
                BlockName = blockName,
                HasPrintRegion = hasPrintRegion,
                CoordinateMode = "Frame",
                PrintRegion = referenceFrame,
                PaperName = paperForm.PaperName,
                PaperWidthMm = paperForm.PaperWidthMm,
                PaperHeightMm = paperForm.PaperHeightMm,
                TitleRegion = ToFrameRelative(titleRegion, referenceFrame),
                DrawingNumberRegion = ToFrameRelative(numberRegion, referenceFrame),
                CreatedAt = now,
                UpdatedAt = now
            };

            var inserted = TitleBlockLibraryStore.Upsert(definition);
            var saved = TitleBlockLibraryStore.Load();
            var savedDefinition = saved.Blocks.FirstOrDefault(x =>
                string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));

            AddBlockLog($"Saved. inserted={inserted}, libraryCount={saved.Blocks.Count}, verifyFound={savedDefinition != null}, path={TitleBlockLibraryStore.DefaultPath}");
            if (savedDefinition == null)
            {
                throw new InvalidOperationException("图框库保存后回读验证失败，请检查配置文件权限。");
            }

            editor.WriteMessage(inserted
                ? $"\n已新增图框块: {blockName}"
                : $"\n已更新已有图框块: {blockName}");
            editor.WriteMessage($"\n固定输出纸张: {definition.PaperName} {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} mm");
            editor.WriteMessage(hasPrintRegion
                ? "\n已保存图框打印边界。"
                : "\n未保存打印边界，打印时使用块外包框。");
            editor.WriteMessage($"\n图框库: {TitleBlockLibraryStore.DefaultPath}");

            MessageBox.Show(
                $"图框已保存: {blockName}\n纸张: {definition.PaperName} {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} mm",
                "批量打印",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (System.Exception ex)
        {
            AddBlockLog("Failed: " + ex);
            editor.WriteMessage("\n新增图框失败: " + ex.Message);
            MessageBox.Show("新增图框失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void ShowBatchPlotWindowCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        if (_batchPlotForm is { IsDisposed: false })
        {
            _batchPlotForm.Activate();
            return;
        }

        var form = new BatchPlotForm(doc);
        _batchPlotForm = form;
        form.FormClosed += (_, _) =>
        {
            try
            {
                if (form.HasPendingPrint)
                {
                    form.ExecutePendingPrint();
                }
            }
            finally
            {
                if (ReferenceEquals(_batchPlotForm, form))
                {
                    _batchPlotForm = null;
                }

                form.Dispose();
            }
        };

        ShowModelessDialog(form);
    }

    private static void SinglePlotCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var editor = doc.Editor;
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
            // 例如：用户设了旋转 30° 的 UCS，框选的角点就是 UCS 坐标
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

            var candidates = PaperSizeDetector.DetectCandidates(width, height);
            if (candidates.Count == 0)
            {
                candidates = new List<PaperDetection> { PaperSizeDetector.Detect(width, height) };
            }

            if (candidates[0].PaperWidthMm <= 0 || candidates[0].PaperHeightMm <= 0)
            {
                MessageBox.Show("无法根据所选外框识别纸张尺寸。", "单张打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
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
            AcadPlotterInstaller.InstallBundledPlotter();
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
                PlotterService.Preview(job, deviceName, styleSheet, doc);
                editor.WriteMessage("\n单张打印预览已打开。");
            }
            else
            {
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
    }

    private static void ShowRectangleBatchPlotCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        if (_rectangleBatchPlotForm is { IsDisposed: false })
        {
            _rectangleBatchPlotForm.Activate();
            return;
        }

        var editor = doc.Editor;
        if (!doc.Database.TileMode)
        {
            try
            {
                // A layout viewport makes editor picks use model-space coordinates.
                // Return to paper space so the scan window and layout entities share
                // the same coordinate system, and viewport contents are not scanned.
                editor.SwitchToPaperSpace();
            }
            catch
            {
                try
                {
                    CadApp.SetSystemVariable("CVPORT", 1);
                }
                catch
                {
                }
            }
        }

        var first = editor.GetPoint(new PromptPointOptions("\n框选矩形图框扫描范围第一个角点: "));
        if (first.Status != PromptStatus.OK)
        {
            return;
        }

        var second = editor.GetCorner(new PromptCornerOptions("\n框选矩形图框扫描范围对角点: ", first.Value));
        if (second.Status != PromptStatus.OK)
        {
            return;
        }

        var window = new Extents3d(
            new Point3d(
                Math.Min(first.Value.X, second.Value.X),
                Math.Min(first.Value.Y, second.Value.Y),
                0),
            new Point3d(
                Math.Max(first.Value.X, second.Value.X),
                Math.Max(first.Value.Y, second.Value.Y),
                0));

        try
        {
            var results = RectangleFrameScanner.ScanWindow(doc, window);
            if (results.Count == 0)
            {
                MessageBox.Show(
                    "框选范围内没有识别到符合常见纸张比例的矩形框。",
                    "批量打印(选矩形框)",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var form = new RectangleBatchPlotForm(doc, window, results);
            _rectangleBatchPlotForm = form;
            form.FormClosed += (_, _) =>
            {
                if (ReferenceEquals(_rectangleBatchPlotForm, form))
                {
                    _rectangleBatchPlotForm = null;
                }
                form.Dispose();
            };
            ShowModelessDialog(form);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(
                "矩形框识别失败: " + ex.Message,
                "批量打印(选矩形框)",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

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

    private static string? FindPlotOption(System.Collections.Generic.IEnumerable<string> values, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return null;
        }

        return values.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            ?? values.FirstOrDefault(value => value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>构建 UCS → DCS 变换矩阵：等价于 acedTrans(UCS, DCS)。</summary>
    /// <summary>
    /// 构建 UCS → DCS 变换矩阵，等价于 ObjectARX 的 acedTrans(point, 1, 2)。
    ///
    /// 原理：
    ///   UCS→DCS = UCS→WCS × WCS→DCS
    ///
    ///   UCS→WCS 直接从编辑器取 CurrentUserCoordinateSystem。
    ///   WCS→DCS 通过 GetCurrentView 获取当前视图的方向(VectorDirection)、
    ///   目标点(Target)和扭转角(ViewTwist)构造，和 PlotterService.GetWorldToDisplayMatrix
    ///   逻辑完全一致（官方 Autodesk 文档推荐的矩阵构造方式）。
    ///
    ///   图纸空间没有视图旋转概念，直接返回 UCS→WCS 即可。
    /// </summary>
    private static Matrix3d BuildUcsToDcsMatrix(Editor editor)
    {
        // 第一步：UCS → WCS
        // CurrentUserCoordinateSystem 是 CAD 原生维护的 UCS→WCS 矩阵
        // UCS=WCS 时此矩阵为单位矩阵，TransformBy 不起作用
        var ucsToWcs = editor.CurrentUserCoordinateSystem;

        // 第二步：WCS → DCS（仅模型空间需要，图纸空间无视图变换）
        var doc = editor.Document;
        if (doc.Database.TileMode)
        {
            try
            {
                var view = editor.GetCurrentView();

                // 按官方文档构造 DCS→WCS 矩阵（显示坐标系到世界坐标系）
                // PlaneToWorld: 将 DCS 的 XY 平面法线对齐到 ViewDirection
                var wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                // Displacement: 平移使 Target 为原点
                wcsToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcsToDcs;
                // Rotation: 绕 ViewDirection 旋转 ViewTwist 角度
                wcsToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;
                // 取逆得到 WCS→DCS
                wcsToDcs = wcsToDcs.Inverse();

                // 合并两个变换：UCS→WCS→DCS
                // PreMultiplyBy(A) = A × this，最终矩阵 = wcsToDcs × ucsToWcs
                return ucsToWcs.PreMultiplyBy(wcsToDcs);
            }
            catch (System.Exception ex)
            {
                // 老版本 CAD 或某些状态下 GetCurrentView 可能抛异常
                // 此时退回 UCS→WCS，打印窗口可能偏大但不影响输出
                editor.WriteMessage($"\n单张打印 UCS→DCS 变换失败，退回 UCS→WCS：{ex.Message}");
            }
        }

        // 图纸空间：DCS = UCS（没有视图旋转概念）
        return ucsToWcs;
    }

    private static void RevealFileInExplorer(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{Path.GetFullPath(filePath)}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // PDF 已成功生成；资源管理器打开失败不应把打印标记为失败。
        }
    }

    private static DialogResult ShowModalDialog(Form form)
    {
#if ACAD_CORE
        return form.ShowDialog();
#else
        return CadApp.ShowModalDialog(form);
#endif
    }

    private static void ShowModelessDialog(Form form)
    {
#if ACAD_CORE
        form.Show();
#else
        CadApp.ShowModelessDialog(form);
#endif
    }

    private static bool TryGetRegion(Editor editor, string firstPrompt, string secondPrompt, Matrix3d inverseBlockTransform, out LocalRectangle region)
    {
        region = new LocalRectangle();
        var first = editor.GetPoint(new PromptPointOptions(firstPrompt));
        if (first.Status != PromptStatus.OK)
        {
            return false;
        }

        var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
        var second = editor.GetCorner(cornerOptions);
        if (second.Status != PromptStatus.OK)
        {
            return false;
        }

        var p1 = first.Value.TransformBy(inverseBlockTransform);
        var p2 = second.Value.TransformBy(inverseBlockTransform);
        region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
        return true;
    }

    private static OptionalRegionStatus TryGetOptionalRegion(
        Editor editor,
        string firstPrompt,
        string secondPrompt,
        Matrix3d inverseBlockTransform,
        out LocalRectangle region)
    {
        region = new LocalRectangle();
        var firstOptions = new PromptPointOptions(firstPrompt)
        {
            AllowNone = true
        };
        var first = editor.GetPoint(firstOptions);
        if (first.Status == PromptStatus.None)
        {
            return OptionalRegionStatus.None;
        }

        if (first.Status != PromptStatus.OK)
        {
            return OptionalRegionStatus.Cancel;
        }

        var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
        var second = editor.GetCorner(cornerOptions);
        if (second.Status != PromptStatus.OK)
        {
            return OptionalRegionStatus.Cancel;
        }

        var p1 = first.Value.TransformBy(inverseBlockTransform);
        var p2 = second.Value.TransformBy(inverseBlockTransform);
        region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
        return OptionalRegionStatus.Selected;
    }

    private static bool TryGetBlockExtents(Database database, ObjectId blockReferenceId, out Extents3d extents)
    {
        extents = default;
        using var tr = database.TransactionManager.StartTransaction();
        var blockRef = (BlockReference)tr.GetObject(blockReferenceId, OpenMode.ForRead);

        try
        {
            var direct = blockRef.GeometricExtents;
            if (HasValidExtents(direct))
            {
                extents = direct;
                return true;
            }
        }
        catch
        {
            // AutoCAD can report eInvalidExtents for dynamic blocks or entities
            // whose graphics extents have not been generated yet.
        }

        var hasExtents = false;
        var combined = default(Extents3d);
        var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
        foreach (ObjectId entityId in definition)
        {
            try
            {
                var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (entity == null)
                {
                    continue;
                }

                var transformed = TransformWorldExtents(entity.GeometricExtents, blockRef.BlockTransform);
                if (!HasValidExtents(transformed))
                {
                    continue;
                }

                if (!hasExtents)
                {
                    combined = transformed;
                    hasExtents = true;
                }
                else
                {
                    combined.AddExtents(transformed);
                }
            }
            catch
            {
                // Ignore individual entities without valid graphics extents.
            }
        }

        if (!hasExtents || !HasValidExtents(combined))
        {
            return false;
        }

        extents = combined;
        return true;
    }

    private static bool HasValidExtents(Extents3d extents)
    {
        var values = new[]
        {
            extents.MinPoint.X, extents.MinPoint.Y,
            extents.MaxPoint.X, extents.MaxPoint.Y
        };
        return values.All(value => !double.IsNaN(value) && !double.IsInfinity(value))
            && extents.MaxPoint.X - extents.MinPoint.X > 1e-6
            && extents.MaxPoint.Y - extents.MinPoint.Y > 1e-6;
    }

    private static Extents3d TransformWorldExtents(Extents3d extents, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z).TransformBy(transform),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, extents.MinPoint.Z).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, extents.MinPoint.Z).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z).TransformBy(transform)
        };

        return new Extents3d(
            new Point3d(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
            new Point3d(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
    }

    private static Extents3d TransformRegion(LocalRectangle region, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(transform)
        };

        var minX = Math.Min(Math.Min(points[0].X, points[1].X), Math.Min(points[2].X, points[3].X));
        var minY = Math.Min(Math.Min(points[0].Y, points[1].Y), Math.Min(points[2].Y, points[3].Y));
        var maxX = Math.Max(Math.Max(points[0].X, points[1].X), Math.Max(points[2].X, points[3].X));
        var maxY = Math.Max(Math.Max(points[0].Y, points[1].Y), Math.Max(points[2].Y, points[3].Y));
        return new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
    }

    private static LocalRectangle TransformExtents(Extents3d extents, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform)
        };

        return LocalRectangle.FromPoints(
            points.Min(p => p.X),
            points.Min(p => p.Y),
            points.Max(p => p.X),
            points.Max(p => p.Y));
    }

    private static LocalRectangle ToFrameRelative(LocalRectangle region, LocalRectangle referenceFrame)
    {
        return LocalRectangle.FromPoints(
            region.MinX - referenceFrame.MinX,
            region.MinY - referenceFrame.MinY,
            region.MaxX - referenceFrame.MinX,
            region.MaxY - referenceFrame.MinY);
    }

    private static void AddBlockLog(string message)
    {
        try
        {
            var logDirectory = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "AddTitleBlock_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    /// <summary>
    /// When <paramref name="blockRef"/> is a container block whose definition contains
    /// nested block references (e.g. a dynamic block with visibility states), resolve
    /// the visible inner block's effective name and its transform relative to the outer block.
    /// Returns false if no nested blocks are found on visible layers.
    /// </summary>
    private static bool TryGetVisibleNestedBlock(
        Transaction tr,
        BlockReference blockRef,
        out string innerBlockName,
        out Matrix3d innerTransform)
    {
        innerBlockName = "";
        innerTransform = Matrix3d.Identity;

        // 只针对动态块：普通块即使内有嵌套块也不深入，保持原有行为
        if (!blockRef.IsDynamicBlock)
        {
            return false;
        }

        var definitionId = blockRef.BlockTableRecord;
        if (definitionId.IsNull)
        {
            return false;
        }

        var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        var nestedBlocks = new List<(string Name, Matrix3d Transform)>();

        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference nested)
            {
                continue;
            }

            if (!IsEntityVisible(nested))
            {
                continue;
            }

            var nestedName = CadTextExtractor.GetBlockName(nested, tr);
            if (!string.IsNullOrWhiteSpace(nestedName))
            {
                nestedBlocks.Add((nestedName, nested.BlockTransform));
            }
        }

        if (nestedBlocks.Count == 0)
        {
            return false;
        }

        // Dynamic block visibility states typically leave exactly one nested block visible.
        // If multiple are on visible layers, pick the one with the largest bounding area.
        var selected = nestedBlocks[0];
        if (nestedBlocks.Count > 1)
        {
            double bestArea = 0;
            foreach (var block in nestedBlocks)
            {
                var area = Math.Abs(block.Transform[0, 0] * block.Transform[1, 1]);
                if (area > bestArea)
                {
                    bestArea = area;
                    selected = block;
                }
            }
        }

        innerBlockName = selected.Name;
        innerTransform = selected.Transform;
        return true;
    }

    private static bool IsEntityVisible(Entity entity)
    {
        try
        {
            return entity.Visible;
        }
        catch
        {
            return true;
        }
    }

    private enum OptionalRegionStatus
    {
        Cancel,
        None,
        Selected
    }
}
