using System;
using System.Collections.Generic;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

public static class DirectoryTableGenerator
{
    public static bool PromptAndGenerate(Document document, IReadOnlyList<PlotJob> jobs, AppSettings settings, out string message)
    {
        message = "";
        var selected = jobs.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            message = "没有勾选任何图纸。";
            return false;
        }

        if (GetEnabledColumns(settings).Count == 0)
        {
            message = "图纸目录没有启用任何字段，请先在设置中启用目录列。";
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
        var columns = GetEnabledColumns(settings);
        if (columns.Count == 0)
        {
            return;
        }

        var widths = columns.Select(x => Math.Max(1, x.Width)).ToArray();
        var rowHeight = Math.Max(1, settings.DirectoryRowHeight);
        var headerRows = settings.DirectoryDrawHeader ? 1 : 0;
        var rowCount = jobs.Count + headerRows;
        var totalWidth = widths.Sum();
        var totalHeight = rowHeight * rowCount;

        using (document.LockDocument())
        using (var tr = document.Database.TransactionManager.StartTransaction())
        {
            var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
            var textStyleId = EnsureTextStyleId(tr, document.Database, settings.DirectoryTextStyleName);
            var layerName = EnsureLayer(tr, document.Database, settings.DirectoryLayerName);

            if (settings.DirectoryDrawGridLines)
            {
                DrawGrid(space, tr, origin, widths, rowHeight, rowCount, totalWidth, totalHeight, layerName, settings.DirectoryColorIndex);
            }

            if (settings.DirectoryDrawHeader)
            {
                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    AddCellText(
                        space, tr, document.Database, textStyleId, columns[columnIndex].Header,
                        origin, widths, columnIndex, 0, rowHeight, settings,
                        columns[columnIndex].Centered, layerName);
                }
            }

            for (var rowIndex = 0; rowIndex < jobs.Count; rowIndex++)
            {
                var drawingRow = rowIndex + headerRows;
                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    var column = columns[columnIndex];
                    var value = GetColumnValue(column.Key, jobs[rowIndex], rowIndex, settings);
                    AddCellText(
                        space, tr, document.Database, textStyleId, value,
                        origin, widths, columnIndex, drawingRow, rowHeight, settings,
                        column.Centered, layerName);
                }
            }

