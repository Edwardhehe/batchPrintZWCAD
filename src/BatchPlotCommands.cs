using System;
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
        CadMenuInstaller.Install();
    }

    public void Terminate()
    {
    }

    [CommandMethod("_ZBP_INTERNAL_ADD_TITLE_BLOCK")]
    public void AddTitleBlock()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var editor = doc.Editor;
        var db = doc.Database;

        var blockOptions = new PromptEntityOptions("\n选择要加入图框库的图框块: ");
        blockOptions.SetRejectMessage("\n请选择普通块参照。");
        blockOptions.AddAllowedClass(typeof(BlockReference), exactMatch: false);
        var blockResult = editor.GetEntity(blockOptions);
        if (blockResult.Status != PromptStatus.OK)
        {
            return;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var blockRef = (BlockReference)tr.GetObject(blockResult.ObjectId, OpenMode.ForRead);
        var blockName = CadTextExtractor.GetBlockName(blockRef, tr);
        var inverse = blockRef.BlockTransform.Inverse();

        var hasPrintRegion = TryGetOptionalRegion(
            editor,
            "\n框选图框打印外边界第一个角点，或回车使用块外包框: ",
            "\n框选图框打印外边界对角点: ",
            inverse,
            out var printRegion);

        if (!TryGetRegion(editor, "\n框选图名区域第一个角点: ", "\n框选图名区域对角点: ", inverse, out var titleRegion))
        {
            return;
        }

        if (!TryGetRegion(editor, "\n框选图号区域第一个角点: ", "\n框选图号区域对角点: ", inverse, out var numberRegion))
        {
            return;
        }

        var definition = new TitleBlockDefinition
        {
            BlockName = blockName,
            HasPrintRegion = hasPrintRegion,
            PrintRegion = printRegion,
            TitleRegion = titleRegion,
            DrawingNumberRegion = numberRegion,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };

        TitleBlockLibraryStore.Upsert(definition);
        tr.Commit();

        editor.WriteMessage($"\n已保存图框块: {blockName}");
        editor.WriteMessage(hasPrintRegion ? "\n已保存图框打印边界。" : "\n未保存打印边界，打印时使用块外包框。");
        editor.WriteMessage($"\n图框库: {TitleBlockLibraryStore.DefaultPath}");
    }

    [CommandMethod("_ZBP_INTERNAL_SHOW_PANEL", CommandFlags.Session)]
    public void ShowBatchPlotWindow()
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

    [CommandMethod("_ZBP_INTERNAL_OPEN_CONFIG")]
    public void OpenConfigDirectory()
    {
        System.IO.Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
        System.Diagnostics.Process.Start(TitleBlockLibraryStore.DefaultDirectory);
    }

    [CommandMethod("_ZBP_INTERNAL_MANAGE_LIBRARY")]
    public void ManageLibrary()
    {
        using var form = new TitleBlockLibraryManagerForm();
        CadApp.ShowModalDialog(form);
    }

    [CommandMethod("_ZBP_INTERNAL_SETTINGS")]
    public void ShowSettings()
    {
        using var form = new SettingsForm();
        CadApp.ShowModalDialog(form);
    }

    [CommandMethod("_ZBP_INTERNAL_RELOAD_MENU")]
    public void ReloadMenu()
    {
        CadMenuInstaller.Install(force: true);
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

    private static bool TryGetOptionalRegion(Editor editor, string firstPrompt, string secondPrompt, Matrix3d inverseBlockTransform, out LocalRectangle region)
    {
        region = new LocalRectangle();
        var firstOptions = new PromptPointOptions(firstPrompt)
        {
            AllowNone = true
        };
        var first = editor.GetPoint(firstOptions);
        if (first.Status == PromptStatus.None)
        {
            return false;
        }

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
}
