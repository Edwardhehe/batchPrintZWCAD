using System;
using System.IO;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 模型拆图（含 UCS）：先把源 DWG 另存一份，再只删模型空间里图框外的对象。
/// 不切布局、不删布局，避免副本或当前图被改坏后 CAD 打不开。
/// </summary>
internal static class DwgModelSplitter
{
    /// <summary>
    /// 复制已保存的源 DWG，打开副本后按图框窗口删除框外模型实体。
    /// UCS 只用于去留判定，不改副本里的坐标、UCS、视图和布局。
    /// </summary>
    /// <param name="sourceDatabase">当前打开的源数据库，仅用于解析已保存路径。</param>
    /// <param name="sourcePath">调度层传入的身份路径。</param>
    /// <param name="outputPath">拆出 DWG 路径。</param>
    /// <param name="job">拆图任务。</param>
    /// <param name="result">去留统计。</param>
    internal static void Split(
        Database sourceDatabase,
        string sourcePath,
        string outputPath,
        PlotJob job,
        DwgSplitService.SplitResult result)
    {
        var copyFrom = DwgDatabaseCleanup.ResolveSavedSourcePath(sourceDatabase, sourcePath, job);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Copy(copyFrom, outputPath, overwrite: true);

        using var db = new Database(false, true);
        db.ReadDwgFile(outputPath, FileOpenMode.OpenForReadAndWriteNoShare, true, "");
        db.CloseInput(true);

        var oldWorkingDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var model = (BlockTableRecord)tr.GetObject(
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForWrite);
                var layerLocks = DwgDatabaseCleanup.UnlockLayers(tr, db);
                CleanModelSpaceByWindow(tr, model, job, result);
                DwgDatabaseCleanup.RestoreLayerLocks(tr, layerLocks);
                tr.Commit();
            }

            if (result.KeptEntities == 0)
            {
                throw new InvalidOperationException(
                    "拆图范围内未找到可保留对象，已停止生成空 DWG。请检查图框的 UCS/WCS 坐标。");
            }

            DwgDatabaseCleanup.PurgeUnusedNamedObjects(db);
            db.SaveAs(outputPath, DwgVersion.Current);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
        }
    }

    /// <summary>只删模型空间框外实体；UCS 只用于去留判定，不改实体坐标。</summary>
    private static void CleanModelSpaceByWindow(
        Transaction tr,
        BlockTableRecord model,
        PlotJob job,
        DwgSplitService.SplitResult result)
    {
        var ids = model.Cast<ObjectId>().ToList();
        foreach (var id in ids)
        {
            if (id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForWrite, false) is not Entity entity)
            {
                continue;
            }

            if (DwgDatabaseCleanup.IsTemporaryOverlayEntity(entity))
            {
                entity.Erase();
                result.RemovedEntities++;
                continue;
            }

            if (DwgSplitGeometry.ShouldKeepEntity(tr, entity, job, result))
            {
                result.KeptEntities++;
            }
            else
            {
                entity.Erase();
                result.RemovedEntities++;
            }
        }
    }
}
