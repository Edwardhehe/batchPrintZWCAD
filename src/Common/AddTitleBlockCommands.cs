using System;
using System.Collections.Generic;
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
    private static void AddTitleBlockCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            AddBlockLog("No active document.");
            return;
        }

        var editor = doc.Editor;

        // 新增图框必须在 WCS 下操作，避免 UCS 旋转导致存储的坐标区域与后续扫描不匹配
        if (!editor.CurrentUserCoordinateSystem.IsEqualTo(Matrix3d.Identity))
        {
            MessageBox.Show("新增图框前请先将 UCS 切换为世界坐标系（WCS）。\n命令行输入 UCS 然后回车即可。",
                "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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

                // 动态块通过可见性状态切换不同尺寸
                // 入库时应使用当前可见的内层嵌套块的名称和变换矩阵
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

            // 可选字段（日期/版次/设计阶段/信息1/信息2）框选
            LocalRectangle dateRegion;
            LocalRectangle revisionRegion;
            LocalRectangle phaseRegion;
            LocalRectangle info1Region;
            LocalRectangle info2Region;
            using (var fieldDialog = new FieldBoxSelectDialog(editor, inverse))
            {
                if (ShowModalDialog(fieldDialog) != DialogResult.OK)
                {
                    AddBlockLog("Optional field selection cancelled.");
                    return;
                }

                dateRegion = fieldDialog.DateRegion;
                revisionRegion = fieldDialog.RevisionRegion;
                phaseRegion = fieldDialog.PhaseRegion;
                info1Region = fieldDialog.Info1Region;
                info2Region = fieldDialog.Info2Region;
            }

            if (dateRegion.HasArea()) AddBlockLog($"Date region: ({dateRegion.MinX:0.###},{dateRegion.MinY:0.###})-({dateRegion.MaxX:0.###},{dateRegion.MaxY:0.###})");
            if (revisionRegion.HasArea()) AddBlockLog($"Revision region: ({revisionRegion.MinX:0.###},{revisionRegion.MinY:0.###})-({revisionRegion.MaxX:0.###},{revisionRegion.MaxY:0.###})");
            if (phaseRegion.HasArea()) AddBlockLog($"Phase region: ({phaseRegion.MinX:0.###},{phaseRegion.MinY:0.###})-({phaseRegion.MaxX:0.###},{phaseRegion.MaxY:0.###})");
            if (info1Region.HasArea()) AddBlockLog($"Info1 region: ({info1Region.MinX:0.###},{info1Region.MinY:0.###})-({info1Region.MaxX:0.###},{info1Region.MaxY:0.###})");
            if (info2Region.HasArea()) AddBlockLog($"Info2 region: ({info2Region.MinX:0.###},{info2Region.MinY:0.###})-({info2Region.MaxX:0.###},{info2Region.MaxY:0.###})");

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
                DateRegion = dateRegion.HasArea() ? ToFrameRelative(dateRegion, referenceFrame) : new LocalRectangle(),
                RevisionRegion = revisionRegion.HasArea() ? ToFrameRelative(revisionRegion, referenceFrame) : new LocalRectangle(),
                PhaseRegion = phaseRegion.HasArea() ? ToFrameRelative(phaseRegion, referenceFrame) : new LocalRectangle(),
                // 信息1/信息2是用户自定义可选字段，未框选时保持空区域，后续命名会自动跳过空值。
                Info1Region = info1Region.HasArea() ? ToFrameRelative(info1Region, referenceFrame) : new LocalRectangle(),
                Info2Region = info2Region.HasArea() ? ToFrameRelative(info2Region, referenceFrame) : new LocalRectangle(),
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

    /// <summary>
    /// 针对动态块：深入一层找到当前可见的内层嵌套块，返回其名称和变换矩阵。
    /// 普通块（IsDynamicBlock=false）直接返回 false，不干扰。
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

            // CAD 引擎原生维护 entity.Visible，动态块隐藏状态自动为 false
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

        // 动态块的可见性状态通常只有一个嵌套块可见
        // 如果有多个可见，取包围盒面积最大的
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

    /// <summary>
    /// 查询实体是否可见。CAD 引擎原生维护，动态块切换状态时自动更新。
    /// API 不可用时返回 true（宁可多扫不丢）。
    /// </summary>
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
}
