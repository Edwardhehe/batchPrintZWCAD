using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
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

/// <summary>
/// 签章相关的命令行入口：导入签名图、给图框配置签章槽位、查看现状。
/// <para>
/// 先给命令行闭环再补界面：签章是低频配置动作（一个人一辈子配一两次），
/// 命令行的录入成本可接受，而界面缺位时功能仍能完整用起来。
/// </para>
/// </summary>
public sealed partial class BatchPlotCommands
{
    /// <summary>
    /// 导入签名/印章图片：
    /// ZBP_SIGN_IMPORT → 选图片 → 输入名称 → 选类型 → 是否去白底。
    /// </summary>
    [CommandMethod("ZBP_SIGN_IMPORT")]
    public void ImportSignature()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var editor = doc.Editor;
        try
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "选择签名 / 印章图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                editor.WriteMessage("\n已取消导入。");
                return;
            }

            var sourcePath = dialog.FileName;

            var nameOptions = new PromptStringOptions("\n输入显示名称")
            {
                DefaultValue = Path.GetFileNameWithoutExtension(sourcePath),
                AllowSpaces = true,
                UseDefaultValue = true,
            };
            var nameResult = editor.GetString(nameOptions);
            if (nameResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n已取消导入。");
                return;
            }

            var kindOptions = new PromptKeywordOptions("\n选择类型");
            kindOptions.Keywords.Add("Signature", "S", "签名");
            kindOptions.Keywords.Add("Stamp", "T", "印章");
            kindOptions.Keywords.Default = "Signature";
            kindOptions.AllowNone = false;
            var kindResult = editor.GetKeywords(kindOptions);
            if (kindResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n已取消导入。");
                return;
            }

            var kind = string.Equals(kindResult.StringResult, "Stamp", StringComparison.OrdinalIgnoreCase)
                ? "Stamp"
                : "Signature";

            var backgroundOptions = new PromptKeywordOptions("\n是否自动去白底（扫描件建议去）");
            backgroundOptions.Keywords.Add("Yes", "Y", "是");
            backgroundOptions.Keywords.Add("No", "N", "否");
            backgroundOptions.Keywords.Default = "Yes";
            backgroundOptions.AllowNone = false;
            var backgroundResult = editor.GetKeywords(backgroundOptions);
            var removeBackground = backgroundResult.Status != PromptStatus.OK
                || !string.Equals(backgroundResult.StringResult, "No", StringComparison.OrdinalIgnoreCase);

            var item = SignatureLibraryStore.ImportImage(
                sourcePath,
                kind,
                nameResult.StringResult,
                removeBackground);

            var library = SignatureLibraryStore.Load();
            library.Items.Add(item);
            SignatureLibraryStore.Save(library);

            editor.WriteMessage(
                $"\n已导入：{item.Name}（{(kind == "Stamp" ? "印章" : "签名")}）"
                + $"\n图片：{SignatureLibraryStore.LocateImageFile(item)}"
                + $"\n目录：{SignatureLibraryStore.GetDirectory(kind)}"
                + $"\nId：{item.Id}（配置槽位时用得上）");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\n导入失败：" + ex.Message);
            PluginLog.Error("签章", "导入签名图片失败：" + ex.Message, ex);
        }
    }

    /// <summary>
    /// 给当前图纸里的某个图框配置签章槽位：
    /// ZBP_SIGN_SLOT → 选图框块参照 → 依次框选槽位 → 绑定签名 → 存库。
    /// <para>
    /// 录入时一次算出两份坐标：相对纸张右下角的毫米偏移（PDF 盖章用）与相对图框的 DWG 坐标
    /// （后续 DWG 内嵌与人工核对用）。不运行时互算——打印窗口会被留白/居中/学习修正反复改写，
    /// 从 DWG 坐标反推纸面偏移不可靠。
    /// </para>
    /// </summary>
    [CommandMethod("ZBP_SIGN_SLOT")]
    public void ConfigureSignatureSlots()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var editor = doc.Editor;

        if (!editor.CurrentUserCoordinateSystem.IsEqualTo(Matrix3d.Identity))
        {
            MessageBox.Show(
                "配置签章槽位前请先把 UCS 切换为世界坐标系（WCS）。\n命令行输入 UCS 回车即可。",
                "批量打印",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var library = SignatureLibraryStore.Load();
        if (library.Items.Count == 0)
        {
            MessageBox.Show(
                "签名库还是空的。\n请先运行 ZBP_SIGN_IMPORT 导入签名或印章图片。",
                "批量打印",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var entityOptions = new PromptEntityOptions("\n选择要配置签章槽位的图框块参照");
            entityOptions.SetRejectMessage("\n必须选择一个块参照。");
            entityOptions.AddAllowedClass(typeof(BlockReference), true);
            var entityResult = editor.GetEntity(entityOptions);
            if (entityResult.Status != PromptStatus.OK)
            {
                return;
            }

            using var transaction = doc.Database.TransactionManager.StartTransaction();
            var blockRef = transaction.GetObject(entityResult.ObjectId, OpenMode.ForRead) as BlockReference;
            if (blockRef == null)
            {
                editor.WriteMessage("\n所选对象不是块参照。");
                return;
            }

            var blockName = GetDefinitionBlockName(transaction, blockRef);
            var frameLibrary = TitleBlockLibraryStore.Load();
            var definition = frameLibrary.Blocks.FirstOrDefault(
                x => string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));

            if (definition == null)
            {
                editor.WriteMessage(
                    $"\n图框「{blockName}」还没有入库，无法确定纸张尺寸。"
                    + "\n请先执行 ZBP_ADD_TITLE_BLOCK 把该图框加入图框库。");
                return;
            }

            if (definition.PaperWidthMm <= 0 || definition.PaperHeightMm <= 0)
            {
                editor.WriteMessage(
                    $"\n图框「{blockName}」未记录纸张尺寸，无法换算毫米偏移。"
                    + "\n请在「图框信息库」里补齐图幅后重试。");
                return;
            }

            var extents = blockRef.GeometricExtents;
            var frameWidthUnits = extents.MaxPoint.X - extents.MinPoint.X;
            var frameHeightUnits = extents.MaxPoint.Y - extents.MinPoint.Y;
            if (frameWidthUnits <= 1e-6 || frameHeightUnits <= 1e-6)
            {
                editor.WriteMessage("\n图框块参照的包围盒无效，无法配置。");
                return;
            }

            var scaleX = definition.PaperWidthMm / frameWidthUnits;
            var scaleY = definition.PaperHeightMm / frameHeightUnits;

            editor.WriteMessage(
                $"\n图框：{blockName}  纸张 {definition.PaperWidthMm:F0}×{definition.PaperHeightMm:F0}mm"
                + $"  图面 {frameWidthUnits:F1}×{frameHeightUnits:F1}"
                + $"  换算 {scaleX:F4} mm/单位");

            if (Math.Abs(blockRef.Rotation) > 1e-6)
            {
                editor.WriteMessage(
                    $"\n注意：该块参照旋转了 {blockRef.Rotation * 180 / Math.PI:F2}°，"
                    + "包围盒会偏大，算出的偏移可能不准；建议在未旋转的图框上配置。");
            }

            var slots = new List<SignatureSlot>();
            var continueAdding = true;
            while (continueAdding)
            {
                var slotNameOptions = new PromptStringOptions("\n槽位名称（如 设计 / 审核 / 出图章）")
                {
                    DefaultValue = slots.Count == 0 ? "设计" : "",
                    AllowSpaces = false,
                    UseDefaultValue = slots.Count == 0,
                };
                var slotNameResult = editor.GetString(slotNameOptions);
                if (slotNameResult.Status != PromptStatus.OK
                    || string.IsNullOrWhiteSpace(slotNameResult.StringResult))
                {
                    break;
                }

                var slotName = slotNameResult.StringResult.Trim();

                var firstOptions = new PromptPointOptions($"\n槽位「{slotName}」第一个角点");
                var firstResult = editor.GetPoint(firstOptions);
                if (firstResult.Status != PromptStatus.OK)
                {
                    break;
                }

                var secondOptions = new PromptCornerOptions(
                    $"\n槽位「{slotName}」对角点",
                    firstResult.Value);
                var secondResult = editor.GetCorner(secondOptions);
                if (secondResult.Status != PromptStatus.OK)
                {
                    break;
                }

                var minX = Math.Min(firstResult.Value.X, secondResult.Value.X);
                var maxX = Math.Max(firstResult.Value.X, secondResult.Value.X);
                var minY = Math.Min(firstResult.Value.Y, secondResult.Value.Y);
                var maxY = Math.Max(firstResult.Value.Y, secondResult.Value.Y);

                var signatureId = PromptSignature(editor, library);
                if (signatureId == null)
                {
                    break;
                }

                // 相对外框右下角的 DWG 坐标，与 FrameRightBottomDynamic 同语义，便于核对与后续 DWG 内嵌。
                var cadRegion = LocalRectangle.FromPoints(
                    minX - extents.MaxPoint.X,
                    minY - extents.MinPoint.Y,
                    maxX - extents.MaxPoint.X,
                    maxY - extents.MinPoint.Y);

                var slot = new SignatureSlot
                {
                    SlotName = slotName,
                    RightInsetMm = (extents.MaxPoint.X - maxX) * scaleX,
                    BottomInsetMm = (minY - extents.MinPoint.Y) * scaleY,
                    WidthMm = (maxX - minX) * scaleX,
                    HeightMm = (maxY - minY) * scaleY,
                    DefaultSignatureId = signatureId,
                    CadRegion = cadRegion,
                };

                // 新格式：位置存在 Regions 里（支持一个槽位多处）。命令行只录一处，但结构要与界面一致，
                // 否则后续在界面里追加位置时会被当成「首次框选」把这一处替换掉。
                slot.Regions.Add(new SignatureSlotRegion
                {
                    CadRegion = cadRegion,
                    RightInsetMm = slot.RightInsetMm,
                    BottomInsetMm = slot.BottomInsetMm,
                    WidthMm = slot.WidthMm,
                    HeightMm = slot.HeightMm,
                });

                slots.Add(slot);
                editor.WriteMessage(
                    $"\n已记录「{slot.SlotName}」：距右 {slot.RightInsetMm:F1}mm、距下 {slot.BottomInsetMm:F1}mm、"
                    + $"尺寸 {slot.WidthMm:F1}×{slot.HeightMm:F1}mm");

                var moreOptions = new PromptKeywordOptions("\n继续添加槽位?");
                moreOptions.Keywords.Add("Yes", "Y", "是");
                moreOptions.Keywords.Add("No", "N", "否");
                moreOptions.Keywords.Default = "Yes";
                moreOptions.AllowNone = true;
                var moreResult = editor.GetKeywords(moreOptions);
                continueAdding = moreResult.Status == PromptStatus.OK
                    && !string.Equals(moreResult.StringResult, "No", StringComparison.OrdinalIgnoreCase);
            }

            transaction.Commit();

            if (slots.Count == 0)
            {
                editor.WriteMessage("\n未配置任何槽位，图框库未改动。");
                return;
            }

            // 同名槽位覆盖，新槽位追加：允许反复微调同一个槽位而不产生重复项。
            foreach (var slot in slots)
            {
                var existing = definition.SignatureSlots.FirstOrDefault(
                    x => string.Equals(x.SlotName, slot.SlotName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    definition.SignatureSlots.Remove(existing);
                }

                definition.SignatureSlots.Add(slot);
            }

            TitleBlockLibraryStore.Upsert(definition);
            editor.WriteMessage($"\n已保存 {slots.Count} 个签章槽位到图框库（块名 {blockName}）。");
        }
        catch (System.Exception ex)
        {
            editor.WriteMessage("\n配置签章槽位失败：" + ex.Message);
            PluginLog.Error("签章", "配置签章槽位失败：" + ex.Message, ex);
        }
    }

    /// <summary>
    /// 签名库管理窗口（菜单「其他设置 → 签名库管理」）。
    /// 命令行入口 ZBP_SIGNATURE_LIBRARY，与导入/替换/改有效期都在同一个窗口里完成。
    /// </summary>
    [CommandMethod("ZBP_SIGNATURE_LIBRARY", CommandFlags.Session)]
    public void ManageSignatureLibrary() => SignatureLibraryManagerForm.ShowManager();

    [CommandMethod("_ZBP_INTERNAL_SIGNATURE_LIBRARY", CommandFlags.Session)]
    public void ManageSignatureLibraryLegacy() => ManageSignatureLibrary();

    /// <summary>印章库管理窗口（菜单「其他设置 → 印章库管理」）。界面按 MSteel 印章库复刻。</summary>
    [CommandMethod("ZBP_STAMP_LIBRARY", CommandFlags.Session)]
    public void ManageStampLibrary() => StampLibraryForm.ShowManager();

    [CommandMethod("_ZBP_INTERNAL_STAMP_LIBRARY", CommandFlags.Session)]
    public void ManageStampLibraryLegacy() => ManageStampLibrary();

    /// <summary>命令行列出签名库与各图框的槽位配置，便于排查「为什么没盖章」。</summary>
    [CommandMethod("ZBP_SIGN_LIST")]
    public void ListSignatures()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var editor = doc.Editor;
        var library = SignatureLibraryStore.Load();

        editor.WriteMessage($"\n签名库：{SignatureLibraryStore.DefaultPath}");
        if (library.Items.Count == 0)
        {
            editor.WriteMessage("\n（空，请先运行 ZBP_SIGN_IMPORT）");
        }
        else
        {
            foreach (var item in library.Items)
            {
                var path = SignatureLibraryStore.ResolveImagePath(item);
                var valid = item.IsValidOn(DateTime.Now) ? "" : "  [已过期]";
                var missing = string.IsNullOrEmpty(path) ? "  [图片缺失]" : "";
                editor.WriteMessage(
                    $"\n  {item.Id}  {item.Name}  ({item.Kind}){valid}{missing}");
            }
        }

        var frameLibrary = TitleBlockLibraryStore.Load();
        editor.WriteMessage("\n图框签章槽位：");
        var any = false;
        foreach (var block in frameLibrary.Blocks)
        {
            if (block.SignatureSlots == null || block.SignatureSlots.Count == 0)
            {
                continue;
            }

            any = true;
            editor.WriteMessage($"\n  {block.BlockName}（{block.PaperWidthMm:F0}×{block.PaperHeightMm:F0}mm）");
            foreach (var slot in block.SignatureSlots)
            {
                var bound = library.Find(slot.DefaultSignatureId);
                var binding = slot.AutoPickSignature
                    ? "自动读取"
                    : (bound == null ? "未绑定" : bound.Name);
                var regions = slot.GetRegions();
                editor.WriteMessage($"\n    槽位「{slot.SlotName}」{regions.Count} 处 → {binding}");
                for (var index = 0; index < regions.Count; index++)
                {
                    var region = regions[index];
                    editor.WriteMessage(
                        $"\n      {index + 1}. 距右 {region.RightInsetMm:F1} 距下 {region.BottomInsetMm:F1} "
                        + $"{region.WidthMm:F1}×{region.HeightMm:F1}mm");
                }
            }
        }

        if (!any)
        {
            editor.WriteMessage("\n（还没有图框配置槽位，请运行 ZBP_SIGN_SLOT）");
        }
    }

    /// <summary>取块定义名：动态块要取 DynamicBlockTableRecord，否则拿到的是匿名名 *U12 之类。</summary>
    private static string GetDefinitionBlockName(Transaction transaction, BlockReference blockRef)
    {
        var recordId = blockRef.IsDynamicBlock && !blockRef.DynamicBlockTableRecord.IsNull
            ? blockRef.DynamicBlockTableRecord
            : blockRef.BlockTableRecord;

        var record = transaction.GetObject(recordId, OpenMode.ForRead) as BlockTableRecord;
        return record?.Name ?? "";
    }

    /// <summary>让用户从签名库里挑一个；返回 null 表示取消。</summary>
    private static string? PromptSignature(Editor editor, SignatureLibrary library)
    {
        editor.WriteMessage("\n可绑定的签名：");
        for (var index = 0; index < library.Items.Count; index++)
        {
            var item = library.Items[index];
            editor.WriteMessage(
                $"\n  {index + 1}. {item.Name}（{(item.Kind == "Stamp" ? "印章" : "签名")}）");
        }

        var options = new PromptIntegerOptions("\n输入要绑定的签名序号（回车=不绑定）")
        {
            LowerLimit = 1,
            UpperLimit = library.Items.Count,
            AllowNone = true,
        };

        var result = editor.GetInteger(options);
        if (result.Status == PromptStatus.None)
        {
            return "";
        }

        if (result.Status != PromptStatus.OK)
        {
            return null;
        }

        return library.Items[result.Value - 1].Id;
    }
}
