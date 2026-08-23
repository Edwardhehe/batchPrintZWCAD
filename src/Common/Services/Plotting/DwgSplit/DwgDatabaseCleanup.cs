using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.DatabaseServices.Filters;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>负责拆图数据库中的布局、图层、对象及冗余数据清理。</summary>
internal static class DwgDatabaseCleanup
{
    // TemporarySequenceOverlay 为了兼容多宿主会把红框和序号写入数据库；
    // 它们只服务界面预览，完整数据库快照拆图时必须主动排除。
    private const string TemporaryOverlayLayerName = "ZBP_TEMP_SEQUENCE_OVERLAY";

    /// <summary>
    /// 另存拆图必须用磁盘上的源 DWG，不能 Wblock 内存库。
    /// </summary>
    /// <param name="sourceDatabase">当前打开的源数据库。</param>
    /// <param name="sourcePath">调度层传入的身份路径。</param>
    /// <param name="job">拆图任务。</param>
    /// <returns>可复制的已保存 DWG 全路径。</returns>
    internal static string ResolveSavedSourcePath(Database sourceDatabase, string sourcePath, PlotJob job)
    {
        foreach (var candidate in new[] { sourcePath, sourceDatabase.Filename, job.SourceFile })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException("拆图请先保存当前图纸，再另存删除图框外对象。");
    }

    /// <summary>
    /// 尝试激活指定布局，使浮动视口 Number 等运行时属性可用。
    /// </summary>
    internal static void TryActivateLayout(string spaceName)
    {
        if (string.IsNullOrWhiteSpace(spaceName))
        {
            return;
        }

        try
        {
            LayoutManager.Current.CurrentLayout = spaceName;
        }
        catch
        {
            // 侧库激活失败时退回 Layout.GetViewports 识别根视口。
        }
    }

    internal static List<(ObjectId LayerId, bool WasLocked)> UnlockLayers(Transaction tr, Database db)
    {
        var states = new List<(ObjectId LayerId, bool WasLocked)>();
        var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId layerId in layerTable)
        {
            var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
            states.Add((layerId, layer.IsLocked));
            if (!layer.IsLocked)
            {
                continue;
            }

            layer.UpgradeOpen();
            layer.IsLocked = false;
        }

