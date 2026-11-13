using System;
using System.Collections.Generic;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
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
            message = "图纸目录没有启用任何字段，请先在设置中勾选目录列。";
            return false;
        }

        var editor = document.Editor;
        var pointResult = editor.GetPoint(new PromptPointOptions("\n指定图纸目录左上角基点: "));
        if (pointResult.Status != PromptStatus.OK)
        {
            message = "已取消生成目录。";
            return false;
        }

        double breakHeight = 0;
        if (settings.DirectoryAskTableBreak)
        {
            var distanceOptions = new PromptDistanceOptions("\n指定单栏最大高度以向右分栏(回车=不分栏): ")
            {
                AllowNone = true,
                Only2d = true,
                UseDefaultValue = false
            };
            var distanceResult = editor.GetDistance(distanceOptions);
            if (distanceResult.Status == PromptStatus.Cancel)
            {
                message = "已取消生成目录。";
                return false;
            }

            if (distanceResult.Status == PromptStatus.OK)
            {
                breakHeight = Math.Abs(distanceResult.Value);
                if (breakHeight <= 1e-6)
                {
                    breakHeight = 0;
                }
            }
        }

        // GetPoint 返回 UCS，Table.Position 是 WCS；这里换算后再生成，避免非世界坐标系下点位错开。
        Generate(document, selected, settings, ToWcs(document.Editor, pointResult.Value), breakHeight);
        if (breakHeight > 1e-6)
        {
            message = $"已生成图纸目录，共 {selected.Count} 行（已按高度 {breakHeight:0.##} 向右分栏，右栏重复表头）。";
        }
        else
        {
            message = $"已生成图纸目录，共 {selected.Count} 行。";
        }

        return true;
    }

    public static void Generate(
        Document document,
        IReadOnlyList<PlotJob> jobs,
        AppSettings settings,
        Point3d origin,
        double breakHeight = 0)
    {
        var columns = GetEnabledColumns(settings);
        if (columns.Count == 0)
        {
            return;
        }

        var headerRows = settings.DirectoryDrawHeader ? 1 : 0;
        var totalRows = jobs.Count + headerRows;
        var totalCols = columns.Count;
        var defaultRowHeight = Math.Max(1, settings.DirectoryRowHeight);
        var textHeight = Math.Max(1, settings.DirectoryTextHeight);
        var widthFactor = settings.DirectoryTextWidthFactor > 1e-6 ? settings.DirectoryTextWidthFactor : 0.7;

        using (document.LockDocument())
        using (var tr = document.Database.TransactionManager.StartTransaction())
        {
            var space = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForWrite);
            var textStyleId = EnsureTextStyleId(tr, document.Database, settings.DirectoryTextStyleName, widthFactor);
            var layerName = EnsureLayer(tr, document.Database, settings.DirectoryLayerName);

            // 参考 TAD：创建原生 Table 实体并初始化。
            // 先按当前 UCS 对齐轴向，再设插入点，避免改 Normal 时把位置带偏。
            var tb = new Table();
            tb.SetDatabaseDefaults();
            AlignTableToUcs(tb, document.Editor);
            tb.Position = origin;
            tb.Layer = layerName;

            if (settings.DirectoryColorIndex > 0 && settings.DirectoryColorIndex <= 256)
            {
                tb.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)settings.DirectoryColorIndex);
            }

            tb.SetSize(totalRows, totalCols);
            tb.GenerateLayout();

            // 去掉默认带合并的标题单元格，改成普通表头行
            try
            {
                if (tb.Cells[0, 0].IsMerged == true)
                {
                    tb.UnmergeCells(tb.Cells[0, 0].GetMergeRange());
                }
            }
            catch { }

            // 写入表头内容
            if (settings.DirectoryDrawHeader)
            {
                for (var col = 0; col < totalCols; col++)
                {
                    ApplyCellContent(
                        tb.Cells[0, col],
                        ToCadDirectoryText(columns[col].Header),
                        textHeight,
                        widthFactor,
                        textStyleId,
                        columns[col].Centered);
                }
            }

            // 写入图纸数据行
            for (var r = 0; r < jobs.Count; r++)
            {
                var tableRow = r + headerRows;
                for (var col = 0; col < totalCols; col++)
                {
                    var column = columns[col];
                    var value = GetColumnValue(column.Key, jobs[r], r, settings);
                    ApplyCellContent(
                        tb.Cells[tableRow, col],
                        ToCadDirectoryText(value),
                        textHeight,
                        widthFactor,
                        textStyleId,
                        column.Centered);
                }
            }

            // 单元格边距（相对字高）——先设边距再定列宽/行高，便于按列宽测算换行高度
