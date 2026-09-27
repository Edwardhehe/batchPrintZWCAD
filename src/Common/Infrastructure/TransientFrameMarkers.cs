using System;
using System.Collections.Generic;
#if AUTOCAD
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
// AutoCAD 中 DatabaseServices 和 GraphicsInterface 同样存在 Polyline，必须明确使用数据库实体类型。
using Polyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
#else
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.GraphicsInterface;
// ZWCAD 中 DatabaseServices 和 GraphicsInterface 都有 Polyline，消歧义。
using Polyline = ZwSoft.ZwCAD.DatabaseServices.Polyline;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 新增图框流程中的临时红色标识（矩形框 + 两条对角线 + 可选居中文字）。
/// 基于 TransientManager 的临时图素，不写入图形数据库，Dispose/Clear 时全部删除。
/// </summary>
public sealed class TransientFrameMarkers : IDisposable
{
    // TransientManager 以 (mode, subSystemId) 标识一组临时图素；
    // 每个字段使用不同的 subSystemId，才能单独替换/清除某个字段的标识。
    private const int FirstSubSystemId = 128;

    private static readonly Color MarkerColor = Color.FromColorIndex(ColorMethod.ByAci, 1);

    private readonly Editor _editor;
    private readonly Database? _database;
    // 字段名是中文：显式指定能显示中文的已有样式，不依赖宿主对非数据库 DBText 的默认样式（可能是无大字体的 txt.shx）。
    private readonly ObjectId _labelTextStyleId;
    private readonly Dictionary<string, int> _subSystemIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entity>> _markers = new(StringComparer.Ordinal);
    private int _nextSubSystemId = FirstSubSystemId;

    public TransientFrameMarkers(Editor editor)
    {
        _editor = editor;
        try
        {
            _database = editor.Document?.Database;
        }
        catch
        {
            _database = null;
        }

        _labelTextStyleId = ResolveLabelTextStyle(_database);
    }

    /// <summary>
    /// 以世界坐标两角点设置某个标识（红色矩形 + 对角线 + 居中文字）。
    /// 同名字段重复调用时先移除旧标识，实现"重新框选即替换"。
    /// </summary>
    /// <param name="refresh">为 false 时跳过刷屏，便于调用方批量改完后只刷新一次。</param>
    public void SetBox(string key, Point3d corner1, Point3d corner2, string? label, bool refresh = true)
    {
        Remove(key, refresh: false);

        var entities = BuildBoxEntities(corner1, corner2, label, _database, _labelTextStyleId);
        var subSystemId = GetSubSystemId(key);
        var transientManager = TransientManager.CurrentTransientManager;
        foreach (var entity in entities)
        {
            // DirectTopmost 确保临时标识在 CAD 显示刷新（如对话框隐藏/显示切换）时不会消失，
            // 仅在显式调用 EraseTransients 或 Dispose 时才清除。
            transientManager.AddTransient(entity, TransientDrawingMode.DirectTopmost, subSystemId, new IntegerCollection());
        }

        _markers[key] = entities;
        if (refresh)
        {
            RefreshDisplay();
        }
    }

    /// <summary>
    /// 移除某个字段的临时标识（如点击"清除"）。
    /// </summary>
    /// <param name="refresh">为 false 时跳过刷屏，便于调用方批量改完后只刷新一次。</param>
    public void Remove(string key, bool refresh = true)
    {
        if (!_markers.TryGetValue(key, out var entities))
        {
            return;
        }

        TransientManager.CurrentTransientManager.EraseTransients(
            TransientDrawingMode.DirectTopmost, _subSystemIds[key], new IntegerCollection());
        foreach (var entity in entities)
        {
            entity.Dispose();
        }

        _markers.Remove(key);
        if (refresh)
        {
            RefreshDisplay();
        }
    }

    /// <summary>
    /// 刷新 CAD 视图。首次弹出模态窗口时可要求 Regen，确保刚创建的临时红框在窗口接管消息循环后立即显示；
    /// 普通字段更新只做 UpdateScreen，避免每次框选都触发较重的重生成。
    /// </summary>
    public void RefreshDisplay(bool regenerate = false)
    {
        try
        {
            _editor.UpdateScreen();
            if (regenerate)
            {
                _editor.Regen();
            }
        }
        catch
        {
            // 临时标识刷新失败不应中断图框录入；后续 CAD 视图刷新仍会显示已注册的 transient。
        }
    }

    /// <summary>
    /// 删除全部临时标识。图框保存或窗口关闭后必须调用。
    /// </summary>
    public void Clear()
    {
        foreach (var key in new List<string>(_markers.Keys))
        {
            Remove(key, refresh: false);
        }

        RefreshDisplay();
    }

    public void Dispose() => Clear();

    private int GetSubSystemId(string key)
    {
        if (!_subSystemIds.TryGetValue(key, out var id))
        {
            id = _nextSubSystemId++;
            _subSystemIds[key] = id;
        }

        return id;
    }

    private static List<Entity> BuildBoxEntities(Point3d corner1, Point3d corner2, string? label, Database? database, ObjectId textStyleId)
    {
        var minX = Math.Min(corner1.X, corner2.X);
        var minY = Math.Min(corner1.Y, corner2.Y);
        var maxX = Math.Max(corner1.X, corner2.X);
        var maxY = Math.Max(corner1.Y, corner2.Y);
        var z = (corner1.Z + corner2.Z) / 2;

        var rectangle = new Polyline();
        rectangle.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
        rectangle.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
        rectangle.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
        rectangle.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);
        rectangle.Closed = true;
        rectangle.Color = MarkerColor;

