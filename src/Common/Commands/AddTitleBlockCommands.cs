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
            bool isStretchableBlock;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockRef = (BlockReference)tr.GetObject(blockResult.ObjectId, OpenMode.ForRead);
                blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                blockTransform = blockRef.BlockTransform;
                frameDefinitionId = blockRef.BlockTableRecord;
                // “可拉伸”与“可见性切换基础图幅”是两个独立特性。这里检查距离拉伸和
                // 查寻列表（加长列表）定长拉伸；A1/A2/A3 等可见状态仍由下面的复合块名分别入库和匹配。
                var hasLookupStretch = HasLookupStretchProperty(blockRef);
                isStretchableBlock = HasStretchDistanceProperty(blockRef) || hasLookupStretch;
                var hasPaperVisibilityStates = HasVisibilityStateProperty(blockRef);
                // 求值定义里被隐藏的内层块参照是“可见性切换内层图框”的直接证据，
                // 与属性名是否叫“可见”无关，自定义属性名的可见性块也能识别。
                var hasNestedVisibilityStates = HasHiddenNestedBlockReference(tr, blockRef);

                // 纸张候选顺序必须与矩形框批打一致：模型空间优先 1:100、再 1:1；
                // 布局空间优先 1:1、再 1:100。以所选块实际所属布局判断，不能只看当前 TileMode。
                var owner = (BlockTableRecord)tr.GetObject(blockRef.OwnerId, OpenMode.ForRead);
                isPaperSpace = owner.IsLayout
                    && !owner.LayoutId.IsNull
                    && !((Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead)).ModelType;

                // 动态块通过可见性状态切换不同尺寸
                // 入库块名使用“外层块名+内层可见嵌套块名”复合名，变换矩阵取内层嵌套块的
                // 查寻列表拉伸块的外框在外层自身求值定义里，进入内层复合身份会丢掉加长后的实际长度；
                // 但求值定义里存在被隐藏的内层块参照时，说明是可见性状态在切换内层图框，
                // 不同可见内层块本质是不同图框，必须按“外层+可见内层”复合名各自独立入库。
                if ((hasNestedVisibilityStates || !hasLookupStretch)
                    && hasPaperVisibilityStates
                    && TryGetVisibleNestedBlock(
                        tr,
                        blockRef,
                        out var innerName,
                        out var innerTransform,
                        out var innerDefinitionId,
                        out var isInnerStretchable))
                {
                    blockName = blockName + "+" + innerName;
                    blockTransform = innerTransform * blockRef.BlockTransform;
                    frameDefinitionId = innerDefinitionId;
                    // 组合情况：外层负责 A1/A2/A3 可见性，当前可见内层块本身还可自由拉长。
                    isStretchableBlock |= isInnerStretchable;
                    AddBlockLog($"Paper visibility state detected: outer={CadTextExtractor.GetBlockName(blockRef, tr)}, inner={innerName}, stored={blockName}");
                }
                else if (isStretchableBlock)
                {
                    // 外层自由拉伸、内层图签只随右边界移动时，打印外框必须来自外层当前求值定义。
                    AddBlockLog($"Stretch-only block detected: block={blockName}, frameDefinition=outer evaluated definition");
                }

                tr.Commit();
            }

            AddBlockLog("Selected block: " + blockName);

            // 动态块的可见性状态可能形成“外层块名+内层块名”的复合身份，
            // 必须等最终身份解析完成后立即检查重名；若用户不覆盖，就不要继续识别外框和录入字段。
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
            if (BlockFrameGeometry.TryGetFrame(
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
                settings.LongPaperSnapToleranceMm,
                settings.CustomScales);
            // 只有可自由拉长的块才按 A1+ 入库；仅在 A1/A2/A3 间切换的可见性块仍保存固定纸张。
            paperDetectionOptions.IncludeGenericDynamicTitleBlockPaper = isStretchableBlock;
            // 识别不到标准纸张时要求用户输入绘图比例，按 图面尺寸/比例 生成任意纸张，避免超大尺寸输出。
            var paperOptions = ArbitraryPaperPicker.DetectCandidatesOrPrompt(
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

            var usesVariableLengthTemplate = isStretchableBlock;

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
                CoordinateMode = usesVariableLengthTemplate
                    ? TitleBlockDefinition.DynamicRightBottomCoordinateMode
                    : "Frame",
                PrintRegion = referenceFrame,
                PaperName = paperName,
                PaperWidthMm = paperWidthMm,
                PaperHeightMm = paperHeightMm,
                TitleRegion = usesVariableLengthTemplate ? ToFrameRightBottomRelative(titleRegion, referenceFrame) : ToFrameRelative(titleRegion, referenceFrame),
                DrawingNumberRegion = usesVariableLengthTemplate ? ToFrameRightBottomRelative(numberRegion, referenceFrame) : ToFrameRelative(numberRegion, referenceFrame),
                DateRegion = dateRegion.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(dateRegion, referenceFrame) : ToFrameRelative(dateRegion, referenceFrame)) : new LocalRectangle(),
                RevisionRegion = revisionRegion.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(revisionRegion, referenceFrame) : ToFrameRelative(revisionRegion, referenceFrame)) : new LocalRectangle(),
                PhaseRegion = phaseRegion.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(phaseRegion, referenceFrame) : ToFrameRelative(phaseRegion, referenceFrame)) : new LocalRectangle(),
                // 信息1/信息2是用户自定义可选字段，未框选时保持空区域，后续命名会自动跳过空值。
                Info1Region = info1Region.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(info1Region, referenceFrame) : ToFrameRelative(info1Region, referenceFrame)) : new LocalRectangle(),
                Info2Region = info2Region.HasArea() ? (usesVariableLengthTemplate ? ToFrameRightBottomRelative(info2Region, referenceFrame) : ToFrameRelative(info2Region, referenceFrame)) : new LocalRectangle(),
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
            editor.WriteMessage(usesVariableLengthTemplate
                ? $"\n可拉伸基础图幅: {definition.PaperName}（实际纸张在扫描块实例时判断）"
                : $"\n固定输出纸张: {definition.PaperName} {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} mm");
            editor.WriteMessage(hasPrintRegion
                ? "\n已保存图框打印边界。"
                : "\n未保存打印边界，打印时使用块外包框。");
            editor.WriteMessage($"\n图框库: {TitleBlockLibraryStore.DefaultPath}");

            var savedPaperText = usesVariableLengthTemplate
                ? $"{definition.PaperName}（实际纸张在扫描时判断）"
                : $"{definition.PaperName} {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} mm";
            MessageBox.Show(
                $"图框已保存: {blockName}\n纸张: {savedPaperText}",
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
        out ObjectId innerDefinitionId,
        out bool isInnerStretchable)
    {
        innerBlockName = "";
        innerTransform = Matrix3d.Identity;
        innerDefinitionId = ObjectId.Null;
        isInnerStretchable = false;

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
        var nestedBlocks = new List<(string Name, Matrix3d Transform, ObjectId DefinitionId, bool IsStretchable, double FrameArea)>();

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
                var frameArea = 0d;
                if (BlockFrameGeometry.TryGetFrame(
                        tr,
                        nested.BlockTableRecord,
                        out var nestedFrame,
                        out _))
                {
                    frameArea = RectangleGeometry.GetActualArea(
                        RectangleGeometry.TransformRectangle(nestedFrame, nested.BlockTransform));
                }

                nestedBlocks.Add((
                    nestedName,
                    nested.BlockTransform,
                    nested.BlockTableRecord,
                    HasStretchDistanceProperty(nested) || HasLookupStretchProperty(nested),
                    frameArea));
            }
        }

        if (nestedBlocks.Count == 0)
        {
            return false;
        }

        // 可见状态里可能同时存在图框、图签、徽标等多个嵌套块，必须按真实外框面积选图框；
        // 不能只比较 BlockTransform 的缩放值，否则多个 1:1 嵌套块会误选第一个小块。
        var selected = nestedBlocks
            .OrderByDescending(block => block.FrameArea)
            .ThenByDescending(block => Math.Abs(block.Transform[0, 0] * block.Transform[1, 1]))
            .First();

        innerBlockName = selected.Name;
        innerTransform = selected.Transform;
        innerDefinitionId = selected.DefinitionId;
        isInnerStretchable = selected.IsStretchable;
        return true;
    }

    /// <summary>
    /// 判断动态块当前求值定义里是否存在被隐藏的内层块参照。存在即说明该块通过可见性状态
    /// 在内层图框之间切换：不同可见内层块本质是不同图框，必须走“外层+可见内层”复合名
    /// 分别入库；查寻列表（加长）拉伸块没有这种隐藏内层块，不能因此混入复合身份。
    /// </summary>
    private static bool HasHiddenNestedBlockReference(Transaction tr, BlockReference blockRef)
    {
        try
        {
            if (!blockRef.IsDynamicBlock || blockRef.BlockTableRecord.IsNull)
            {
                return false;
            }

            var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is BlockReference nested
                    && !IsEntityVisible(nested))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 读取失败时不改变原有判定路径。
        }

        return false;
    }

    /// <summary>
    /// 判断块参照本身是否带可写的距离参数。距离参数代表自由拉伸能力；可见性参数只负责
    /// A1/A2/A3 等基础图幅切换，绝不能在这里等同为拉伸。
    /// </summary>
    private static bool HasStretchDistanceProperty(BlockReference blockRef)
    {
        try
        {
            if (!blockRef.IsDynamicBlock)
            {
                return false;
            }

            foreach (DynamicBlockReferenceProperty property in blockRef.DynamicBlockReferencePropertyCollection)
            {
                if (property.ReadOnly)
                {
                    continue;
                }

                if (property.UnitsType == DynamicBlockReferencePropertyUnitsType.Distance)
                {
                    return true;
                }
            }
        }
        catch
        {
            // 老版本宿主无法读取动态属性时保持固定坐标模式，不能误改普通动态块的锚点。
        }

        return false;
    }

    /// <summary>
    /// 判断块参照是否带查寻（lookup）类自定义列表属性。这类属性在属性面板里表现为
    /// “自定义”分组下的下拉列表，常用于“定长拉伸”图框（加长列表），每档对应一个固定
    /// 加长长度。它们没有距离参数，但必须按可拉伸模板入库，扫描时才能从每个参照各自的
    /// 求值定义取到加长后的实际外框。显式命名为“可见/Visibility”的字符串列表属于
    /// 图幅可见性切换，不算查寻拉伸。
    /// </summary>
    private static bool HasLookupStretchProperty(BlockReference blockRef)
    {
        try
        {
            if (!blockRef.IsDynamicBlock)
            {
                return false;
            }

            foreach (DynamicBlockReferenceProperty property in blockRef.DynamicBlockReferencePropertyCollection)
            {
                if (property.ReadOnly)
                {
                    continue;
                }

                if (property.UnitsType != DynamicBlockReferencePropertyUnitsType.NoUnits
                    || property.Value is not string)
                {
                    continue;
                }

                var propertyName = property.PropertyName ?? "";
                if (propertyName.IndexOf("可见", StringComparison.OrdinalIgnoreCase) >= 0
                    || propertyName.IndexOf("visibility", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                var allowedValues = property.GetAllowedValues();
                if (allowedValues != null && allowedValues.Length > 1)
                {
                    return true;
                }
            }
        }
        catch
        {
            // 老版本宿主无法读取动态属性时保持固定坐标模式，不能误改普通动态块的锚点。
        }

        return false;
    }

    /// <summary>
    /// 判断外层动态块是否通过状态切换选择 A1/A2/A3 等内部图幅。只有这种情况才进入当前
    /// 可见内层块建立复合身份；单纯存在一个随拉伸移动的内层图签不能作为进入内层的依据。
    /// </summary>
    private static bool HasVisibilityStateProperty(BlockReference blockRef)
    {
        try
        {
            if (!blockRef.IsDynamicBlock)
            {
                return false;
            }

            foreach (DynamicBlockReferenceProperty property in blockRef.DynamicBlockReferencePropertyCollection)
            {
                if (property.ReadOnly)
                {
                    continue;
                }

                var allowedValues = property.GetAllowedValues();
                if (allowedValues == null || allowedValues.Length <= 1)
                {
                    continue;
                }

                var propertyName = property.PropertyName ?? "";
                var explicitlyNamedVisibility =
                    propertyName.IndexOf("可见", StringComparison.OrdinalIgnoreCase) >= 0
                    || propertyName.IndexOf("visibility", StringComparison.OrdinalIgnoreCase) >= 0;
                var stringStateSelector =
                    property.UnitsType == DynamicBlockReferencePropertyUnitsType.NoUnits
                    && property.Value is string;
                if (explicitlyNamedVisibility || stringStateSelector)
                {
                    return true;
                }
            }
        }
        catch
        {
            // 无法确认可见性状态时留在外层，避免把固定宽度的内层图签误当成打印外框。
        }

        return false;
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
