using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

public sealed class BatchPlotCommands : IExtensionApplication
{
    public void Initialize()
    {
        CadMenuInstaller.Install(force: true);
    }

    public void Terminate()
    {
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
        CadApp.ShowModalDialog(form);
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
        if (CadApp.ShowModalDialog(form) == DialogResult.OK && form.RequestPickDirectoryCellSizes && doc != null)
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
                "已安装自动加载。\n\n下次启动中望CAD会自动加载批量打印插件。\n\n写入位置:\n" + string.Join("\n", roots),
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
            Extents3d blockExtents;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockRef = (BlockReference)tr.GetObject(blockResult.ObjectId, OpenMode.ForRead);
                blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                blockTransform = blockRef.BlockTransform;
                blockExtents = blockRef.GeometricExtents;
                tr.Commit();
            }

            AddBlockLog("Selected block: " + blockName);
            var inverse = blockTransform.Inverse();

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

            var detected = PaperSizeDetector.Detect(
                printExtents.MaxPoint.X - printExtents.MinPoint.X,
                printExtents.MaxPoint.Y - printExtents.MinPoint.Y);

            AddBlockLog($"Detected paper: {detected.PaperName}, {detected.PaperWidthMm:0.##} x {detected.PaperHeightMm:0.##}");

            using var paperForm = new PaperSizeSelectionForm(detected);
            var paperResult = CadApp.ShowModalDialog(paperForm);
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
                CoordinateMode = "Local",
                PrintRegion = printRegion,
                PaperName = paperForm.PaperName,
                PaperWidthMm = paperForm.PaperWidthMm,
                PaperHeightMm = paperForm.PaperHeightMm,
                TitleRegion = titleRegion,
                DrawingNumberRegion = numberRegion,
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

        using var form = new BatchPlotForm(doc);
        CadApp.ShowModalDialog(form);
        if (form.HasPendingPrint)
        {
            form.ExecutePendingPrint();
        }
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

    private enum OptionalRegionStatus
    {
        Cancel,
        None,
        Selected
    }
}