            tr.Commit();
        }
    }

    public static bool PromptColumnSize(
        Document document,
        AppSettings settings,
        string columnKey,
        out AppSettings updated,
        out string message)
    {
        updated = settings;
        var column = settings.DirectoryColumns.FirstOrDefault(x =>
            string.Equals(x.Key, columnKey, StringComparison.OrdinalIgnoreCase));
        if (column == null)
        {
            message = "没有找到要设置的目录字段。";
            return false;
        }

        var editor = document.Editor;
        var first = editor.GetPoint(new PromptPointOptions($"\n框选目录“{column.Header}”单元格第一个角点: "));
        if (first.Status != PromptStatus.OK)
        {
            message = "已取消目录列宽设置。";
            return false;
        }

        var second = editor.GetCorner(new PromptCornerOptions($"\n框选目录“{column.Header}”单元格对角点: ", first.Value));
        if (second.Status != PromptStatus.OK)
        {
            message = "已取消目录列宽设置。";
            return false;
        }

        var width = Math.Abs(second.Value.X - first.Value.X);
        if (width <= 1e-6)
        {
            message = "框选区域的宽度为 0，目录列宽未修改。";
            return false;
        }

        // 每行“图中交互”只负责当前列宽；目录行高由顶部独立按钮量取，避免两种参数互相覆盖。
        column.Width = width;
        AppSettingsStore.Save(updated);
        message = $"“{column.Header}”列宽已设置为 {width:0.##}。";
        return true;
    }

    public static bool PromptRowHeight(Document document, AppSettings settings, out AppSettings updated, out string message)
    {
        updated = settings;
        var options = new PromptDistanceOptions("\n在图中点取目录行高的两个端点: ")
        {
            UseDefaultValue = false,
            Only2d = true
        };
        var result = document.Editor.GetDistance(options);
        if (result.Status != PromptStatus.OK)
        {
            message = "已取消目录行高设置。";
            return false;
        }

        var height = Math.Abs(result.Value);
        if (height <= 1e-6)
        {
            message = "量取的目录行高为 0，设置未修改。";
            return false;
        }

        // 高度使用 CAD 两点量距结果，不依赖当前视图方向，适合水平或旋转后的目录模板。
        updated.DirectoryRowHeight = height;
        AppSettingsStore.Save(updated);
        message = $"目录行高已设置为 {height:0.##}。";
        return true;
    }

    /// <summary>
    /// 在当前活动图纸中点选文字，并把目录绘制所需的五项外观属性写入设置。
    /// 只保存可跨图纸持久化的值（颜色索引、字高、宽度因子、样式名、图层名），
    /// 绝不保存实体或文字样式的 ObjectId，避免切换/新建图纸后引用旧数据库对象。
    /// </summary>
    public static bool PromptTextAppearance(
        Document document,
        AppSettings settings,
        out AppSettings updated,
        out string message)
    {
        updated = settings;
        if (document == null)
        {
            message = "当前没有可用的 CAD 图纸。";
            return false;
        }

        try
        {
            var options = new PromptEntityOptions("\n点选一段文字作为图纸目录文字样式: ");
            options.SetRejectMessage("\n请选择单行文字、多行文字或属性文字。");
            // AttributeReference/AttributeDefinition 均继承 DBText；false 表示允许其派生类型。
            options.AddAllowedClass(typeof(DBText), false);
            options.AddAllowedClass(typeof(MText), false);

            var selection = document.Editor.GetEntity(options);
            if (selection.Status != PromptStatus.OK)
            {
                message = "已取消点选目录文字样式。";
                return false;
            }

            using var tr = document.Database.TransactionManager.StartTransaction();
            if (tr.GetObject(selection.ObjectId, OpenMode.ForRead, false) is not Entity entity)
            {
                message = "选择的对象不是有效文字。";
                return false;
            }

            double textHeight;
            double widthFactor;
            ObjectId textStyleId;
            if (entity is DBText dbText)
            {
                textHeight = dbText.Height;
                widthFactor = dbText.WidthFactor;
                textStyleId = dbText.TextStyleId;
            }
            else if (entity is MText mText)
            {
                textHeight = mText.TextHeight;
                textStyleId = mText.TextStyleId;
                widthFactor = 1d;
                if (!textStyleId.IsNull
                    && tr.GetObject(textStyleId, OpenMode.ForRead, false) is TextStyleTableRecord mTextStyle
                    && mTextStyle.XScale > 0)
                {
                    // MText 没有独立 WidthFactor，目录使用其文字样式的 XScale 作为对应宽度因子。
                    widthFactor = mTextStyle.XScale;
                }
            }
            else
            {
                message = "请选择单行文字、多行文字或属性文字。";
                return false;
            }

            var textStyleName = "";
            if (!textStyleId.IsNull
                && tr.GetObject(textStyleId, OpenMode.ForRead, false) is TextStyleTableRecord textStyle)
            {
                textStyleName = textStyle.Name ?? "";
            }

            settings.DirectoryColorIndex = Math.Max(0, Math.Min(256, entity.ColorIndex));
            if (textHeight > 1e-6)
            {
                settings.DirectoryTextHeight = textHeight;
            }
            if (widthFactor > 1e-6)
            {
                settings.DirectoryTextWidthFactor = widthFactor;
            }
            settings.DirectoryTextStyleName = textStyleName;
            settings.DirectoryLayerName = string.IsNullOrWhiteSpace(entity.Layer) ? "0" : entity.Layer;

            tr.Commit();
            AppSettingsStore.Save(settings);
            updated = settings;
            message =
                $"已从所选文字读取目录样式：颜色 {settings.DirectoryColorIndex}，" +
                $"字高 {settings.DirectoryTextHeight:0.##}，宽度因子 {settings.DirectoryTextWidthFactor:0.##}，" +
                $"文字样式“{(string.IsNullOrWhiteSpace(settings.DirectoryTextStyleName) ? "默认" : settings.DirectoryTextStyleName)}”，" +
                $"图层“{settings.DirectoryLayerName}”。";
            return true;
        }
        catch (Exception ex)
        {
            // 当前图纸可能刚被关闭或切换；只报告失败，不写入半套设置。
            message = "点选目录文字样式失败，请确认当前活动图纸仍然打开：" + ex.Message;
            return false;
        }
    }

    private static List<DirectoryColumnSetting> GetEnabledColumns(AppSettings settings)
    {
        return (settings.DirectoryColumns ?? new List<DirectoryColumnSetting>())
            .Where(x => x.Enabled && x.Width > 0)
            .Select(x => x.Clone())
            .ToList();
    }

    private static string GetColumnValue(string key, PlotJob job, int rowIndex, AppSettings settings)
    {
        // 这里的字段键与 TitleBlockScanner 写入 PlotJob 的识别结果保持一一对应。
        return key switch
        {
            "Sequence" => (rowIndex + 1).ToString(),
            "DrawingNumber" => job.DrawingNumber,
            "Title" => job.Title,
            "PaperName" => FileNameSanitizer.NormalizeLongPaperFraction(
                OutputPaperNameResolver.Resolve(job, settings.LongPaperSnapToleranceMm),
                settings.LongPaperNameFormat),
            "Date" => job.Date,
            "Revision" => job.Revision,
            "Phase" => job.Phase,
            "Info1" => job.Info1,
            "Info2" => job.Info2,
            _ => ""
        } ?? "";
    }

    private static void DrawGrid(
        BlockTableRecord space,
        Transaction tr,
        Point3d origin,
        IReadOnlyList<double> widths,
        double rowHeight,
        int rowCount,
        double totalWidth,
        double totalHeight,
        string layerName,
        int colorIndex)
    {
        var x = origin.X;
        AddVerticalLine(space, tr, x, origin.Y, totalHeight, layerName, colorIndex);
        foreach (var width in widths)
        {
            x += width;
            AddVerticalLine(space, tr, x, origin.Y, totalHeight, layerName, colorIndex);
        }

        for (var rowIndex = 0; rowIndex <= rowCount; rowIndex++)
        {
            var y = origin.Y - rowIndex * rowHeight;
            AddHorizontalLine(space, tr, origin.X, y, totalWidth, layerName, colorIndex);
        }
    }

    private static void AddVerticalLine(
        BlockTableRecord space,
        Transaction tr,
        double x,
        double topY,
        double height,
        string layerName,
        int colorIndex)
    {
        AddLine(space, tr, new Point3d(x, topY, 0), new Point3d(x, topY - height, 0), layerName, colorIndex);
    }

    private static void AddHorizontalLine(
        BlockTableRecord space,
        Transaction tr,
        double leftX,
        double y,
        double width,
        string layerName,
        int colorIndex)
    {
        AddLine(space, tr, new Point3d(leftX, y, 0), new Point3d(leftX + width, y, 0), layerName, colorIndex);
    }

    private static void AddLine(
        BlockTableRecord space,
        Transaction tr,
        Point3d start,
        Point3d end,
        string layerName,
        int colorIndex)
    {
        var line = new Line(start, end)
        {
            Layer = layerName,
            ColorIndex = colorIndex
        };
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
        bool centered,
        string layerName)
    {
        var left = origin.X + widths.Take(column).Sum();
        var top = origin.Y - row * rowHeight;
        var width = widths[column];
        var centerY = top - rowHeight / 2.0;
        var horizontalPadding = Math.Min(width * 0.05, rowHeight * 0.25);
        var insertion = centered
            ? new Point3d(left + width / 2.0, centerY, 0)
            : new Point3d(left + horizontalPadding, centerY, 0);

        var dbText = new DBText
        {
            TextString = text ?? "",
            Height = GetTextHeight(text ?? "", width, rowHeight, settings),
            WidthFactor = settings.DirectoryTextWidthFactor,
            Position = insertion,
            HorizontalMode = centered ? TextHorizontalMode.TextCenter : TextHorizontalMode.TextLeft,
            VerticalMode = TextVerticalMode.TextVerticalMid,
            AlignmentPoint = insertion,
            Layer = layerName,
            ColorIndex = settings.DirectoryColorIndex
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
        var configured = Math.Max(1, settings.DirectoryTextHeight);
        var byRow = rowHeight * 0.8;
        var charCount = Math.Max(1, (text ?? "").Length);
        var byWidth = width * 0.9 / Math.Max(1, charCount * settings.DirectoryTextWidthFactor);
        return Math.Max(1, Math.Min(configured, Math.Min(byRow, byWidth)));
    }

    private static string EnsureLayer(Transaction tr, Database db, string? configuredName)
    {
        var layerName = string.IsNullOrWhiteSpace(configuredName) ? "0" : configuredName!.Trim();
        try
        {
            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (table.Has(layerName))
            {
                return layerName;
            }

            // 用户输入的新图层在生成目录时自动创建，只影响当前图纸，不修改 CAD 全局设置。
            table.UpgradeOpen();
            var record = new LayerTableRecord { Name = layerName };
            table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return layerName;
        }
        catch
        {
            return "0";
        }
    }

    private static ObjectId EnsureTextStyleId(Transaction tr, Database db, string? textStyleName)
    {
        if (string.IsNullOrWhiteSpace(textStyleName))
        {
            return ObjectId.Null;
        }

        try
        {
            var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            var styleName = textStyleName!.Trim();
            if (table.Has(styleName))
            {
                return table[styleName];
            }

            if (!string.Equals(styleName, "宋体", StringComparison.OrdinalIgnoreCase))
            {
                return ObjectId.Null;
            }

            // 默认”宋体”样式缺失时仅在当前图纸中创建，不修改 CAD 全局模板或用户配置。
            table.UpgradeOpen();
            var record = new TextStyleTableRecord
            {
                Name = styleName,
                FileName = "simsun.ttc"
            };
            var id = table.Add(record);
            tr.AddNewlyCreatedDBObject(record, true);
            return id;
        }
        catch
        {
            return ObjectId.Null;
        }
    }
}
