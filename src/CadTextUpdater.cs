using System;
using System.IO;
using System.Linq;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

public static class CadTextUpdater
{
    public static bool TryUpdateOpenDocument(PlotJob job, string? newTitle, string? newNumber, Document currentDocument, out string message)
    {
        message = "";
        var doc = FindTargetDocument(job, currentDocument);
        if (doc == null)
        {
            message = "对应 DWG 未打开，已只修改表格，未反写 CAD。";
            return false;
        }

        using (doc.LockDocument())
        {
            return TryUpdate(doc.Database, job, newTitle, newNumber, out message);
        }
    }

    private static bool TryUpdate(Database db, PlotJob job, string? newTitle, string? newNumber, out string message)
    {
        message = "";
        var library = TitleBlockLibraryStore.Load();
        var definition = library.Blocks.FirstOrDefault(x => string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase));
        if (definition == null)
        {
            message = "图框库中找不到对应块定义。";
            return false;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var owner = FindOwnerRecord(tr, blockTable, job.SpaceName);
        if (owner == null)
        {
            message = "找不到对应空间。";
            return false;
        }

        var blockRef = FindBlockReference(tr, owner, job, definition);
        if (blockRef == null)
        {
            message = "找不到对应图框块。";
            return false;
        }

        var changed = 0;
        if (newTitle != null)
        {
            changed += UpdateRegionText(tr, owner, blockRef, definition.TitleRegion, newTitle);
        }

        if (newNumber != null)
        {
            changed += UpdateRegionText(tr, owner, blockRef, definition.DrawingNumberRegion, newNumber);
        }

        if (changed == 0)
        {
            message = "未找到可写文字。若图名/图号是块定义里的固定文字，请改成属性或图框外独立文字。";
            return false;
        }

        tr.Commit();
        message = $"已反写 CAD 文字 {changed} 处。";
        return true;
    }

    private static BlockTableRecord? FindOwnerRecord(Transaction tr, BlockTable blockTable, string spaceName)
    {
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (owner.IsLayout && string.Equals(owner.Name, spaceName, StringComparison.OrdinalIgnoreCase))
            {
                return owner;
            }
        }

        return null;
    }

    private static BlockReference? FindBlockReference(Transaction tr, BlockTableRecord owner, PlotJob job, TitleBlockDefinition definition)
    {
        var matches = owner
            .Cast<ObjectId>()
            .Select(id => tr.GetObject(id, OpenMode.ForRead, false))
            .OfType<BlockReference>()
            .Where(blockRef => string.Equals(CadTextExtractor.GetBlockName(blockRef, tr), job.BlockName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var byOriginalText = matches.FirstOrDefault(blockRef =>
            string.Equals(CadTextExtractor.ExtractRegionText(tr, blockRef, owner, definition.DrawingNumberRegion), job.CadDrawingNumber, StringComparison.Ordinal)
            && string.Equals(CadTextExtractor.ExtractRegionText(tr, blockRef, owner, definition.TitleRegion), job.CadTitle, StringComparison.Ordinal));

        return byOriginalText ?? matches.ElementAtOrDefault(job.MatchIndex) ?? matches.FirstOrDefault();
    }

    private static int UpdateRegionText(Transaction tr, BlockTableRecord owner, BlockReference blockRef, LocalRectangle region, string value)
    {
        var changed = 0;
        var inverse = blockRef.BlockTransform.Inverse();

        foreach (ObjectId attributeId in blockRef.AttributeCollection)
        {
            if (!attributeId.IsValid || attributeId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attributeId, OpenMode.ForWrite, false) is AttributeReference attribute)
            {
                var local = attribute.Position.TransformBy(inverse);
                if (region.Contains(local.X, local.Y))
                {
                    attribute.TextString = value;
                    changed++;
                }
            }
        }

        foreach (ObjectId id in owner)
        {
            if (id == blockRef.ObjectId)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForWrite, false) is DBText dbText)
            {
                var local = dbText.Position.TransformBy(inverse);
                if (region.Contains(local.X, local.Y))
                {
                    dbText.TextString = value;
                    changed++;
                }
            }
            else if (tr.GetObject(id, OpenMode.ForWrite, false) is MText mText)
            {
                var local = mText.Location.TransformBy(inverse);
                if (region.Contains(local.X, local.Y))
                {
                    mText.Contents = value;
                    changed++;
                }
            }
        }

        return changed;
    }

    private static Document? FindTargetDocument(PlotJob job, Document currentDocument)
    {
        if (IsSameDocument(job.SourceFile, currentDocument))
        {
            return currentDocument;
        }

        foreach (Document doc in CadApp.DocumentManager)
        {
            if (IsSameDocument(job.SourceFile, doc))
            {
                return doc;
            }
        }

        return null;
    }

    private static bool IsSameDocument(string sourceFile, Document doc)
    {
        var docFile = doc.Database.Filename;
        if (string.IsNullOrWhiteSpace(sourceFile) || string.IsNullOrWhiteSpace(docFile))
        {
            return string.Equals(sourceFile, doc.Name, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(docFile), StringComparison.OrdinalIgnoreCase);
    }
}
