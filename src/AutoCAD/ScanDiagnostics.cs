using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands
{
    [CommandMethod("ZBP_DIAG_SCAN", CommandFlags.Session)]
    public void DiagnoseScan()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var lines = new List<string>
        {
            "Batch plot scan diagnostics",
            "Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "Document: " + (string.IsNullOrWhiteSpace(doc.Database.Filename) ? doc.Name : doc.Database.Filename),
            "Library: " + TitleBlockLibraryStore.DefaultPath
        };

        try
        {
            var library = TitleBlockLibraryStore.Load();
            lines.Add("Library blocks: " + library.Blocks.Count);
            foreach (var definition in library.Blocks.OrderBy(x => x.BlockName, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"LIB\t{definition.BlockName}\tmode={definition.CoordinateMode}\tprint=({definition.PrintRegion.MinX:0.###},{definition.PrintRegion.MinY:0.###})-({definition.PrintRegion.MaxX:0.###},{definition.PrintRegion.MaxY:0.###})\ttitle=({definition.TitleRegion.MinX:0.###},{definition.TitleRegion.MinY:0.###})-({definition.TitleRegion.MaxX:0.###},{definition.TitleRegion.MaxY:0.###})\tnumber=({definition.DrawingNumberRegion.MinX:0.###},{definition.DrawingNumberRegion.MinY:0.###})-({definition.DrawingNumberRegion.MaxX:0.###},{definition.DrawingNumberRegion.MaxY:0.###})");
            }

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var blockCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId recordId in blockTable)
                {
                    var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                    if (!owner.IsLayout)
                    {
                        continue;
                    }

                    foreach (ObjectId id in owner)
                    {
                        if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef)
                        {
                            continue;
                        }

                        var blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                        blockCounts.TryGetValue(blockName, out var count);
                        blockCounts[blockName] = count + 1;
                    }
                }

                lines.Add("Block references in layouts: " + blockCounts.Values.Sum());
                foreach (var pair in blockCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var inLibrary = library.Blocks.Any(x => string.Equals(x.BlockName, pair.Key, StringComparison.OrdinalIgnoreCase));
                    lines.Add($"BREF\t{pair.Key}\tcount={pair.Value}\tinLibrary={inLibrary}");
                }

                DumpMatchedFrameCandidates(tr, blockTable, library, lines);

                tr.Commit();
            }

            var jobs = TitleBlockScanner.Scan(doc, library);
            lines.Add("Scanned jobs: " + jobs.Count);
            foreach (var job in jobs)
            {
                lines.Add($"JOB\tblock={job.BlockName}\tspace={job.SpaceName}\tnumber={job.DrawingNumber}\ttitle={job.Title}\twindow=({job.MinX:0.###},{job.MinY:0.###})-({job.MaxX:0.###},{job.MaxY:0.###})\tnote={job.DetectionNote}");
            }
        }
        catch (System.Exception ex)
        {
            lines.Add("ERROR: " + ex);
        }

        var logDirectory = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "ScanDiagnostics_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
        File.WriteAllLines(logPath, lines);
        doc.Editor.WriteMessage("\nBatch plot scan diagnostics written to: " + logPath);
    }

    [CommandMethod("ZBP_DIAG_EXTENTS", CommandFlags.Session)]
    public void DiagnoseBlockExtents()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var lines = new List<string>
        {
            "Block extents diagnostics",
            "Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "Document: " + (string.IsNullOrWhiteSpace(doc.Database.Filename) ? doc.Name : doc.Database.Filename)
        };

        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var library = TitleBlockLibraryStore.Load();
            var names = new HashSet<string>(library.Blocks.Select(x => x.BlockName), StringComparer.OrdinalIgnoreCase);
            var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId recordId in blockTable)
            {
                var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                if (!owner.IsLayout)
                {
                    continue;
                }

                foreach (ObjectId id in owner)
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef)
                    {
                        continue;
                    }

                    var name = CadTextExtractor.GetBlockName(blockRef, tr);
                    if (!names.Contains(name))
                    {
                        continue;
                    }

                    lines.Add($"BREF\tname={name}\thandle={blockRef.Handle}\tdynamic={blockRef.IsDynamicBlock}\tposition={blockRef.Position}\tscale={blockRef.ScaleFactors}");
                    try
                    {
                        var ext = blockRef.GeometricExtents;
                        lines.Add($"BREF_EXT\tOK\tmin={ext.MinPoint}\tmax={ext.MaxPoint}");
                    }
                    catch (System.Exception ex)
                    {
                        lines.Add($"BREF_EXT\tFAIL\t{ex.GetType().FullName}\t{ex.Message}");
                    }

                    var definitionId = blockRef.IsDynamicBlock && !blockRef.DynamicBlockTableRecord.IsNull
                        ? blockRef.DynamicBlockTableRecord
                        : blockRef.BlockTableRecord;
                    var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
                    var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    var failedTypes = new Dictionary<string, int>(StringComparer.Ordinal);
                    var entityCount = 0;
                    var validCount = 0;
                    foreach (ObjectId entityId in definition)
                    {
                        var entity = tr.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                        if (entity == null)
                        {
                            continue;
                        }

                        entityCount++;
                        var typeName = entity.GetType().FullName ?? entity.GetType().Name;
                        typeCounts[typeName] = typeCounts.TryGetValue(typeName, out var typeCount) ? typeCount + 1 : 1;
                        try
                        {
                            var ext = entity.GeometricExtents;
                            if (IsFinite(ext.MinPoint) && IsFinite(ext.MaxPoint))
                            {
                                validCount++;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            failedTypes[typeName] = failedTypes.TryGetValue(typeName, out var failCount) ? failCount + 1 : 1;
                            if (entity is BlockReference nestedBlock)
                            {
                                var nestedName = CadTextExtractor.GetBlockName(nestedBlock, tr);
                                lines.Add($"DEF_FAIL_ENTITY\ttype={typeName}\thandle={entity.Handle}\tname={nestedName}\tposition={nestedBlock.Position}\tscale={nestedBlock.ScaleFactors}\terror={ex.Message}");
                            }
                            else
                            {
                                lines.Add($"DEF_FAIL_ENTITY\ttype={typeName}\thandle={entity.Handle}\terror={ex.Message}");
                            }
                        }
                    }

                    lines.Add($"DEF\tname={definition.Name}\tentities={entityCount}\tvalidExtents={validCount}\ttypes={string.Join(",", typeCounts.Select(x => x.Key + ":" + x.Value))}");
                    lines.Add($"DEF_FAIL_TYPES\t{string.Join(",", failedTypes.Select(x => x.Key + ":" + x.Value))}");
                }
            }

            tr.Commit();
        }

        var logDirectory = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "ExtentsDiagnostics_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
        File.WriteAllLines(logPath, lines);
        doc.Editor.WriteMessage("\nBlock extents diagnostics written to: " + logPath);
    }

    [CommandMethod("ZBP_DIAG_SCAN_SCOPES", CommandFlags.Session)]
    public void DiagnoseScanScopes()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var library = TitleBlockLibraryStore.Load();
        var lines = new List<string>
        {
            "Scan scope diagnostics",
            "Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "Document: " + (string.IsNullOrWhiteSpace(doc.Database.Filename) ? doc.Name : doc.Database.Filename)
        };

        foreach (var scope in new[]
                 {
                     TitleBlockScanScope.AllSpaces,
                     TitleBlockScanScope.PaperLayouts,
                     TitleBlockScanScope.CurrentSpace,
                     TitleBlockScanScope.ModelSpace
                 })
        {
            try
            {
                var jobs = TitleBlockScanner.Scan(doc, library, scope);
                lines.Add($"{scope}\tcount={jobs.Count}\tspaces={string.Join(",", jobs.GroupBy(x => x.SpaceName).Select(x => x.Key + ":" + x.Count()))}");
            }
            catch (System.Exception ex)
            {
                lines.Add($"{scope}\tERROR\t{ex}");
            }
        }

        var logDirectory = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "ScanScopes_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
        File.WriteAllLines(logPath, lines);
        doc.Editor.WriteMessage("\nScan scope diagnostics written to: " + logPath);
    }

    [CommandMethod("ZBP_DIAG_ATTRIBUTES", CommandFlags.Session)]
    public void DiagnoseAttributes()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        var lines = new List<string>
        {
            "Attribute diagnostics",
            "Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "Document: " + (string.IsNullOrWhiteSpace(doc.Database.Filename) ? doc.Name : doc.Database.Filename)
        };

        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            var library = TitleBlockLibraryStore.Load();
            var names = new HashSet<string>(library.Blocks.Select(x => x.BlockName), StringComparer.OrdinalIgnoreCase);
            var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            var dumped = 0;
            foreach (ObjectId recordId in blockTable)
            {
                var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                if (!owner.IsLayout)
                {
                    continue;
                }

                foreach (ObjectId id in owner)
                {
                    if (dumped >= 160 || tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef)
                    {
                        continue;
                    }

                    var name = CadTextExtractor.GetBlockName(blockRef, tr);
                    if (!names.Contains(name) && blockRef.AttributeCollection.Count == 0)
                    {
                        continue;
                    }

                    lines.Add($"BREF\tname={name}\thandle={blockRef.Handle}\tattributes={blockRef.AttributeCollection.Count}");
                    foreach (ObjectId attributeId in blockRef.AttributeCollection)
                    {
                        if (tr.GetObject(attributeId, OpenMode.ForRead, false) is not AttributeReference attribute)
                        {
                            continue;
                        }

                        lines.Add(
                            $"ATTR\ttag={attribute.Tag}\ttext={attribute.TextString}\tposition={attribute.Position}\talignment={ReadPointProperty(attribute, "AlignmentPoint")}\tinvisible={attribute.Invisible}\tmtext={ReadProperty(attribute, "IsMTextAttribute")}\tmtextValue={ReadMTextAttribute(attribute)}");
                    }

                    var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
                    DumpNestedAttributes(tr, definition, Matrix3d.Identity, lines, new HashSet<ObjectId>(), 0);
                    foreach (ObjectId definitionId in definition)
                    {
                        if (tr.GetObject(definitionId, OpenMode.ForRead, false) is AttributeDefinition attributeDefinition)
                        {
                            lines.Add(
                                $"ATTDEF\ttag={attributeDefinition.Tag}\tdefault={attributeDefinition.TextString}\tposition={attributeDefinition.Position}\talignment={ReadPointProperty(attributeDefinition, "AlignmentPoint")}\tconstant={attributeDefinition.Constant}\tinvisible={attributeDefinition.Invisible}");
                        }
                    }

                    if (dumped == 0 && string.Equals(name, "TKHF", StringComparison.OrdinalIgnoreCase))
                    {
                        DumpDefinitionText(tr, definition, Matrix3d.Identity, lines, new HashSet<ObjectId>(), 0);
                    }

                    dumped++;
                }
            }

            tr.Commit();
        }

        var logDirectory = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Logs");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "Attributes_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
        File.WriteAllLines(logPath, lines);
        doc.Editor.WriteMessage("\nAttribute diagnostics written to: " + logPath);
    }

    private static void DumpNestedAttributes(
        Transaction tr,
        BlockTableRecord definition,
        Matrix3d entityToRoot,
        ICollection<string> lines,
        ISet<ObjectId> visited,
        int depth)
    {
        if (depth > 10 || !visited.Add(definition.ObjectId))
        {
            return;
        }

        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference nested)
            {
                continue;
            }

            foreach (ObjectId attributeId in nested.AttributeCollection)
            {
                if (tr.GetObject(attributeId, OpenMode.ForRead, false) is not AttributeReference attribute)
                {
                    continue;
                }

                var local = attribute.Position.TransformBy(entityToRoot);
                var alignment = ReadPointProperty(attribute, "AlignmentPoint");
                lines.Add($"NESTED_ATTR\tdepth={depth}\tblock={CadTextExtractor.GetBlockName(nested, tr)}\ttag={attribute.Tag}\ttext={attribute.TextString}\trootPosition={local}\talignment={alignment}");
            }

            try
            {
                var nestedDefinition = (BlockTableRecord)tr.GetObject(nested.BlockTableRecord, OpenMode.ForRead);
                DumpNestedAttributes(
                    tr,
                    nestedDefinition,
                    nested.BlockTransform * entityToRoot,
                    lines,
                    visited,
                    depth + 1);
            }
            catch
            {
            }
        }

        visited.Remove(definition.ObjectId);
    }

    private static void DumpDefinitionText(
        Transaction tr,
        BlockTableRecord definition,
        Matrix3d entityToRoot,
        ICollection<string> lines,
        ISet<ObjectId> visited,
        int depth)
    {
        if (depth > 12 || !visited.Add(definition.ObjectId))
        {
            return;
        }

        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            string text = "";
            Point3d point = Point3d.Origin;
            string tag = "";
            if (entity is AttributeDefinition attributeDefinition)
            {
                text = attributeDefinition.TextString;
                point = attributeDefinition.Position;
                tag = attributeDefinition.Tag;
            }
            else if (entity is DBText dbText)
            {
                text = dbText.TextString;
                point = dbText.Position;
            }
            else if (entity is MText mText)
            {
                text = mText.Contents;
                point = mText.Location;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                lines.Add(
                    $"DEF_TEXT\tdepth={depth}\ttype={entity.GetType().Name}\ttag={tag}\ttext={text}\trootPosition={point.TransformBy(entityToRoot)}");
            }

            if (entity is not BlockReference nested)
            {
                continue;
            }

            foreach (ObjectId attributeId in nested.AttributeCollection)
            {
                if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute)
                {
                    lines.Add(
                        $"DEF_ATTR\tdepth={depth}\tblock={CadTextExtractor.GetBlockName(nested, tr)}\ttag={attribute.Tag}\ttext={attribute.TextString}\trootPosition={attribute.Position.TransformBy(entityToRoot)}");
                }
            }

            try
            {
                var nestedDefinition = (BlockTableRecord)tr.GetObject(nested.BlockTableRecord, OpenMode.ForRead);
                DumpDefinitionText(
                    tr,
                    nestedDefinition,
                    nested.BlockTransform * entityToRoot,
                    lines,
                    visited,
                    depth + 1);
            }
            catch
            {
            }
        }

        visited.Remove(definition.ObjectId);
    }

    private static object ReadProperty(object value, string propertyName)
    {
        try
        {
            return value.GetType().GetProperty(propertyName)?.GetValue(value, null) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static object ReadPointProperty(object value, string propertyName)
    {
        return ReadProperty(value, propertyName);
    }

    private static string ReadMTextAttribute(AttributeReference attribute)
    {
        try
        {
            var value = attribute.GetType().GetProperty("MTextAttribute")?.GetValue(attribute, null);
            if (value is MText mText)
            {
                return mText.Contents;
            }
        }
        catch
        {
        }

        return "";
    }

    private static bool IsFinite(Point3d point)
    {
        return !double.IsNaN(point.X) && !double.IsInfinity(point.X)
            && !double.IsNaN(point.Y) && !double.IsInfinity(point.Y)
            && !double.IsNaN(point.Z) && !double.IsInfinity(point.Z);
    }

    private static void DumpMatchedFrameCandidates(
        Transaction tr,
        BlockTable blockTable,
        TitleBlockLibrary library,
        ICollection<string> lines)
    {
        var dumped = 0;
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            foreach (ObjectId id in owner)
            {
                if (dumped >= 6)
                {
                    return;
                }

                if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference frameRef)
                {
                    continue;
                }

                var frameName = CadTextExtractor.GetBlockName(frameRef, tr);
                var definition = library.Blocks.FirstOrDefault(x =>
                    string.Equals(x.BlockName, frameName, StringComparison.OrdinalIgnoreCase));
                if (definition == null)
                {
                    continue;
                }

                dumped++;
                var frame = definition.HasPrintRegion ? definition.PrintRegion : GetBlockLocalExtents(frameRef);
                var title = definition.TitleRegion;
                var number = definition.DrawingNumberRegion;
                var inverse = frameRef.BlockTransform.Inverse();
                var rawBounds = TryGetWorldBounds(frameRef, out var frameBounds)
                    ? $"rawBounds=({frameBounds.MinX:0.###},{frameBounds.MinY:0.###})-({frameBounds.MaxX:0.###},{frameBounds.MaxY:0.###})"
                    : "rawBounds=NA";
                lines.Add($"PROBE\tblock={frameName}\tspace={owner.Name}\tpos=({frameRef.Position.X:0.###},{frameRef.Position.Y:0.###})\t{rawBounds}\tframe=({frame.MinX:0.###},{frame.MinY:0.###})-({frame.MaxX:0.###},{frame.MaxY:0.###})\ttitleRel=({title.MinX:0.###},{title.MinY:0.###})-({title.MaxX:0.###},{title.MaxY:0.###})\tnumberRel=({number.MinX:0.###},{number.MinY:0.###})-({number.MaxX:0.###},{number.MaxY:0.###})");
                DumpDefinitionCandidates(tr, frameRef, frame, lines);
                DumpWindowCandidates(tr, owner, frameRef.ObjectId, frameRef.BlockTransform, frame, lines);
                DumpOwnerCandidates(tr, owner, frameRef.ObjectId, inverse, frame, title, number, lines);
            }
        }
    }

    private static void DumpWindowCandidates(
        Transaction tr,
        BlockTableRecord owner,
        ObjectId frameId,
        Matrix3d frameLocalToWorld,
        LocalRectangle frame,
        ICollection<string> lines)
    {
        if (!TryTransformRectangle(frame, frameLocalToWorld, out var worldFrame))
        {
            return;
        }

        var width = worldFrame.MaxX - worldFrame.MinX;
        var height = worldFrame.MaxY - worldFrame.MinY;
        var focus = LocalRectangle.FromPoints(
            worldFrame.MinX + width * 0.55,
            worldFrame.MinY,
            worldFrame.MaxX,
            worldFrame.MinY + height * 0.3);
        var fullWindow = LocalRectangle.FromPoints(worldFrame.MinX, worldFrame.MinY, worldFrame.MaxX, worldFrame.MaxY);

        var count = 0;
        var fullCount = 0;
        foreach (ObjectId id in owner)
        {
            if (count >= 80 || id == frameId)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            if (TryGetEntityText(entity, out var text, out var point))
            {
                if (Contains(fullWindow, point.X, point.Y) || (TryGetWorldBounds(entity, out var fullBounds) && Intersects(fullBounds, fullWindow)))
                {
                    fullCount++;
                    if (fullCount <= 30)
                    {
                        lines.Add($"WIN_ANY_TEXT\ttype={entity.GetType().Name}\tpt=({point.X:0.###},{point.Y:0.###})\ttext={text}");
                    }
                }

                if (Contains(focus, point.X, point.Y) || (TryGetWorldBounds(entity, out var bounds) && Intersects(bounds, focus)))
                {
                    count++;
                    lines.Add($"WIN_TEXT\ttype={entity.GetType().Name}\tpt=({point.X:0.###},{point.Y:0.###})\ttext={text}");
                }
                continue;
            }

            if (entity is BlockReference blockRef && TryGetWorldBounds(blockRef, out var blockBounds) && Intersects(blockBounds, focus))
            {
                count++;
                var blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                lines.Add($"WIN_BLOCK\tname={blockName}\tpt=({blockRef.Position.X:0.###},{blockRef.Position.Y:0.###})\tbounds=({blockBounds.MinX:0.###},{blockBounds.MinY:0.###})-({blockBounds.MaxX:0.###},{blockBounds.MaxY:0.###})");
            }
        }

        lines.Add($"WIN_ANY_COUNT\ttexts={fullCount}\twindow=({fullWindow.MinX:0.###},{fullWindow.MinY:0.###})-({fullWindow.MaxX:0.###},{fullWindow.MaxY:0.###})");
    }

    private static void DumpDefinitionCandidates(
        Transaction tr,
        BlockReference frameRef,
        LocalRectangle frame,
        ICollection<string> lines)
    {
        try
        {
            var definition = (BlockTableRecord)tr.GetObject(frameRef.BlockTableRecord, OpenMode.ForRead);
            DumpDefinitionCandidates(tr, definition, Matrix3d.Identity, frame, lines, new HashSet<ObjectId>(), 0, ref UnsafeCounter.Value);
            UnsafeCounter.Value = 0;
        }
        catch
        {
        }
    }

    private static void DumpDefinitionCandidates(
        Transaction tr,
        BlockTableRecord definition,
        Matrix3d entityToFrameLocal,
        LocalRectangle frame,
        ICollection<string> lines,
        ISet<ObjectId> visited,
        int depth,
        ref int count)
    {
        if (count >= 80 || depth > 8 || !visited.Add(definition.ObjectId))
        {
            return;
        }

        foreach (ObjectId id in definition)
        {
            if (count >= 80)
            {
                break;
            }

            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            if (TryGetEntityText(entity, out var text, out var point))
            {
                var local = point.TransformBy(entityToFrameLocal);
                var rel = ToFrameRelative(local, frame);
                if (IsInFrameBottomRight(rel, frame))
                {
                    count++;
                    lines.Add($"DEF_TEXT\tdepth={depth}\ttype={entity.GetType().Name}\tpt=({rel.X:0.###},{rel.Y:0.###})\ttext={text}");
                }
            }

            if (entity is BlockReference nestedBlock)
            {
                var nestedToFrameLocal = nestedBlock.BlockTransform * entityToFrameLocal;
                foreach (ObjectId attributeId in nestedBlock.AttributeCollection)
                {
                    if (!attributeId.IsValid || attributeId.IsErased)
                    {
                        continue;
                    }

                    if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute
                        && TryGetEntityText(attribute, out var attributeText, out var attributePoint))
                    {
                        var local = attributePoint.TransformBy(entityToFrameLocal);
                        var rel = ToFrameRelative(local, frame);
                        if (IsInFrameBottomRight(rel, frame))
                        {
                            count++;
                            lines.Add($"DEF_ATTR\tdepth={depth}\tpt=({rel.X:0.###},{rel.Y:0.###})\ttext={attributeText}");
                        }
                    }
                }

                try
                {
                    var nestedDefinition = (BlockTableRecord)tr.GetObject(nestedBlock.BlockTableRecord, OpenMode.ForRead);
                    DumpDefinitionCandidates(tr, nestedDefinition, nestedToFrameLocal, frame, lines, visited, depth + 1, ref count);
                }
                catch
                {
                }
            }
        }

        visited.Remove(definition.ObjectId);
    }

    private static void DumpOwnerCandidates(
        Transaction tr,
        BlockTableRecord owner,
        ObjectId frameId,
        Matrix3d worldToFrameLocal,
        LocalRectangle frame,
        LocalRectangle title,
        LocalRectangle number,
        ICollection<string> lines)
    {
        var count = 0;
        foreach (ObjectId id in owner)
        {
            if (count >= 80 || id == frameId)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            if (TryGetEntityText(entity, out var text, out var point))
            {
                var local = ToFrameRelative(point.TransformBy(worldToFrameLocal), frame);
                if (IsCandidate(local, frame, title, number))
                {
                    count++;
                    lines.Add($"CAND_TEXT\ttype={entity.GetType().Name}\tpt=({local.X:0.###},{local.Y:0.###})\ttext={text}");
                }
                continue;
            }

            if (entity is BlockReference blockRef)
            {
                var name = CadTextExtractor.GetBlockName(blockRef, tr);
                var pointLocal = ToFrameRelative(blockRef.Position.TransformBy(worldToFrameLocal), frame);
                if (TryGetBounds(blockRef, worldToFrameLocal, frame, out var bounds)
                    && IsCandidate(bounds, frame, title, number))
                {
                    count++;
                    lines.Add($"CAND_BLOCK\tname={name}\tpt=({pointLocal.X:0.###},{pointLocal.Y:0.###})\tbounds=({bounds.MinX:0.###},{bounds.MinY:0.###})-({bounds.MaxX:0.###},{bounds.MaxY:0.###})");
                }
            }
        }
    }

    private static bool TryGetEntityText(Entity entity, out string text, out Point3d point)
    {
        text = "";
        point = Point3d.Origin;
        if (entity is DBText dbText)
        {
            text = dbText.TextString;
            point = dbText.Position;
            return true;
        }

        if (entity is AttributeReference attribute)
        {
            text = attribute.TextString;
            point = attribute.Position;
            return true;
        }

        if (entity is MText mText)
        {
            var textProperty = typeof(MText).GetProperty("Text", BindingFlags.Instance | BindingFlags.Public);
            text = textProperty?.GetValue(mText, null) as string ?? mText.Contents;
            point = mText.Location;
            return true;
        }

        return false;
    }

    private static bool TryGetBounds(Entity entity, Matrix3d transform, LocalRectangle frame, out LocalRectangle bounds)
    {
        bounds = new LocalRectangle();
        try
        {
            var extents = entity.GeometricExtents;
            var points = new[]
            {
                ToFrameRelative(new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(transform), frame),
                ToFrameRelative(new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform), frame),
                ToFrameRelative(new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(transform), frame),
                ToFrameRelative(new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform), frame)
            };

            bounds = LocalRectangle.FromPoints(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetWorldBounds(Entity entity, out LocalRectangle bounds)
    {
        bounds = new LocalRectangle();
        try
        {
            var extents = entity.GeometricExtents;
            bounds = LocalRectangle.FromPoints(
                extents.MinPoint.X,
                extents.MinPoint.Y,
                extents.MaxPoint.X,
                extents.MaxPoint.Y);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryTransformRectangle(LocalRectangle rectangle, Matrix3d transform, out LocalRectangle transformed)
    {
        transformed = new LocalRectangle();
        try
        {
            var points = new[]
            {
                new Point3d(rectangle.MinX, rectangle.MinY, 0).TransformBy(transform),
                new Point3d(rectangle.MinX, rectangle.MaxY, 0).TransformBy(transform),
                new Point3d(rectangle.MaxX, rectangle.MinY, 0).TransformBy(transform),
                new Point3d(rectangle.MaxX, rectangle.MaxY, 0).TransformBy(transform)
            };

            transformed = LocalRectangle.FromPoints(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Point3d ToFrameRelative(Point3d point, LocalRectangle frame)
    {
        return new Point3d(point.X - frame.MinX, point.Y - frame.MinY, 0);
    }

    private static bool IsCandidate(Point3d point, LocalRectangle frame, LocalRectangle title, LocalRectangle number)
    {
        if (Contains(Expand(title, 6000), point.X, point.Y) || Contains(Expand(number, 6000), point.X, point.Y))
        {
            return true;
        }

        var width = Math.Abs(frame.MaxX - frame.MinX);
        var height = Math.Abs(frame.MaxY - frame.MinY);
        return point.X >= width * 0.65 && point.Y <= height * 0.22;
    }

    private static bool IsInFrameBottomRight(Point3d point, LocalRectangle frame)
    {
        var width = Math.Abs(frame.MaxX - frame.MinX);
        var height = Math.Abs(frame.MaxY - frame.MinY);
        return point.X >= width * 0.55
            && point.X <= width + 10000
            && point.Y >= -10000
            && point.Y <= height * 0.25;
    }

    private static bool IsCandidate(LocalRectangle bounds, LocalRectangle frame, LocalRectangle title, LocalRectangle number)
    {
        return Intersects(bounds, Expand(title, 6000))
            || Intersects(bounds, Expand(number, 6000))
            || IsCandidate(new Point3d((bounds.MinX + bounds.MaxX) / 2d, (bounds.MinY + bounds.MaxY) / 2d, 0), frame, title, number);
    }

    private static bool Contains(LocalRectangle rectangle, double x, double y)
    {
        return x >= rectangle.MinX && x <= rectangle.MaxX && y >= rectangle.MinY && y <= rectangle.MaxY;
    }

    private static bool Intersects(LocalRectangle a, LocalRectangle b)
    {
        return a.MinX <= b.MaxX && a.MaxX >= b.MinX && a.MinY <= b.MaxY && a.MaxY >= b.MinY;
    }

    private static LocalRectangle Expand(LocalRectangle rectangle, double amount)
    {
        return LocalRectangle.FromPoints(
            rectangle.MinX - amount,
            rectangle.MinY - amount,
            rectangle.MaxX + amount,
            rectangle.MaxY + amount);
    }

    private static LocalRectangle GetBlockLocalExtents(BlockReference blockRef)
    {
        try
        {
            var inverse = blockRef.BlockTransform.Inverse();
            var extents = blockRef.GeometricExtents;
            var points = new[]
            {
                new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(inverse),
                new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(inverse),
                new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(inverse),
                new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(inverse)
            };

            return LocalRectangle.FromPoints(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y));
        }
        catch
        {
            return new LocalRectangle();
        }
    }

    private static class UnsafeCounter
    {
        public static int Value;
    }
}
