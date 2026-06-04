using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;

namespace ZwcadBatchPlot;

public static class DwgSplitService
{
    public sealed class SplitResult
    {
        public PlotJob Job { get; set; } = new();
        public string OutputPath { get; set; } = "";
        public Exception? Error { get; set; }
        public int RemovedEntities { get; set; }
        public int KeptEntities { get; set; }
        public int UnknownExtentsKept { get; set; }
    }

    public static List<SplitResult> SplitMany(
        IReadOnlyList<PlotJob> jobs,
        Document currentDocument,
        AppSettings settings,
        Action<PlotJob>? beforeJob = null)
    {
        var results = new List<SplitResult>();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in jobs.GroupBy(x => GetSourceKey(x, currentDocument), StringComparer.OrdinalIgnoreCase))
        {
            foreach (var job in group)
            {
                beforeJob?.Invoke(job);
                var result = new SplitResult { Job = job };
                try
                {
                    var sourceFile = ResolveSourceFile(job, currentDocument);
                    if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
                    {
                        throw new FileNotFoundException("源 DWG 文件不存在，请先保存当前图纸。", sourceFile);
                    }

                    var outputPath = BuildOutputPath(job, sourceFile, settings, reservedPaths);
                    if (job.IsPaperSpace)
                    {
                        SplitPaperByCleaningCopy(sourceFile, outputPath, job, result);
                    }
                    else
                    {
                        SplitModelByCloningWindow(sourceFile, outputPath, job, result);
                    }

                    result.OutputPath = outputPath;
                }
                catch (Exception ex)
                {
                    result.Error = ex;
                }

                results.Add(result);
            }
        }

