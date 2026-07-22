using System;
using System.Collections.Generic;
#if AUTOCAD
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
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
    private readonly Dictionary<string, int> _subSystemIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entity>> _markers = new(StringComparer.Ordinal);
    private int _nextSubSystemId = FirstSubSystemId;

    public TransientFrameMarkers(Editor editor)
    {
        _editor = editor;
    }

    /// <summary>
    /// 以世界坐标两角点设置某个标识（红色矩形 + 对角线 + 居中文字）。
    /// 同名字段重复调用时先移除旧标识，实现"重新框选即替换"。
    /// </summary>
    public void SetBox(string key, Point3d corner1, Point3d corner2, string? label)
    {
        Remove(key);

        var entities = BuildBoxEntities(corner1, corner2, label);
        var subSystemId = GetSubSystemId(key);
        var transientManager = TransientManager.CurrentTransientManager;
        foreach (var entity in entities)
        {
            // DirectTopmost 确保临时标识在 CAD 显示刷新（如对话框隐藏/显示切换）时不会消失，
            // 仅在显式调用 EraseTransients 或 Dispose 时才清除。
            transientManager.AddTransient(entity, TransientDrawingMode.DirectTopmost, subSystemId, new IntegerCollection());
        }

        _markers[key] = entities;
        _editor.UpdateScreen();
    }

    /// <summary>
    /// 移除某个字段的临时标识（如点击"清除"）。
    /// </summary>
    public void Remove(string key)
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
        _editor.UpdateScreen();
    }

    /// <summary>
    /// 删除全部临时标识。图框保存或窗口关闭后必须调用。
    /// </summary>
    public void Clear()
    {
        foreach (var key in new List<string>(_markers.Keys))
        {
            Remove(key);
        }
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

    private static List<Entity> BuildBoxEntities(Point3d corner1, Point3d corner2, string? label)
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
            // 文字高度随框选大小自适应，取框高的 35% 且不超过框宽/字数，最大 120 单位。
            var textHeight = Math.Min(Math.Min(height * 0.35, width / Math.Max(label.Length, 1)), 120d);
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
                // AlignmentPoint 必须在 HorizontalMode/VerticalMode 之后设置，
                // 否则部分 CAD 引擎会回退到 Position（左下角）对齐。
                text.AlignmentPoint = center;
                text.Position = center;
                entities.Add(text);
            }
        }

        return entities;
    }
}