        var diagonal1 = new Line(new Point3d(minX, minY, z), new Point3d(maxX, maxY, z)) { Color = MarkerColor };
        var diagonal2 = new Line(new Point3d(minX, maxY, z), new Point3d(maxX, minY, z)) { Color = MarkerColor };

        var entities = new List<Entity> { rectangle, diagonal1, diagonal2 };

        if (!string.IsNullOrWhiteSpace(label))
        {
            var width = maxX - minX;
            var height = maxY - minY;
            // 文字高度随框选大小自适应：取框高的 35% 且不超过框宽/字数。
            // 不再设绝对上限，避免模型空间大比例图框里字段名小到看不见。
            var textHeight = Math.Min(height * 0.35, width / Math.Max(label!.Length, 1));
            if (textHeight > 1e-6)
            {
                var center = new Point3d((minX + maxX) / 2, (minY + maxY) / 2, z);
                var text = new DBText
                {
                    TextString = label,
                    Height = textHeight,
                    HorizontalMode = TextHorizontalMode.TextCenter,
                    VerticalMode = TextVerticalMode.TextVerticalMid,
                    Color = MarkerColor
                };
                if (!textStyleId.IsNull)
                {
                    text.TextStyleId = textStyleId;
                }

                // AlignmentPoint 必须在 HorizontalMode/VerticalMode 之后设置，
                // 否则部分 CAD 引擎会回退到 Position（左下角）对齐。
                text.AlignmentPoint = center;
                text.Position = center;
                if (database != null)
                {
                    try
                    {
                        // 临时文字不在数据库中，按指定数据库的样式计算居中后的 Position。
                        text.AdjustAlignment(database);
                    }
                    catch
                    {
                        // 个别宿主不支持对非数据库文字调整对齐时，保留 Position=AlignmentPoint 的旧行为。
                    }
                }

                entities.Add(text);
            }
        }

        return entities;
    }

    /// <summary>
    /// 只读挑选一个能显示中文的已有文字样式，不在用户图纸中新建或修改样式（不产生 DBMOD/UNDO/残留样式）。
    /// 顺序：当前样式（TrueType 或带大字体）→ 批打覆盖层已建的宋体样式 → 常见中文 TrueType → 带大字体的 SHX → 任意 TrueType → 当前样式。
    /// </summary>
    private static ObjectId ResolveLabelTextStyle(Database? database)
    {
        if (database == null)
        {
            return ObjectId.Null;
        }

        try
        {
            using var tr = database.TransactionManager.StartTransaction();
            var table = (TextStyleTable)tr.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            var currentId = database.Textstyle;
            var result = ObjectId.Null;

            if (!currentId.IsNull
                && tr.GetObject(currentId, OpenMode.ForRead) is TextStyleTableRecord current
                && (IsTrueTypeStyle(current) || HasBigFont(current)))
            {
                result = currentId;
            }
            else if (table.Has(TemporarySequenceOverlay.TextStyleName))
            {
                result = table[TemporarySequenceOverlay.TextStyleName];
            }
            else
            {
                var bigFontId = ObjectId.Null;
                var trueTypeId = ObjectId.Null;
                foreach (ObjectId id in table)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not TextStyleTableRecord record
                        || record.IsErased
                        || record.IsShapeFile
                        || string.IsNullOrEmpty(record.Name))
                    {
                        continue;
                    }

                    if (IsChineseTrueTypeStyle(record))
                    {
                        result = id;
                        break;
                    }

                    if (bigFontId.IsNull && HasBigFont(record))
                    {
                        bigFontId = id;
                    }

                    if (trueTypeId.IsNull && IsTrueTypeStyle(record))
                    {
                        trueTypeId = id;
                    }
                }

                if (result.IsNull)
                {
                    result = !bigFontId.IsNull ? bigFontId : !trueTypeId.IsNull ? trueTypeId : currentId;
                }
            }

            tr.Commit();
            return result;
        }
        catch
        {
            // 取样式失败时退回旧行为（不指定样式）。
            return ObjectId.Null;
        }
    }

    private static readonly string[] ChineseFontKeywords =
    {
        "simsun", "nsimsun", "simhei", "simfang", "simkai", "msyh", "fangsong", "kaiti",
        "stsong", "stfangso", "stkaiti", "stheiti", "宋", "黑", "仿宋", "楷", "雅黑"
    };

    private static bool IsTrueTypeStyle(TextStyleTableRecord record)
    {
        var fileName = record.FileName ?? "";
        return fileName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(GetTypeFace(record));
    }

    private static bool IsChineseTrueTypeStyle(TextStyleTableRecord record)
    {
        if (!IsTrueTypeStyle(record))
        {
            return false;
        }

        var names = (record.FileName ?? "") + "|" + GetTypeFace(record);
        foreach (var keyword in ChineseFontKeywords)
        {
            if (names.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasBigFont(TextStyleTableRecord record)
        => !string.IsNullOrWhiteSpace(record.BigFontFileName);

    private static string GetTypeFace(TextStyleTableRecord record)
    {
        try
        {
            return record.Font.TypeFace ?? "";
        }
        catch
        {
            return "";
        }
    }
}
