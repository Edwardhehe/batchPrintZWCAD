using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.DatabaseServices.Filters;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/**
 * 模型拆图（含 UCS）：按 WS.CAD.DwgSplitter 思路——
 * 收集与图框相交/落入的模型实体 → 克隆进新库匿名块 → 块参照上挂 SpatialFilter（XClip）→ SaveAs。
 * 不再整文件复制后删框外；布局拆图仍由 <see cref="DwgPaperSplitter"/> 负责。
 *
 * 说明：跨框实体会整段克隆，XClip 只裁显示，框外几何仍在块定义里（体积可能大于「删除框外」方案）。
 */
internal static class DwgModelSplitter
{
    private const string BlockNamePrefix = "SplitBlock_";

    /**
     * 从源库模型空间按图框窗口克隆实体到新 DWG（带 XClip）。
     * 源库可为当前未保存文档或侧载库；不改写源文件。
     */
    internal static void Split(
        Database sourceDatabase,
        string sourcePath,
        string outputPath,
        PlotJob job,
        DwgSplitService.SplitResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        // 多文件侧载常无 UCS 元数据；与出图路径一致，先按四角反推朝向再算保留区。
        CadSelectionWindow.EnsureModelFrameOrientation(job);

        var oldWorkingDatabase = HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase = sourceDatabase;

            List<ObjectId> keepIds;
            using (var sourceTr = sourceDatabase.TransactionManager.StartTransaction())
            {
                keepIds = CollectKeepEntityIds(sourceTr, sourceDatabase, job, result);
                sourceTr.Commit();
            }

            if (keepIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "拆图范围内未找到可保留对象，已停止生成空 DWG。请检查图框的 UCS/WCS 坐标。"
                    + (string.IsNullOrWhiteSpace(sourcePath) ? "" : " 源=" + sourcePath));
            }

            var clipCorners = DwgSplitGeometry.BuildKeepPolygon(job);
            if (clipCorners.Length < 3)
            {
                throw new InvalidOperationException("拆图窗口角点无效，无法建立 XClip 边界。");
            }

            using var newDb = new Database(true, true);
            BuildClippedBlockDatabase(sourceDatabase, newDb, keepIds, clipCorners);

            HostApplicationServices.WorkingDatabase = newDb;
            newDb.SaveAs(outputPath, DwgVersion.Current);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
        }
    }

    /// <summary>遍历模型空间，收集应保留的实体 Id（跳过临时红框/序号）。</summary>
    private static List<ObjectId> CollectKeepEntityIds(
        Transaction tr,
        Database sourceDatabase,
        PlotJob job,
        DwgSplitService.SplitResult result)
    {
        var blockTable = (BlockTable)tr.GetObject(sourceDatabase.BlockTableId, OpenMode.ForRead);
        var model = (BlockTableRecord)tr.GetObject(
            blockTable[BlockTableRecord.ModelSpace],
            OpenMode.ForRead);

        var keepIds = new List<ObjectId>();
        foreach (ObjectId id in model)
        {
            if (id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            if (DwgDatabaseCleanup.IsTemporaryOverlayEntity(entity))
            {
                result.RemovedEntities++;
                continue;
            }

            if (DwgSplitGeometry.ShouldKeepEntity(tr, entity, job, result))
            {
                keepIds.Add(id);
                result.KeptEntities++;
            }
            else
            {
                result.RemovedEntities++;
            }
        }

        return keepIds;
    }

    /**
     * 在空库中建块、WblockCloneObjects、插入块参照并写入 SpatialFilter（等价 XClip）。
     * 裁剪角点为 WCS；块原点与插入点取裁剪框第一角，与 WS 样例一致。
     */
    private static void BuildClippedBlockDatabase(
        Database sourceDatabase,
        Database newDatabase,
        IReadOnlyList<ObjectId> keepIds,
        Point3d[] clipCornersWcs)
    {
        var basePoint = clipCornersWcs[0];
        using var tr = newDatabase.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(newDatabase.BlockTableId, OpenMode.ForWrite);

        var blockName = BlockNamePrefix + Guid.NewGuid().ToString("N");
        var blockDefinition = new BlockTableRecord
        {
            Name = blockName,
            Origin = basePoint
        };
        var blockId = blockTable.Add(blockDefinition);
        tr.AddNewlyCreatedDBObject(blockDefinition, true);

        using (var idCollection = new ObjectIdCollection(keepIds.ToArray()))
        using (var mapping = new IdMapping())
        {
            sourceDatabase.WblockCloneObjects(
                idCollection,
                blockId,
                mapping,
                DuplicateRecordCloning.Replace,
                false);
        }

        var modelSpace = (BlockTableRecord)tr.GetObject(
            blockTable[BlockTableRecord.ModelSpace],
            OpenMode.ForWrite);
        var blockRef = new BlockReference(basePoint, blockId);
        modelSpace.AppendEntity(blockRef);
        tr.AddNewlyCreatedDBObject(blockRef, true);

        ApplySpatialClip(tr, blockRef, clipCornersWcs);
        tr.Commit();
    }

    /// <summary>给块参照写入 ACAD_FILTER / SPATIAL，效果等同 XClip 矩形/多边形裁剪。</summary>
    private static void ApplySpatialClip(
        Transaction tr,
        BlockReference blockRef,
        Point3d[] clipCornersWcs)
    {
        var points = new Point2dCollection();
        foreach (var corner in clipCornersWcs)
        {
            points.Add(new Point2d(corner.X, corner.Y));
        }

        var definition = new SpatialFilterDefinition(
            points,
            Vector3d.ZAxis,
            elevation: 0.0,
            frontClip: 1.0e+20,
            backClip: 1.0e+20,
            enabled: true);

        var filter = new SpatialFilter
        {
            Definition = definition
        };

        if (blockRef.ExtensionDictionary.IsNull)
        {
            blockRef.CreateExtensionDictionary();
        }

        var extensionDictionary = (DBDictionary)tr.GetObject(
            blockRef.ExtensionDictionary,
            OpenMode.ForWrite);

        DBDictionary filterDictionary;
        if (extensionDictionary.Contains("ACAD_FILTER"))
        {
            filterDictionary = (DBDictionary)tr.GetObject(
                extensionDictionary.GetAt("ACAD_FILTER"),
                OpenMode.ForWrite);
            if (filterDictionary.Contains("SPATIAL"))
            {
                filterDictionary.Remove("SPATIAL");
            }
        }
        else
        {
            filterDictionary = new DBDictionary();
            extensionDictionary.SetAt("ACAD_FILTER", filterDictionary);
            tr.AddNewlyCreatedDBObject(filterDictionary, true);
        }

        filterDictionary.SetAt("SPATIAL", filter);
        tr.AddNewlyCreatedDBObject(filter, true);
    }
}
