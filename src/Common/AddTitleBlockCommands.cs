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
            ObjectId frameDefinitionId;
            bool isPaperSpace;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockRef = (BlockReference)tr.GetObject(blockResult.ObjectId, OpenMode.ForRead);
                blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                blockTransform = blockRef.BlockTransform;
                frameDefinitionId = blockRef.BlockTableRecord;

                // 纸张候选顺序必须与矩形框批打一致：模型空间优先 1:100、再 1:1；
                // 布局空间优先 1:1、再 1:100。以所选块实际所属布局判断，不能只看当前 TileMode。
                var owner = (BlockTableRecord)tr.GetObject(blockRef.OwnerId, OpenMode.ForRead);
                isPaperSpace = owner.IsLayout
                    && !owner.LayoutId.IsNull
                    && !((Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead)).ModelType;

                // 动态块通过可见性状态切换不同尺寸
                // 入库块名使用“外层块名+内层可见嵌套块名”复合名，变换矩阵取内层嵌套块的
                if (TryGetVisibleNestedBlock(
                        tr,
                        blockRef,
                        out var innerName,
                        out var innerTransform,
                        out var innerDefinitionId))
                {
                    blockName = blockName + "+" + innerName;
                    blockTransform = innerTransform * blockRef.BlockTransform;
                    frameDefinitionId = innerDefinitionId;
                    AddBlockLog($"Dynamic block detected: outer={CadTextExtractor.GetBlockName(blockRef, tr)}, inner={innerName}, stored={blockName}");
                }

                tr.Commit();
            }

            AddBlockLog("Selected block: " + blockName);

            // 同名图框已入库时先确认：用户选“是”才继续后续框选流程，保存时覆盖原记录。
            var existingLibrary = TitleBlockLibraryStore.Load();
            if (existingLibrary.Blocks.Any(x =>
                    string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase)))
            {
                var overwrite = MessageBox.Show(
                    $"图框库中已存在同名图框: {blockName}\n是否重新录入？\n选择“是”将覆盖原有图框设置。",
                    "批量打印",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (overwrite != DialogResult.Yes)
                {
                    AddBlockLog("Duplicate block name; user chose not to overwrite.");
                    editor.WriteMessage($"\n已取消录入，图框库中的 {blockName} 保持不变。");
                    return;
                }
            }

            inverse = blockTransform.Inverse();

            // 自动范围优先使用块内面积最大的闭合矩形；找不到时再合并可见线类图素的包围盒。
            // 识别结果直接保留在入库基准定义空间，避免“世界包围盒再反变换”放大旋转图框。
            Extents3d printExtents;
            LocalRectangle referenceFrame;
            if (TryGetBlockFrame(
                    doc.Database,
                    frameDefinitionId,
                    out referenceFrame,
                    out var frameSource))
            {
                printExtents = TransformRegion(referenceFrame, blockTransform);
                AddBlockLog(frameSource == BlockFrameSource.ClosedRectangle
                    ? "Outer frame detected from the largest closed rectangle inside block."
                    : "No closed rectangle found; outer frame detected from visible line geometry extents.");
            }
            else if (TryGetBlockExtents(doc.Database, blockResult.ObjectId, out var blockExtents))
            {
                printExtents = blockExtents;
                referenceFrame = TransformExtents(blockExtents, inverse);
            }
            else
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
                        out var manualFrame))
                {
                    AddBlockLog("Required print boundary selection cancelled.");
                    return;
                }

                referenceFrame = manualFrame;
                printExtents = TransformRegion(manualFrame, blockTransform);
            }

            // 外框和字段区域的红色临时标识；using 确保保存、取消或异常时全部删除。
            using var markers = new TransientFrameMarkers(editor);

            // 外框临时标识初始绘制：红色矩形框 + 两条对角线。
            // 对话框内也可重新框选修改打印范围，OnVisibleChanged 时会自动刷新。
            markers.SetBox("外框", printExtents.MinPoint, printExtents.MaxPoint, null);
            editor.WriteMessage("\n已自动识别图框外框（红色临时标识），请在弹出窗口中框选图名、图号等字段区域。");

            // 与矩形框批打共用同一候选策略：短边匹配、1/8 加长模数、任意加长纸回退，
            // 并把 1:100 / 1:1 按当前空间顺序放在其他比例之前。
            var placedFrame = RectangleGeometry.TransformRectangle(referenceFrame, blockTransform);
            var detectedWidth = placedFrame.ActualWidth > 0
                ? placedFrame.ActualWidth
                : printExtents.MaxPoint.X - printExtents.MinPoint.X;
            var detectedHeight = placedFrame.ActualHeight > 0
                ? placedFrame.ActualHeight
                : printExtents.MaxPoint.Y - printExtents.MinPoint.Y;
            var settings = AppSettingsStore.Load();
            var paperDetectionOptions = PaperSizeDetector.CreateRectangleBatchOptions(
                settings.PaperMatchToleranceMm,
                isPaperSpace,
                settings.LongPaperSnapToleranceMm);
            var paperOptions = PaperSizeDetector.DetectCandidatesOrFallback(
                detectedWidth,
                detectedHeight,
                paperDetectionOptions);
            var detected = paperOptions[0];

            AddBlockLog($"Detected {paperOptions.Count} paper option(s); preferred: {detected.PaperName}, {detected.PaperWidthMm:0.##} x {detected.PaperHeightMm:0.##}, {detected.ScaleText}");

            // 字段（图名/图号必选，日期/版次/设计阶段/信息1/信息2可选）框选 + 纸张设置，同一对话框完成。
            // 同时支持修改打印范围（外框），识别不准时可手动重新框选。
            LocalRectangle titleRegion;
            LocalRectangle numberRegion;
            LocalRectangle dateRegion;
            LocalRectangle revisionRegion;
            LocalRectangle phaseRegion;
            LocalRectangle info1Region;
            LocalRectangle info2Region;
            string paperName;
            double paperWidthMm;
            double paperHeightMm;
            using (var fieldDialog = new FieldBoxSelectDialog(
                       editor,
                       inverse,
                       blockTransform,
                       markers,
                       referenceFrame,
                       paperOptions,
                       paperDetectionOptions))
            {
                if (ShowModalDialog(fieldDialog) != DialogResult.OK)
                {
                    AddBlockLog("Field selection cancelled.");
                    return;
                }

                titleRegion = fieldDialog.TitleRegion;
                numberRegion = fieldDialog.DrawingNumberRegion;
                dateRegion = fieldDialog.DateRegion;
                revisionRegion = fieldDialog.RevisionRegion;
                phaseRegion = fieldDialog.PhaseRegion;
                info1Region = fieldDialog.Info1Region;
                info2Region = fieldDialog.Info2Region;

                // 用户可能在对话框中重新框选了打印范围，读取最新值并重算世界坐标。
                referenceFrame = fieldDialog.ReferenceFrame;
                printExtents = TransformRegion(referenceFrame, blockTransform);

                paperName = fieldDialog.PaperName;
                paperWidthMm = fieldDialog.PaperWidthMm;
                paperHeightMm = fieldDialog.PaperHeightMm;
            }

            AddBlockLog($"Title region: ({titleRegion.MinX:0.###},{titleRegion.MinY:0.###})-({titleRegion.MaxX:0.###},{titleRegion.MaxY:0.###})");
            AddBlockLog($"Number region: ({numberRegion.MinX:0.###},{numberRegion.MinY:0.###})-({numberRegion.MaxX:0.###},{numberRegion.MaxY:0.###})");
            if (dateRegion.HasArea()) AddBlockLog($"Date region: ({dateRegion.MinX:0.###},{dateRegion.MinY:0.###})-({dateRegion.MaxX:0.###},{dateRegion.MaxY:0.###})");
            if (revisionRegion.HasArea()) AddBlockLog($"Revision region: ({revisionRegion.MinX:0.###},{revisionRegion.MinY:0.###})-({revisionRegion.MaxX:0.###},{revisionRegion.MaxY:0.###})");
            if (phaseRegion.HasArea()) AddBlockLog($"Phase region: ({phaseRegion.MinX:0.###},{phaseRegion.MinY:0.###})-({phaseRegion.MaxX:0.###},{phaseRegion.MaxY:0.###})");
            if (info1Region.HasArea()) AddBlockLog($"Info1 region: ({info1Region.MinX:0.###},{info1Region.MinY:0.###})-({info1Region.MaxX:0.###},{info1Region.MaxY:0.###})");
            if (info2Region.HasArea()) AddBlockLog($"Info2 region: ({info2Region.MinX:0.###},{info2Region.MinY:0.###})-({info2Region.MaxX:0.###},{info2Region.MaxY:0.###})");

            // 新流程始终显式确定打印区域（自动识别或手动框选），因此 HasPrintRegion 始终为 true。
            const bool hasPrintRegion = true;

            var now = DateTime.Now;
            var definition = new TitleBlockDefinition
            {
                BlockName = blockName,
                HasPrintRegion = hasPrintRegion,
                CoordinateMode = "Frame",
                PrintRegion = referenceFrame,
                PaperName = paperName,
                PaperWidthMm = paperWidthMm,
                PaperHeightMm = paperHeightMm,
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
        out Matrix3d innerTransform,
        out ObjectId innerDefinitionId)
    {
        innerBlockName = "";
        innerTransform = Matrix3d.Identity;
        innerDefinitionId = ObjectId.Null;

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
        var nestedBlocks = new List<(string Name, Matrix3d Transform, ObjectId DefinitionId)>();

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
                nestedBlocks.Add((nestedName, nested.BlockTransform, nested.BlockTableRecord));
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
        innerDefinitionId = selected.DefinitionId;
        return true;
    }

    private enum BlockFrameSource
    {
        None,
        ClosedRectangle,
        LineExtents
    }

    /// <summary>
    /// 在指定入库基准定义空间内识别打印范围：优先最大闭合矩形，失败后回退到可见线类图素包围盒。
    /// 返回局部坐标而不是世界包围盒，确保块整体旋转时 referenceFrame 不会被二次放大。
    /// </summary>
    private static bool TryGetBlockFrame(
        Database database,
        ObjectId rootDefinitionId,
        out LocalRectangle frame,
        out BlockFrameSource source)
    {
        frame = new LocalRectangle();
        source = BlockFrameSource.None;
        if (rootDefinitionId.IsNull)
        {
            return false;
        }

        using var tr = database.TransactionManager.StartTransaction();
        var rectangles = new List<LocalRectangle>();
        var hasLineExtents = false;
        var lineExtents = default(Extents3d);
        CollectBlockFrameGeometry(
            tr,
            rootDefinitionId,
            Matrix3d.Identity,
            rectangles,
            ref hasLineExtents,
            ref lineExtents,
            new HashSet<ObjectId>(),
            depth: 0);
        tr.Commit();

        if (rectangles.Count > 0)
        {
            frame = rectangles
                .OrderByDescending(RectangleGeometry.GetActualArea)
                .First();
            source = BlockFrameSource.ClosedRectangle;
            return frame.HasArea();
        }

        if (!hasLineExtents || !HasValidExtents(lineExtents))
        {
            return false;
        }

        frame = CreateRectangleFromExtents(lineExtents);
        source = BlockFrameSource.LineExtents;
        return true;
    }

    /// <summary>
    /// 递归收集块内闭合矩形和线类图素。每层先在本层定义空间识别矩形，再把四角变换到根定义空间；
    /// 这样非等比缩放的块实例不会反过来影响“源多段线是否为矩形”的判断。
    /// </summary>
    private static void CollectBlockFrameGeometry(
        Transaction tr,
        ObjectId definitionId,
        Matrix3d definitionToRoot,
        ICollection<LocalRectangle> rectangles,
        ref bool hasLineExtents,
        ref Extents3d lineExtents,
        ISet<ObjectId> visitedDefinitions,
        int depth)
    {
        if (depth > 12 || definitionId.IsNull || !visitedDefinitions.Add(definitionId))
        {
            return;
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
            foreach (ObjectId entityId in definition)
            {
                if (tr.GetObject(entityId, OpenMode.ForRead, false) is not Entity entity
                    || !IsEntityVisible(entity))
                {
                    continue;
                }

                try
                {
                    var localRectangle = new LocalRectangle();
                    var isClosedRectangle = entity switch
                    {
                        Polyline polyline => RectangleGeometry.TryGetRectangle(
                            polyline,
                            Matrix3d.Identity,
                            requireClosed: true,
                            out localRectangle),
                        Polyline2d polyline2d => RectangleGeometry.TryGetRectangleFrom2d(
                            tr,
                            polyline2d,
                            Matrix3d.Identity,
                            requireClosed: true,
                            out localRectangle),
                        Polyline3d polyline3d => RectangleGeometry.TryGetRectangleFrom3d(
                            tr,
                            polyline3d,
                            Matrix3d.Identity,
                            requireClosed: true,
                            out localRectangle),
                        _ => false
                    };

                    if (isClosedRectangle)
                    {
                        rectangles.Add(RectangleGeometry.TransformRectangle(localRectangle, definitionToRoot));
                    }

                    // 合并包盒只统计线类实体，排除文字和属性，作为找不到闭合矩形时的兼容回退。
                    if (entity is Line or Polyline or Polyline2d or Polyline3d)
                    {
                        var transformedExtents = TransformWorldExtents(entity.GeometricExtents, definitionToRoot);
                        if (HasValidExtents(transformedExtents))
                        {
                            if (!hasLineExtents)
                            {
                                lineExtents = transformedExtents;
                                hasLineExtents = true;
                            }
                            else
                            {
                                lineExtents.AddExtents(transformedExtents);
                            }
                        }
                    }
                }
                catch
                {
                    // 个别损坏实体或无效外包框不应中断整个图框录入。
                }

                if (entity is not BlockReference nested || depth >= 12)
                {
                    continue;
                }

                try
                {
                    CollectBlockFrameGeometry(
                        tr,
                        nested.BlockTableRecord,
                        nested.BlockTransform * definitionToRoot,
                        rectangles,
                        ref hasLineExtents,
                        ref lineExtents,
                        visitedDefinitions,
                        depth + 1);
                }
                catch
                {
                    // 循环引用由 visited 处理；不可读取的嵌套定义直接跳过。
                }
            }
        }
        finally
        {
            // visited 只约束当前递归路径，允许同一定义从其他实例路径再次进入。
            visitedDefinitions.Remove(definitionId);
        }
    }

    private static LocalRectangle CreateRectangleFromExtents(Extents3d extents)
    {
        var rectangle = LocalRectangle.FromPoints(
            extents.MinPoint.X,
            extents.MinPoint.Y,
            extents.MaxPoint.X,
            extents.MaxPoint.Y);
        var width = rectangle.MaxX - rectangle.MinX;
        var height = rectangle.MaxY - rectangle.MinY;
        rectangle.ActualWidth = Math.Max(width, height);
        rectangle.ActualHeight = Math.Min(width, height);
        rectangle.CornerPoints = new[]
        {
            rectangle.MinX, rectangle.MinY,
            rectangle.MaxX, rectangle.MinY,
            rectangle.MaxX, rectangle.MaxY,
            rectangle.MinX, rectangle.MaxY
        };
        return rectangle;
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
