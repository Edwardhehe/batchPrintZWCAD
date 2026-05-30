using System;
using System.Collections.Generic;
using System.Linq;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;

namespace ZwcadBatchPlot;

public static class DirectoryTableGenerator
{
    private static readonly string[] Headers = { "序号", "图号", "图名", "图幅", "备注" };

    public static bool PromptAndGenerate(Document document, IReadOnlyList<PlotJob> jobs, AppSettings settings, out string message)
    {
        message = "";
        var selected = jobs.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            message = "没有勾选任何图纸。";
            return false;
        }

        var editor = document.Editor;
        var pointResult = editor.GetPoint(new PromptPointOptions("\n指定图纸目录左上角基点: "));
        if (pointResult.Status != PromptStatus.OK)
        {
            message = "已取消生成目录。";
            return false;
        }

        Generate(document, selected, settings, pointResult.Value);
        message = $"已生成图纸目录，共 {selected.Count} 行。";
        return true;
    }

    public static void Generate(Document document, IReadOnlyList<PlotJob> jobs, AppSettings settings, Point3d origin)
    {
        var widths = GetWidths(settings);
        var rowHeight = settings.DirectoryRowHeight;
        var rowCount = jobs.Count + 1;
        var totalWidth = widths.Sum();
        var totalHeight = rowHeight * rowCount;

        using (document.LockDocument())
        using (var tr = document.Database.TransactionManager.StartTransaction())
        {
            var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
            var textStyleId = ResolveTextStyleId(tr, document.Database, settings.DirectoryTextStyleName);

            var x = origin.X;
            AddVerticalLine(space, tr, x, origin.Y, totalHeight);
            foreach (var width in widths)
            {
                x += width;
                AddVerticalLine(space, tr, x, origin.Y, totalHeight);
            }

            for (var i = 0; i <= rowCount; i++)
            {
                var y = origin.Y - i * rowHeight;
                AddHorizontalLine(space, tr, origin.X, y, totalWidth);
            }

            for (var col = 0; col < Headers.Length; col++)
            {
                AddCellText(space, tr, document.Database, textStyleId, Headers[col], origin, widths, col, 0, rowHeight, settings, bold: false);
            }

            for (var row = 0; row < jobs.Count; row++)
            {
                var job = jobs[row];
                var values = new[]
                {
                    (row + 1).ToString(),
                    job.DrawingNumber,
                    job.Title,
                    job.PaperName,
                    ""
                };

                for (var col = 0; col < values.Length; col++)
                {
                    AddCellText(space, tr, document.Database, textStyleId, values[col], origin, widths, col, row + 1, rowHeight, settings, bold: false);
                }
            }

            tr.Commit();
        }
    }

    public static bool PromptCellSizes(Document document, AppSettings settings, out AppSettings updated, out string message)
    {
        updated = settings;
        message = "";
        var editor = document.Editor;
        var labels = new[] { "序号", "图号", "图名", "图幅", "备注" };
        var widths = new double[labels.Length];
        double rowHeight = 0;

        for (var i = 0; i < labels.Length; i++)
        {
            var first = editor.GetPoint(new PromptPointOptions($"\n框选目录“{labels[i]}”单元格第一个角点: "));
            if (first.Status != PromptStatus.OK)
            {
                message = "已取消目录单元格设置。";
                return false;
            }

            var second = editor.GetCorner(new PromptCornerOptions($"\n框选目录“{labels[i]}”单元格对角点: ", first.Value));
            if (second.Status != PromptStatus.OK)
            {
                message = "已取消目录单元格设置。";
                return false;
            }

            widths[i] = Math.Abs(second.Value.X - first.Value.X);
            var height = Math.Abs(second.Value.Y - first.Value.Y);
            if (i == 0)
            {
                rowHeight = height;
            }
        }

        updated.DirectoryIndexWidth = Math.Max(1, widths[0]);
        updated.DirectoryNumberWidth = Math.Max(1, widths[1]);
        updated.DirectoryTitleWidth = Math.Max(1, widths[2]);
        updated.DirectoryPaperWidth = Math.Max(1, widths[3]);
        updated.DirectoryRemarkWidth = Math.Max(1, widths[4]);
        updated.DirectoryRowHeight = Math.Max(1, rowHeight);
        AppSettingsStore.Save(updated);
        message = "目录单元格尺寸已保存。";
        return true;
    }

    private static double[] GetWidths(AppSettings settings)
    {
        return new[]
        {
            settings.DirectoryIndexWidth,
            settings.DirectoryNumberWidth,
            settings.DirectoryTitleWidth,
            settings.DirectoryPaperWidth,
            settings.DirectoryRemarkWidth
        };
    }

    private static void AddVerticalLine(BlockTableRecord space, Transaction tr, double x, double topY, double height)
    {
        AddLine(space, tr, new Point3d(x, topY, 0), new Point3d(x, topY - height, 0));
    }

    private static void AddHorizontalLine(BlockTableRecord space, Transaction tr, double leftX, double y, double width)
    {
        AddLine(space, tr, new Point3d(leftX, y, 0), new Point3d(leftX + width, y, 0));
    }

    private static void AddLine(BlockTableRecord space, Transaction tr, Point3d start, Point3d end)
    {
        var line = new Line(start, end);
        space.AppendEntity(line);
        tr.AddNewlyCreatedDBObject(line, true);
    }

    private static void AddCellText(
        BlockTableRecord space,
        Transaction tr,
        Database db,
        ObjectId textStyleId,
        string text,
        Point3d origin,
        IReadOnlyList<double> widths,
        int column,
        int row,
        double rowHeight,
        AppSettings settings,
        bool bold)
    {
        var left = origin.X + widths.Take(column).Sum();
        var top = origin.Y - row * rowHeight;
        var width = widths[column];
        var center = new Point3d(left + width / 2.0, top - rowHeight / 2.0, 0);
        var height = GetTextHeight(text, width, rowHeight, settings);

        var dbText = new DBText
        {
            TextString = text ?? "",
            Height = height,
            Position = center,
            HorizontalMode = TextHorizontalMode.TextCenter,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = center
        };
        if (!textStyleId.IsNull)
        {
            dbText.TextStyleId = textStyleId;
        }

        space.AppendEntity(dbText);
        tr.AddNewlyCreatedDBObject(dbText, true);
        try
        {
            dbText.AdjustAlignment(db);
        }
        catch
        {
        }
    }

    private static double GetTextHeight(string text, double width, double rowHeight, AppSettings settings)
    {
        var byRow = rowHeight * settings.DirectoryTextHeightRatio;
        var charCount = Math.Max(1, (text ?? "").Length);
        var byWidth = width / Math.Max(3.0, charCount * 0.9);
        return Math.Max(1, Math.Min(byRow, byWidth));
    }

    private static ObjectId ResolveTextStyleId(Transaction tr, Database db, string? textStyleName)
    {
        if (string.IsNullOrWhiteSpace(textStyleName))
        {
            return ObjectId.Null;
        }

        try
        {
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            return table.Has(textStyleName) ? table[textStyleName] : ObjectId.Null;
        }
        catch
        {
            return ObjectId.Null;
        }
    }
}