        return states;
    }

    internal static void RestoreLayerLocks(Transaction tr, IEnumerable<(ObjectId LayerId, bool WasLocked)> states)
    {
        foreach (var state in states)
        {
            if (!state.WasLocked || state.LayerId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(state.LayerId, OpenMode.ForWrite, false) is LayerTableRecord layer)
            {
                layer.IsLocked = true;
            }
        }
    }

    internal static BlockTableRecord? FindSpaceRecord(Transaction tr, BlockTable blockTable, string spaceName)
    {
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            if (string.Equals(owner.Name, spaceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout.LayoutName, spaceName, StringComparison.OrdinalIgnoreCase))
            {
                return owner;
            }
        }

        return null;
    }

    internal static bool IsTemporaryOverlayEntity(Entity entity)
    {
        try
        {
            return string.Equals(
                entity.Layer,
                TemporaryOverlayLayerName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void DeleteUnneededLayouts(Database db, PlotJob job)
    {
        var layoutNames = new List<(string LayoutName, string BlockRecordName, bool IsModel)>();
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var modelRecordId = blockTable[BlockTableRecord.ModelSpace];
            foreach (ObjectId recordId in blockTable)
            {
                var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                if (!owner.IsLayout)
                {
                    continue;
                }

                var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
                // ZWCAD 的 Wblock 侧库中 Layout.ModelType 可能错误地返回 false。
                // ObjectId 与模型空间标准记录名是更稳定的兜底，绝不能调用 DeleteLayout("Model")。
                var isModel = owner.ObjectId == modelRecordId
                    || layout.ModelType
                    || string.Equals(owner.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(layout.LayoutName, "Model", StringComparison.OrdinalIgnoreCase);
                layoutNames.Add((layout.LayoutName, owner.Name, isModel));
            }

            tr.Commit();
        }

        var manager = LayoutManager.Current;
        var keepLayout = job.IsPaperSpace
            ? layoutNames.FirstOrDefault(x =>
                string.Equals(x.BlockRecordName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.LayoutName, job.SpaceName, StringComparison.OrdinalIgnoreCase)).LayoutName
            : "Model";
        if (job.IsPaperSpace && string.IsNullOrWhiteSpace(keepLayout))
        {
            throw new InvalidOperationException(
                $"拆图时未找到目标布局“{job.SpaceName}”，已停止删除其他布局。");
        }

        if (!string.IsNullOrWhiteSpace(keepLayout))
        {
            try
            {
                manager.CurrentLayout = keepLayout;
            }
            catch
            {
                // 侧库可能拒绝切换布局；后续删除失败会统一报错。
            }
        }

        var failedLayouts = new List<string>();
        foreach (var layout in layoutNames)
        {
            if (layout.IsModel)
            {
                continue;
            }

            if (job.IsPaperSpace
                && (string.Equals(layout.BlockRecordName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(layout.LayoutName, job.SpaceName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                manager.DeleteLayout(layout.LayoutName);
            }
            catch
            {
                failedLayouts.Add(layout.LayoutName);
            }
        }

        if (failedLayouts.Count > 0 && job.IsPaperSpace)
        {
            throw new InvalidOperationException(
                "无法删除非目标布局: " + string.Join("、", failedLayouts));
        }

        // 模型拆图的交付内容是 Model。个别含代理数据的纸空间布局可能拒绝删除；
        // 此时保留该不可见布局比让已经正确清理的模型拆图整体失败更安全。
    }

    /// <summary>
    /// 保存前清理未引用的命名对象（等价于多次 PU），减小拆出 DWG 体积。
    /// 嵌套块/图层引用需多轮；任一轮失败不影响拆图主流程。
    /// </summary>
    internal static void PurgeUnusedNamedObjects(Database db)
    {
        try
        {
            for (var pass = 0; pass < 5; pass++)
            {
                var erased = 0;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    erased += PurgeSymbolTable(db, tr, db.BlockTableId);
                    erased += PurgeSymbolTable(db, tr, db.LayerTableId);
                    erased += PurgeSymbolTable(db, tr, db.LinetypeTableId);
                    erased += PurgeSymbolTable(db, tr, db.TextStyleTableId);
                    erased += PurgeSymbolTable(db, tr, db.DimStyleTableId);
                    erased += PurgeSymbolTable(db, tr, db.RegAppTableId);
                    erased += PurgeSymbolTable(db, tr, db.UcsTableId);
                    erased += PurgeNamedDictionary(db, tr, db.GroupDictionaryId);
                    erased += PurgeNamedDictionary(db, tr, db.MLStyleDictionaryId);
                    erased += PurgeNamedDictionary(db, tr, db.MLeaderStyleDictionaryId);
                    erased += PurgeNamedDictionary(db, tr, db.TableStyleDictionaryId);
                    erased += PurgeNamedDictionary(db, tr, db.MaterialDictionaryId);
                    erased += PurgeNamedDictionary(db, tr, db.PlotStyleNameDictionaryId);
                    tr.Commit();
                }

                if (erased == 0)
                {
                    break;
                }
            }
        }
        catch
        {
            // 清理只用于减小体积，不能因此让拆图失败。
        }
    }

    private static int PurgeSymbolTable(Database db, Transaction tr, ObjectId tableId)
    {
        if (tableId.IsNull)
        {
            return 0;
        }

        var table = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
        var ids = new ObjectIdCollection();
        foreach (ObjectId id in table)
        {
            ids.Add(id);
        }

        return ErasePurgeable(db, tr, ids);
    }

    private static int PurgeNamedDictionary(Database db, Transaction tr, ObjectId dictionaryId)
    {
        if (dictionaryId.IsNull)
        {
            return 0;
        }

        try
        {
            if (tr.GetObject(dictionaryId, OpenMode.ForRead, false) is not DBDictionary dictionary)
            {
                return 0;
            }

            var ids = new ObjectIdCollection();
            foreach (DBDictionaryEntry entry in dictionary)
            {
                ids.Add(entry.Value);
            }

            return ErasePurgeable(db, tr, ids);
        }
        catch
        {
            return 0;
        }
    }

    private static int ErasePurgeable(Database db, Transaction tr, ObjectIdCollection ids)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        db.Purge(ids);
        var erased = 0;
        foreach (ObjectId id in ids)
        {
            try
            {
                if (id.IsErased)
                {
                    continue;
                }

                var obj = tr.GetObject(id, OpenMode.ForWrite, false);
                if (obj == null || obj.IsErased)
                {
                    continue;
                }

                obj.Erase();
                erased++;
            }
            catch
            {
                // 跳过仍被引用或被单独锁定的记录。
            }
        }

        return erased;
    }

}