#pragma warning disable 0618
            double horizontalMargin = textHeight * 0.2;
            double verticalMargin = textHeight * 0.1;
            try
            {
                tb.HorizontalCellMargin = horizontalMargin;
                tb.VerticalCellMargin = verticalMargin;
            }
            catch { }
#pragma warning restore 0618

            // 严格使用设置列宽（图面单位），不再按文字实测撑宽
            for (var c = 0; c < totalCols; c++)
            {
                tb.Columns[c].Width = Math.Max(1, columns[c].Width);
            }

            // 按列宽测算换行后所需行高（ZWCAD 的 Row.MinimumHeight 只读，不能靠它长高）
            for (var r = 0; r < totalRows; r++)
            {
                var needed = defaultRowHeight;
                for (var c = 0; c < totalCols; c++)
                {
                    var cellText = tb.Cells[r, c].TextString;
                    if (string.IsNullOrEmpty(cellText))
                    {
                        continue;
                    }

                    var contentWidth = Math.Max(1, tb.Columns[c].Width - horizontalMargin * 2);
                    using (var mt = new MText())
                    {
                        mt.Contents = cellText;
                        mt.TextHeight = textHeight;
                        if (!textStyleId.IsNull)
                        {
                            mt.TextStyleId = textStyleId;
                        }

                        mt.Width = contentWidth;
                        mt.LineSpacingFactor = 1.0;
                        var actH = mt.ActualHeight + verticalMargin * 2;
                        if (actH > needed)
                        {
                            needed = actH;
                        }
                    }
                }

                tb.Rows[r].Height = needed;
            }

            // 可选分栏：向右排第二栏，并重复顶行表头
            if (breakHeight > 1e-6)
            {
                tb.BreakEnabled = true;
                tb.BreakOptions =
                    TableBreakOptions.EnableBreaking |
                    TableBreakOptions.RepeatTopLabels;
                tb.BreakFlowDirection = TableBreakFlowDirection.Right;
                tb.SetBreakHeight(0, breakHeight);
                try
                {
                    tb.SetBreakSpacing(Math.Max(textHeight, defaultRowHeight * 0.5));
                }
                catch { }
            }
            else
            {
                tb.BreakEnabled = false;
            }

            // 不画网格线时隐藏边框
            if (!settings.DirectoryDrawGridLines)
            {
                try
                {
                    for (var r = 0; r < totalRows; r++)
                    {
                        for (var c = 0; c < totalCols; c++)
                        {
                            tb.Cells[r, c].Borders.Top.IsVisible = false;
                            tb.Cells[r, c].Borders.Bottom.IsVisible = false;
                            tb.Cells[r, c].Borders.Left.IsVisible = false;
                            tb.Cells[r, c].Borders.Right.IsVisible = false;
                        }
                    }
                }
                catch { }
            }

            tb.GenerateLayout();
            try { tb.RecomputeTableBlock(true); } catch { }

            // 换行后若仍低于保底行高则抬回；Recompute 后以实际高度为准，避免裁切
            try
            {
                for (var r = 0; r < totalRows; r++)
                {
                    if (tb.Rows[r].Height < defaultRowHeight - 1e-6)
                    {
                        tb.Rows[r].Height = defaultRowHeight;
                    }
                }

                tb.RecomputeTableBlock(true);
            }
            catch { }

            space.AppendEntity(tb);
            tr.AddNewlyCreatedDBObject(tb, true);

            tr.Commit();
        }
    }

    private static Point3d ToWcs(Editor editor, Point3d pointUcs)
    {
        return pointUcs.TransformBy(GetUcsToWcs(editor));
    }

    private static void AlignTableToUcs(Table table, Editor editor)
    {
        var ucsToWcs = GetUcsToWcs(editor);
        if (ucsToWcs.IsEqualTo(Matrix3d.Identity))
        {
            return;
        }

        var axes = ucsToWcs.CoordinateSystem3d;
        table.Normal = axes.Zaxis;
        table.Direction = axes.Xaxis;
    }

    private static Matrix3d GetUcsToWcs(Editor editor)
    {
        try
        {
            return editor.CurrentUserCoordinateSystem;
        }
        catch
        {
            return Matrix3d.Identity;
        }
    }

    private static void ApplyCellContent(
        Cell cell,
        string rawText,
        double textHeight,
        double widthFactor,
        ObjectId textStyleId,
        bool centered)
    {
        cell.TextString = FormatTextWithWidthFactor(rawText, widthFactor);
        cell.TextHeight = textHeight;
        cell.Alignment = centered ? CellAlignment.MiddleCenter : CellAlignment.MiddleLeft;
        try
        {
            cell.ContentLayout = CellContentLayout.Flow;
        }
        catch { }

        if (!textStyleId.IsNull)
        {
            cell.TextStyleId = textStyleId;
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
            message = "框选得到的宽度为 0，目录列宽未修改。";
            return false;
        }

        column.Width = width;
        if (!TrySaveSettings(updated, out message))
        {
            return false;
        }

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
            message = "点取的目录行高为 0，设置未修改。";
            return false;
        }

        updated.DirectoryRowHeight = height;
        if (!TrySaveSettings(updated, out message))
        {
            return false;
        }

        message = $"目录行高已设置为 {height:0.##}。";
        return true;
    }

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
            var options = new PromptEntityOptions("\n点选一个文字作为图纸目录文字样式: ");
            options.SetRejectMessage("\n请选择单行文字、多行文字或属性文字。");
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
                $"已从选中文字读取目录样式：颜色 {settings.DirectoryColorIndex}，" +
                $"字高 {settings.DirectoryTextHeight:0.##}，宽度因子 {settings.DirectoryTextWidthFactor:0.##}，" +
                $"文字样式“{(string.IsNullOrWhiteSpace(settings.DirectoryTextStyleName) ? "默认" : settings.DirectoryTextStyleName)}”，" +
                $"图层“{settings.DirectoryLayerName}”。";
            return true;
        }
        catch (Exception ex)
        {
            message = "点选目录文字样式失败，请确认当前活动图纸然后重试：" + ex.Message;
            return false;
        }
    }

    /// <summary>持久化目录设置；失败时返回提示文本，由调用方弹窗告知用户。</summary>
    private static bool TrySaveSettings(AppSettings settings, out string message)
    {
        try
        {
            AppSettingsStore.Save(settings);
            message = "";
            return true;
        }
        catch (System.Exception ex)
        {
            message = AppSettingsStore.FormatSaveError(ex);
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
        return key switch
        {
            "Sequence" => (rowIndex + 1).ToString(),
            // 优先用识别结果（图框属性或三点图签的 Cad* 字段），避免清单里仍是序号占位。
            "DrawingNumber" => FirstNonEmpty(job.CadDrawingNumber, job.DrawingNumber),
            "Title" => FirstNonEmpty(job.CadTitle, job.Title),
            "PaperName" => ResolveDirectoryPaperName(job, settings),
            "Date" => FirstNonEmpty(job.CadDate, job.Date),
            "Revision" => FirstNonEmpty(job.CadRevision, job.Revision),
            "Phase" => FirstNonEmpty(job.CadPhase, job.Phase),
            "Info1" => FirstNonEmpty(job.CadInfo1, job.Info1),
            "Info2" => FirstNonEmpty(job.CadInfo2, job.Info2),
            "ProjectName" => job.ProjectName ?? "",
            "DrawingScale" => job.DrawingScale ?? "",
            _ => ""
        } ?? "";
    }

    private static string ResolveDirectoryPaperName(PlotJob job, AppSettings settings)
    {
        // 三点图签取到的图幅文字优先；否则与图框型一样按识别到的纸张尺寸命名。
        if (!string.IsNullOrWhiteSpace(job.SheetSize))
        {
            return job.SheetSize.Trim();
        }

        return FileNameSanitizer.NormalizeLongPaperFraction(
            OutputPaperNameResolver.Resolve(job, settings.LongPaperSnapToleranceMm),
            settings.LongPaperNameFormat);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value ?? "";
            }
        }

        return "";
    }

    private static string ToCadDirectoryText(string value)
    {
        return (value ?? "").Replace('\u2215', '/');
    }

    /// <summary>
    /// 为表格内的 MText 内容加宽度因子格式码，确保单元格严格执行该因子。
    /// </summary>
    private static string FormatTextWithWidthFactor(string text, double factor)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return $"\\W{factor:0.##};{text}";
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

    private static ObjectId EnsureTextStyleId(Transaction tr, Database db, string? textStyleName, double widthFactor)
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

            table.UpgradeOpen();
            var record = new TextStyleTableRecord
            {
                Name = styleName,
                FileName = "simsun.ttc",
                XScale = widthFactor // TextStyle 层面同步写入 XScale
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