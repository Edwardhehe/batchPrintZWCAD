using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 布局拆图：先把源 DWG 另存一份，再只删当前图框外的纸面对象和其他布局。
/// 不经 Wblock 整库克隆，避免视口 On/视图被改坏。
/// </summary>
internal static class DwgPaperSplitter
{
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
                var layerLocks = DwgDatabaseCleanup.UnlockLayers(tr, db);
                CleanPaperSpace(tr, blockTable, job, result);
                DwgDatabaseCleanup.RestoreLayerLocks(tr, layerLocks);
                tr.Commit();
            }

            DwgDatabaseCleanup.DeleteUnneededLayouts(db, job);
            DwgDatabaseCleanup.PurgeUnusedNamedObjects(db);
            db.SaveAs(outputPath, DwgVersion.Current);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
        }
    }

    private static void CleanPaperSpace(Transaction tr, BlockTable blockTable, PlotJob job, DwgSplitService.SplitResult result)
    {
        var target = DwgDatabaseCleanup.FindSpaceRecord(tr, blockTable, job.SpaceName);
        if (target == null)
        {
            throw new InvalidOperationException("未找到布局空间: " + job.SpaceName);
        }

        target.UpgradeOpen();
        CleanOwnerByWindow(tr, target, job, result);
    }

    private static void CleanOwnerByWindow(Transaction tr, BlockTableRecord owner, PlotJob job, DwgSplitService.SplitResult result)
    {
        var polygon = DwgSplitGeometry.BuildKeepPolygon(job);
        var rootViewportId = TryGetPaperSpaceRootViewportId(tr, owner);
        var ids = owner.Cast<ObjectId>().ToList();
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

            if (entity is Viewport viewport)
            {
                if (IsPaperSpaceRootViewport(viewport, rootViewportId)
                    || DwgSplitGeometry.ViewportHitsKeepPolygon(viewport, polygon))
                {
                    result.KeptEntities++;
                    continue;
                }

                if (viewport.Number <= 0 && rootViewportId.IsNull)
                {
                    result.KeptEntities++;
                    continue;
                }

                viewport.Erase();
                result.RemovedEntities++;
                continue;
            }

            if (DwgSplitGeometry.ShouldKeepEntity(tr, entity, job, result))
            {
                result.KeptEntities++;
                continue;
            }

            entity.Erase();
            result.RemovedEntities++;
        }
    }

    /// <summary>
    /// 纸面根视口。另存后再激活布局，优先 Number==1 和 Layout.GetViewports()[0]。
    /// </summary>
    private static ObjectId TryGetPaperSpaceRootViewportId(Transaction tr, BlockTableRecord owner)
    {
        try
        {
            if (owner.IsLayout && !owner.LayoutId.IsNull)
            {
                var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
                var viewportIds = layout.GetViewports();
                if (viewportIds != null && viewportIds.Count > 0)
                {
                    return viewportIds[0];
                }
            }
        }
        catch
        {
            // 再看 Number==1。
        }

        try
        {
            foreach (ObjectId id in owner)
            {
                if (!id.IsErased
                    && tr.GetObject(id, OpenMode.ForRead, false) is Viewport viewport
                    && viewport.Number == 1)
                {
                    return id;
                }
            }
        }
        catch
        {
            // 识别不到根视口时，中心不在图框内的视口仍会删除。
        }

        return ObjectId.Null;
    }

    private static bool IsPaperSpaceRootViewport(Viewport viewport, ObjectId rootViewportId)
    {
        return viewport.Number == 1
            || (!rootViewportId.IsNull && viewport.ObjectId == rootViewportId);
    }
}