        return results;
    }

    private static void SplitModelByCloningWindow(string sourceFile, string outputPath, PlotJob job, SplitResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var sourceDb = new Database(false, true);
        sourceDb.ReadDwgFile(sourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
        sourceDb.CloseInput(true);

        var idsToClone = CollectModelWindowEntities(sourceDb, job, result);
        using var targetDb = new Database(true, true);
        if (idsToClone.Count > 0)
        {
            using var targetTr = targetDb.TransactionManager.StartTransaction();
            var targetBlockTable = (BlockTable)targetTr.GetObject(targetDb.BlockTableId, OpenMode.ForRead);
            var targetModel = (BlockTableRecord)targetTr.GetObject(targetBlockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var mapping = new IdMapping();
            sourceDb.WblockCloneObjects(idsToClone, targetModel.ObjectId, mapping, DuplicateRecordCloning.Replace, false);
            targetTr.Commit();
        }

        FitModelViewToTitleBlock(targetDb, job);
        targetDb.SaveAs(outputPath, DwgVersion.Current);
    }

    private static ObjectIdCollection CollectModelWindowEntities(Database sourceDb, PlotJob job, SplitResult result)
    {
        var ids = new ObjectIdCollection();
        using var tr = sourceDb.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(sourceDb.BlockTableId, OpenMode.ForRead);
        var model = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        var window = BuildWindow(job);

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

            if (ShouldKeepEntity(entity, window, result))
            {
                ids.Add(id);
                result.KeptEntities++;
            }
            else
            {
                result.RemovedEntities++;
            }
        }

        tr.Commit();
        return ids;
    }

    private static void SplitPaperByCleaningCopy(string sourceFile, string outputPath, PlotJob job, SplitResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Copy(sourceFile, outputPath, overwrite: true);

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
                var layerLocks = UnlockLayers(tr, db);
                CleanPaperSpace(tr, blockTable, job, result);
                RestoreLayerLocks(tr, layerLocks);
                tr.Commit();
            }

            DeleteUnneededLayouts(db, job);
            db.SaveAs(outputPath, DwgVersion.Current);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
        }
    }

    private static void CleanPaperSpace(Transaction tr, BlockTable blockTable, PlotJob job, SplitResult result)
    {
        var target = FindSpaceRecord(tr, blockTable, job.SpaceName);
        if (target == null)
        {
            throw new InvalidOperationException("未找到布局空间: " + job.SpaceName);
        }

        target.UpgradeOpen();
        CleanOwnerByWindow(tr, target, job, result);
    }

    private static void CleanOwnerByWindow(Transaction tr, BlockTableRecord owner, PlotJob job, SplitResult result)
    {
        var window = BuildWindow(job);
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

            if (ShouldKeepEntity(entity, window, result))
            {
                result.KeptEntities++;
                continue;
            }

            entity.Erase();
            result.RemovedEntities++;
        }
    }

    private static Extents3d BuildWindow(PlotJob job)
    {
        return new Extents3d(
            new Point3d(Math.Min(job.MinX, job.MaxX), Math.Min(job.MinY, job.MaxY), 0),
            new Point3d(Math.Max(job.MinX, job.MaxX), Math.Max(job.MinY, job.MaxY), 0));
    }

    private static void FitModelViewToTitleBlock(Database db, PlotJob job)
    {
        try
        {
            var minX = Math.Min(job.MinX, job.MaxX);
            var minY = Math.Min(job.MinY, job.MaxY);
            var maxX = Math.Max(job.MinX, job.MaxX);
            var maxY = Math.Max(job.MinY, job.MaxY);
            var width = Math.Max(maxX - minX, 1);
            var height = Math.Max(maxY - minY, 1);
            var screenAspect = 16.0 / 9.0;
            var viewHeight = Math.Max(height, width / screenAspect) * 1.05;
            var viewWidth = Math.Max(width, height * screenAspect) * 1.05;
            var center = new Point2d((minX + maxX) / 2.0, (minY + maxY) / 2.0);

            db.UpdateExt(true);
            db.TileMode = true;
            using var tr = db.TransactionManager.StartTransaction();
            var viewportTable = (ViewportTable)tr.GetObject(db.ViewportTableId, OpenMode.ForRead);
            foreach (ObjectId viewportId in viewportTable)
            {
                var viewport = (ViewportTableRecord)tr.GetObject(viewportId, OpenMode.ForWrite);
                if (!string.Equals(viewport.Name, "*Active", StringComparison.OrdinalIgnoreCase)
                    && viewport.Number != 1)
                {
                    continue;
                }

                viewport.CenterPoint = center;
                viewport.Height = viewHeight;
                viewport.Width = viewWidth;
                viewport.ViewDirection = Vector3d.ZAxis;
                viewport.Target = Point3d.Origin;
                viewport.ViewTwist = 0;
            }

            tr.Commit();
        }
        catch
        {
            // View data is only an opening convenience; the DWG content itself has already been split.
        }
    }

    private static bool ShouldKeepEntity(Entity entity, Extents3d window, SplitResult result)
    {
        try
        {
            return Intersects(entity.GeometricExtents, window);
        }
        catch
        {
            // Some proxy entities, empty attributes, viewports, or custom objects cannot expose extents reliably.
            // Keep them rather than risk deleting visible drawing content.
            result.UnknownExtentsKept++;
            return true;
        }
    }

    private static bool Intersects(Extents3d a, Extents3d b)
    {
        return a.MinPoint.X <= b.MaxPoint.X
            && a.MaxPoint.X >= b.MinPoint.X
            && a.MinPoint.Y <= b.MaxPoint.Y
            && a.MaxPoint.Y >= b.MinPoint.Y;
    }

    private static List<(ObjectId LayerId, bool WasLocked)> UnlockLayers(Transaction tr, Database db)
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

    private static void RestoreLayerLocks(Transaction tr, IEnumerable<(ObjectId LayerId, bool WasLocked)> states)
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

    private static BlockTableRecord? FindSpaceRecord(Transaction tr, BlockTable blockTable, string spaceName)
    {
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            if (string.Equals(owner.Name, spaceName, StringComparison.OrdinalIgnoreCase))
            {
                return owner;
            }
        }

        return null;
    }

    private static void DeleteUnneededLayouts(Database db, PlotJob job)
    {
        var layoutNames = new List<(string LayoutName, string BlockRecordName, bool ModelType)>();
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId recordId in blockTable)
            {
                var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                if (!owner.IsLayout)
                {
                    continue;
                }

                var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
                layoutNames.Add((layout.LayoutName, owner.Name, layout.ModelType));
            }

            tr.Commit();
        }

        var manager = LayoutManager.Current;
        var keepLayout = job.IsPaperSpace
            ? layoutNames.FirstOrDefault(x => string.Equals(x.BlockRecordName, job.SpaceName, StringComparison.OrdinalIgnoreCase)).LayoutName
            : "Model";
        if (!string.IsNullOrWhiteSpace(keepLayout))
        {
            try
            {
                manager.CurrentLayout = keepLayout;
            }
            catch
            {
                // Continue even if a side database refuses layout activation.
            }
        }

        foreach (var layout in layoutNames)
        {
            if (layout.ModelType)
            {
                continue;
            }

            if (job.IsPaperSpace && string.Equals(layout.BlockRecordName, job.SpaceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                manager.DeleteLayout(layout.LayoutName);
            }
            catch
            {
                // Layout deletion can fail for drawings with protected or unusual layout dictionaries.
                // The target space has already been cleaned, so keep going.
            }
        }
    }

    private static string BuildOutputPath(PlotJob job, string sourceFile, AppSettings settings, ISet<string> reservedPaths)
    {
        var directory = Path.Combine(Path.GetDirectoryName(sourceFile) ?? "", "DWG");
        var separator = settings.PdfFileNameSeparator ?? "_";
        var baseName = FileNameSanitizer.Clean($"{job.DrawingNumber}{separator}{job.Title}");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, baseName + ".dwg");
        if (!settings.AddSequenceWhenPdfExists)
        {
            reservedPaths.Add(path);
            return path;
        }

        var index = 1;
        while (File.Exists(path) || reservedPaths.Contains(path))
        {
            path = Path.Combine(directory, $"{baseName}_{index}.dwg");
            index++;
        }

        reservedPaths.Add(path);
        return path;
    }

    private static string GetSourceKey(PlotJob job, Document currentDocument)
    {
        var source = ResolveSourceFile(job, currentDocument);
        return string.IsNullOrWhiteSpace(source) ? job.SourceFile : source;
    }

    private static string ResolveSourceFile(PlotJob job, Document currentDocument)
    {
        if (!string.IsNullOrWhiteSpace(job.SourceFile) && File.Exists(job.SourceFile))
        {
            return Path.GetFullPath(job.SourceFile);
        }

        var currentFile = currentDocument.Database.Filename;
        if (!string.IsNullOrWhiteSpace(currentFile) && File.Exists(currentFile))
        {
            return Path.GetFullPath(currentFile);
        }

        return job.SourceFile;
    }
}
