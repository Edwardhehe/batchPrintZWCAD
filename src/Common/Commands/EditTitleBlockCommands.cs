using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <summary>
    /// 从图框信息库界面编辑已有定义。只使用当前 DWG 中真实存在且当前可见状态匹配的块参照，
    /// 这样回显、重新框选和保存始终共用同一个块局部坐标基准。
    /// </summary>
    internal static bool EditTitleBlockFromLibrary(string blockName)
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            MessageBox.Show("当前没有可编辑的 CAD 图纸。", "图框信息库管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var editor = doc.Editor;
        if (!editor.CurrentUserCoordinateSystem.IsEqualTo(Matrix3d.Identity))
        {
            MessageBox.Show(
                "编辑图框前请先将 UCS 切换为世界坐标系（WCS）。\n命令行输入 UCS 然后回车即可。",
                "图框信息库管理",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        try
        {
            var library = TitleBlockLibraryStore.Load();
            var existing = library.Blocks.FirstOrDefault(x =>
                string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                MessageBox.Show("图框信息库中找不到该记录，请重新读取后再试。", "图框信息库管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (!TryFindEditableReference(doc.Database, existing.BlockName, out var match))
            {
                MessageBox.Show(
                    $"当前图中没有找到图框：{existing.BlockName}\n请打开包含该图框的 DWG，或切换动态块到对应可见状态后再编辑。",
                    "图框信息库管理",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return false;
            }

            // 目标可能在当前 DWG 的其他布局；先切换到其所属空间，再将该图框居中显示。
            if (!string.Equals(LayoutManager.Current.CurrentLayout, match.LayoutName, StringComparison.OrdinalIgnoreCase))
            {
                LayoutManager.Current.CurrentLayout = match.LayoutName;
                System.Windows.Forms.Application.DoEvents();
            }

            // 布局切换会重建/激活 CAD 子窗口，必须重新确认主窗口位于前台。
            CadWindowFocus.ActivateCadWindow();

            if (!editor.CurrentUserCoordinateSystem.IsEqualTo(Matrix3d.Identity))
            {
                MessageBox.Show(
                    $"图框位于“{match.LayoutName}”，该空间当前不是 WCS。\n请切换到 WCS 后再次点击编辑。",
                    "图框信息库管理",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            var mode = GetEditCoordinateMode(existing);
            var inverse = match.BlockTransform.Inverse();
            var referenceFrame = ResolveEditReferenceFrame(doc.Database, existing, match, mode, inverse);
            var worldFrame = TransformRegion(referenceFrame, match.BlockTransform);
            CenterEditorOnWorldExtents(editor, worldFrame);

            var initialState = new FieldBoxSelectInitialState
            {
                TitleRegion = ResolveEditFieldRegion(existing.TitleRegion, mode, referenceFrame, inverse),
                DrawingNumberRegion = ResolveEditFieldRegion(existing.DrawingNumberRegion, mode, referenceFrame, inverse),
                DateRegion = ResolveEditFieldRegion(existing.DateRegion, mode, referenceFrame, inverse),
                RevisionRegion = ResolveEditFieldRegion(existing.RevisionRegion, mode, referenceFrame, inverse),
                PhaseRegion = ResolveEditFieldRegion(existing.PhaseRegion, mode, referenceFrame, inverse),
                Info1Region = ResolveEditFieldRegion(existing.Info1Region, mode, referenceFrame, inverse),
                Info2Region = ResolveEditFieldRegion(existing.Info2Region, mode, referenceFrame, inverse),
                PaperName = existing.PaperName,
                PaperWidthMm = existing.PaperWidthMm,
                PaperHeightMm = existing.PaperHeightMm
            };

            var placedFrame = RectangleGeometry.TransformRectangle(referenceFrame, match.BlockTransform);
            var detectedWidth = placedFrame.ActualWidth > 0
                ? placedFrame.ActualWidth
                : worldFrame.MaxPoint.X - worldFrame.MinPoint.X;
            var detectedHeight = placedFrame.ActualHeight > 0
                ? placedFrame.ActualHeight
                : worldFrame.MaxPoint.Y - worldFrame.MinPoint.Y;
            var settings = AppSettingsStore.Load();
            var paperDetectionOptions = PaperSizeDetector.CreateRectangleBatchOptions(
                settings.PaperMatchToleranceMm,
                match.IsPaperSpace,
                settings.LongPaperSnapToleranceMm,
                settings.CustomScales);
            paperDetectionOptions.IncludeGenericDynamicTitleBlockPaper =
                mode == EditCoordinateMode.FrameRightBottomDynamic;
            if (mode == EditCoordinateMode.FrameRightBottomDynamic)
            {
                paperDetectionOptions.PreferredPaperBaseName = GetGenericDynamicPaperBaseName(existing.PaperName);
            }
            // 可拉伸模板的录入宽高不是固定纸张，编辑回显时也不能用它压过当前实例的实际外框。
            if (mode != EditCoordinateMode.FrameRightBottomDynamic)
            {
                paperDetectionOptions.PreferredPaperWidthMm = existing.PaperWidthMm;
                paperDetectionOptions.PreferredPaperHeightMm = existing.PaperHeightMm;
            }
            // 识别不到标准纸张时要求用户输入绘图比例，按 图面尺寸/比例 生成任意纸张，避免超大尺寸输出。
            var paperOptions = ArbitraryPaperPicker.DetectCandidatesOrPrompt(
                    detectedWidth,
                    detectedHeight,
                    paperDetectionOptions)
                .ToList();
            EnsureConfiguredPaperOption(paperOptions, existing, detectedWidth, detectedHeight);

            using var markers = new TransientFrameMarkers(editor);
            using var dialog = new FieldBoxSelectDialog(
                editor,
                inverse,
                match.BlockTransform,
                markers,
                referenceFrame,
                paperOptions,
                paperDetectionOptions,
                initialState);

            editor.WriteMessage($"\n已定位图框 {existing.BlockName}，红色临时框显示当前已配置字段，可点击对应‘框选’修改。");
            CadWindowFocus.ActivateCadWindow();
            if (ShowModalDialog(dialog) != DialogResult.OK)
            {
                return false;
            }

            referenceFrame = dialog.ReferenceFrame;
            var usesVariableLengthTemplate = IsGenericDynamicPaperName(dialog.PaperName);
            var now = DateTime.Now;
            var updated = new TitleBlockDefinition
            {
                BlockName = existing.BlockName,
                HasPrintRegion = true,
                CoordinateMode = usesVariableLengthTemplate
                    ? TitleBlockDefinition.DynamicRightBottomCoordinateMode
                    : "Frame",
                PrintRegion = referenceFrame,
                PaperName = dialog.PaperName,
                PaperWidthMm = dialog.PaperWidthMm,
                PaperHeightMm = dialog.PaperHeightMm,
                TitleRegion = ToStoredFrameRelative(dialog.TitleRegion, referenceFrame, usesVariableLengthTemplate),
                DrawingNumberRegion = ToStoredFrameRelative(dialog.DrawingNumberRegion, referenceFrame, usesVariableLengthTemplate),
                DateRegion = ToOptionalStoredFrameRelative(dialog.DateRegion, referenceFrame, usesVariableLengthTemplate),
                RevisionRegion = ToOptionalStoredFrameRelative(dialog.RevisionRegion, referenceFrame, usesVariableLengthTemplate),
                PhaseRegion = ToOptionalStoredFrameRelative(dialog.PhaseRegion, referenceFrame, usesVariableLengthTemplate),
                Info1Region = ToOptionalStoredFrameRelative(dialog.Info1Region, referenceFrame, usesVariableLengthTemplate),
                Info2Region = ToOptionalStoredFrameRelative(dialog.Info2Region, referenceFrame, usesVariableLengthTemplate),
                CreatedAt = existing.CreatedAt == default ? now : existing.CreatedAt,
                UpdatedAt = now
            };

            TitleBlockLibraryStore.Upsert(updated);
            MessageBox.Show(
                $"图框信息已更新：{updated.BlockName}\n纸张：{updated.PaperName} {updated.PaperWidthMm:0.##} × {updated.PaperHeightMm:0.##} mm",
                "图框信息库管理",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            AddBlockLog("Edit title block failed: " + ex);
            editor.WriteMessage("\n编辑图框失败: " + ex.Message);
            MessageBox.Show("编辑图框失败: " + ex.Message, "图框信息库管理", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static LocalRectangle ToOptionalStoredFrameRelative(
        LocalRectangle region,
        LocalRectangle referenceFrame,
        bool useRightBottomAnchor)
    {
        return region.HasArea()
            ? ToStoredFrameRelative(region, referenceFrame, useRightBottomAnchor)
            : new LocalRectangle();
    }

    private static LocalRectangle ToStoredFrameRelative(
        LocalRectangle region,
        LocalRectangle referenceFrame,
        bool useRightBottomAnchor)
    {
        return useRightBottomAnchor
            ? ToFrameRightBottomRelative(region, referenceFrame)
            : ToFrameRelative(region, referenceFrame);
    }

    private static EditCoordinateMode GetEditCoordinateMode(TitleBlockDefinition definition)
    {
        if (string.Equals(definition.CoordinateMode, "Frame", StringComparison.OrdinalIgnoreCase))
        {
            return EditCoordinateMode.Frame;
        }

        if (string.Equals(
                definition.CoordinateMode,
                TitleBlockDefinition.DynamicRightBottomCoordinateMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return EditCoordinateMode.FrameRightBottomDynamic;
        }

        return string.Equals(definition.CoordinateMode, "World", StringComparison.OrdinalIgnoreCase)
            ? EditCoordinateMode.World
            : EditCoordinateMode.Local;
    }

    private static LocalRectangle ResolveEditReferenceFrame(
        Database database,
        TitleBlockDefinition definition,
        EditableTitleBlockReference match,
        EditCoordinateMode mode,
        Matrix3d inverse)
    {
        if (mode == EditCoordinateMode.FrameRightBottomDynamic
            && BlockFrameGeometry.TryGetFrame(
                database,
                match.FrameDefinitionId,
                out var liveFrame,
                out _))
        {
            return liveFrame;
        }

        if (definition.HasPrintRegion && definition.PrintRegion.HasArea())
        {
            return mode == EditCoordinateMode.World
                ? TransformEditRegion(definition.PrintRegion, inverse)
                : definition.PrintRegion;
        }

        if (BlockFrameGeometry.TryGetFrame(
                database,
                match.FrameDefinitionId,
                out var detectedFrame,
                out _))
        {
            return detectedFrame;
        }

        if (TryGetBlockExtents(database, match.BlockReferenceId, out var blockExtents))
        {
            return TransformExtents(blockExtents, inverse);
        }

        throw new InvalidOperationException("无法取得该图框的有效打印范围。");
    }

    private static LocalRectangle ResolveEditFieldRegion(
        LocalRectangle storedRegion,
        EditCoordinateMode mode,
        LocalRectangle referenceFrame,
        Matrix3d inverse)
    {
        if (!storedRegion.HasArea())
        {
            return new LocalRectangle();
        }

        if (mode == EditCoordinateMode.Frame)
        {
            return LocalRectangle.FromPoints(
                storedRegion.MinX + referenceFrame.MinX,
                storedRegion.MinY + referenceFrame.MinY,
                storedRegion.MaxX + referenceFrame.MinX,
                storedRegion.MaxY + referenceFrame.MinY);
        }

        if (mode == EditCoordinateMode.FrameRightBottomDynamic)
        {
            return LocalRectangle.FromPoints(
                storedRegion.MinX + referenceFrame.MaxX,
                storedRegion.MinY + referenceFrame.MinY,
                storedRegion.MaxX + referenceFrame.MaxX,
                storedRegion.MaxY + referenceFrame.MinY);
        }

        return mode == EditCoordinateMode.World
            ? TransformEditRegion(storedRegion, inverse)
            : storedRegion;
    }

    private static LocalRectangle TransformEditRegion(LocalRectangle region, Matrix3d transform)
    {
        var extents = new Extents3d(
            new Point3d(region.MinX, region.MinY, 0),
            new Point3d(region.MaxX, region.MaxY, 0));
        return TransformExtents(extents, transform);
    }

    private static void EnsureConfiguredPaperOption(
        IList<PaperDetection> options,
        TitleBlockDefinition definition,
        double drawingWidth,
        double drawingHeight)
    {
        if (string.IsNullOrWhiteSpace(definition.PaperName)
            || definition.PaperWidthMm <= 0
            || definition.PaperHeightMm <= 0)
        {
            return;
        }

        var exists = options.Any(x =>
            string.Equals(x.PaperName, definition.PaperName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(x.PaperWidthMm - definition.PaperWidthMm) <= 0.01d
            && Math.Abs(x.PaperHeightMm - definition.PaperHeightMm) <= 0.01d);
        if (exists)
        {
            return;
        }

        var scaleX = drawingWidth / definition.PaperWidthMm;
        var scaleY = drawingHeight / definition.PaperHeightMm;
        var scale = scaleX > 0 && scaleY > 0 ? (scaleX + scaleY) / 2d : Math.Max(scaleX, scaleY);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1d;
        }

        // 历史库可能包含已不在当前自动候选中的自定义纸张；仍必须回显，不能静默改成第一个候选。
        options.Insert(0, new PaperDetection
        {
            PaperName = definition.PaperName,
            PaperWidthMm = definition.PaperWidthMm,
            PaperHeightMm = definition.PaperHeightMm,
            ScaleValue = scale,
            ScaleText = FormatEditScale(scale),
            IsLong = definition.PaperName.IndexOf('+') > 0,
            RequiresCustomPaper = string.Equals(definition.PaperName, PaperSizeDetector.CustomPaperName, StringComparison.OrdinalIgnoreCase),
            Note = "来自图框信息库的原配置"
        });
    }

    private static string FormatEditScale(double scale)
    {
        return scale >= 1d
            ? "1:" + scale.ToString("0.###", CultureInfo.InvariantCulture)
            : (1d / scale).ToString("0.###", CultureInfo.InvariantCulture) + ":1";
    }

    private static void CenterEditorOnWorldExtents(Editor editor, Extents3d worldExtents)
    {
        var dcsExtents = TransformWorldExtents(worldExtents, BuildWcsToDcsMatrix(editor));
        var width = Math.Max(dcsExtents.MaxPoint.X - dcsExtents.MinPoint.X, 1d) * 1.15d;
        var height = Math.Max(dcsExtents.MaxPoint.Y - dcsExtents.MinPoint.Y, 1d) * 1.15d;

        using var view = editor.GetCurrentView();
        var aspect = view.Height > 1e-9 && view.Width > 1e-9 ? view.Width / view.Height : 1.6d;
        if (width / height > aspect)
        {
            height = width / aspect;
        }
        else
        {
            width = height * aspect;
        }

        view.CenterPoint = new Point2d(
            (dcsExtents.MinPoint.X + dcsExtents.MaxPoint.X) / 2d,
            (dcsExtents.MinPoint.Y + dcsExtents.MaxPoint.Y) / 2d);
        view.Width = width;
        view.Height = height;
        editor.SetCurrentView(view);
        editor.Regen();
        editor.UpdateScreen();
    }

    private static bool TryFindEditableReference(
        Database database,
        string storedBlockName,
        out EditableTitleBlockReference match)
    {
        match = null!;
        using var tr = database.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
        var currentSpaceId = database.CurrentSpaceId;
        var owners = blockTable
            .Cast<ObjectId>()
            .Select(id => (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead))
            .Where(owner => owner.IsLayout)
            .OrderBy(owner => owner.ObjectId.Equals(currentSpaceId) ? 0 : 1)
            .ToList();

        foreach (var owner in owners)
        {
            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            foreach (ObjectId id in owner)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef
                    || !IsEntityVisible(blockRef))
                {
                    continue;
                }

                var outerName = CadTextExtractor.GetBlockName(blockRef, tr);
                Matrix3d nestedTransform;
                ObjectId frameDefinitionId;
                if (string.Equals(storedBlockName, outerName, StringComparison.OrdinalIgnoreCase))
                {
                    nestedTransform = Matrix3d.Identity;
                    frameDefinitionId = blockRef.BlockTableRecord;
                }
                else if (!TryFindNestedDefinition(
                             tr,
                             blockRef.BlockTableRecord,
                             outerName,
                             storedBlockName,
                             Matrix3d.Identity,
                             new HashSet<ObjectId>(),
                             0,
                             out nestedTransform,
                             out frameDefinitionId))
                {
                    continue;
                }

                match = new EditableTitleBlockReference
                {
                    BlockReferenceId = blockRef.ObjectId,
                    BlockTransform = nestedTransform * blockRef.BlockTransform,
                    FrameDefinitionId = frameDefinitionId,
                    LayoutName = layout.LayoutName,
                    IsPaperSpace = !layout.ModelType
                };
                return true;
            }
        }

        return false;
    }

    private static bool TryFindNestedDefinition(
        Transaction tr,
        ObjectId definitionId,
        string outerName,
        string storedBlockName,
        Matrix3d accumulatedTransform,
        ISet<ObjectId> visited,
        int depth,
        out Matrix3d nestedTransform,
        out ObjectId matchedDefinitionId)
    {
        nestedTransform = Matrix3d.Identity;
        matchedDefinitionId = ObjectId.Null;
        if (depth > 6 || definitionId.IsNull || !visited.Add(definitionId))
        {
            return false;
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference nested
                    || !IsEntityVisible(nested))
                {
                    continue;
                }

                var nestedName = CadTextExtractor.GetBlockName(nested, tr);
                var compoundMatch = depth == 0
                    && string.Equals(outerName + "+" + nestedName, storedBlockName, StringComparison.OrdinalIgnoreCase);
                var legacyInnerMatch = string.Equals(nestedName, storedBlockName, StringComparison.OrdinalIgnoreCase);
                var currentTransform = nested.BlockTransform * accumulatedTransform;
                if (compoundMatch || legacyInnerMatch)
                {
                    nestedTransform = currentTransform;
                    matchedDefinitionId = nested.BlockTableRecord;
                    return true;
                }

                if (TryFindNestedDefinition(
                        tr,
                        nested.BlockTableRecord,
                        outerName,
                        storedBlockName,
                        currentTransform,
                        visited,
                        depth + 1,
                        out nestedTransform,
                        out matchedDefinitionId))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            visited.Remove(definitionId);
        }
    }

    private sealed class EditableTitleBlockReference
    {
        public ObjectId BlockReferenceId { get; set; }
        public Matrix3d BlockTransform { get; set; } = Matrix3d.Identity;
        public ObjectId FrameDefinitionId { get; set; }
        public string LayoutName { get; set; } = "";
        public bool IsPaperSpace { get; set; }
    }

    private enum EditCoordinateMode
    {
        Local,
        World,
        Frame,
        FrameRightBottomDynamic
    }
}
