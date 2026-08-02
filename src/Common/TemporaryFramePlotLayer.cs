using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 正式打印事务内的图框外边界临时移层逻辑。
/// 调用方必须让所在事务在绘图引擎结束后回滚而不是提交；这样成功、失败和取消都会由 CAD 事务原子恢复，
/// 不留下实体图层变化、临时图层或“图纸已修改”状态。
/// </summary>
internal static class TemporaryFramePlotLayer
{
    internal const string LayerName = "LA-临时不打印层";

    internal static bool Apply(Transaction tr, Database db, PlotJob job)
    {
        var handles = job.FrameBoundaryHandles?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? Array.Empty<string>();
        if (handles.Length == 0)
        {
            return false;
        }

        var entities = new List<Entity>();
        foreach (var value in handles)
        {
            if (!long.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rawHandle))
            {
                continue;
            }

            try
            {
                var id = db.GetObjectId(false, new Handle(rawHandle), 0);
                if (!id.IsNull
                    && id.IsValid
                    && !id.IsErased
                    && tr.GetObject(id, OpenMode.ForRead, false) is Entity entity)
                {
                    entities.Add(entity);
                }
            }
            catch
            {
                // 图纸被编辑后个别句柄可能失效；其余有效边界仍应继续处理。
            }
        }

        if (entities.Count == 0)
        {
            throw new InvalidOperationException("已识别的图框外边框实体已失效，请重新扫描图纸后再打印。");
        }

        // 原本就在不打印层上的边框无需移动，也不应为了本功能改变其图层归属。
        entities = entities
            .Where(entity => tr.GetObject(entity.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer
                || layer.IsPlottable)
            .ToList();
        if (entities.Count == 0)
        {
            return false;
        }

        var targetLayerId = EnsureTemporaryLayer(tr, db);

        // 锁定图层上的实体不能升级为写；这里只在同一回滚事务内临时解锁，事务退出会自动恢复。
        foreach (var layerId in entities.Select(entity => entity.LayerId).Distinct())
        {
            if (tr.GetObject(layerId, OpenMode.ForRead, false) is LayerTableRecord layer && layer.IsLocked)
            {
                layer.UpgradeOpen();
                layer.IsLocked = false;
            }
        }

        foreach (var entity in entities)
        {
            entity.UpgradeOpen();
            entity.LayerId = targetLayerId;
        }

        return true;
    }

    private static ObjectId EnsureTemporaryLayer(Transaction tr, Database db)
    {
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (table.Has(LayerName))
        {
            var existing = (LayerTableRecord)tr.GetObject(table[LayerName], OpenMode.ForWrite);
            existing.IsOff = false;
            existing.IsFrozen = false;
            existing.IsLocked = false;
            existing.IsPlottable = false;
            return existing.ObjectId;
        }

        table.UpgradeOpen();
        var layer = new LayerTableRecord
        {
            Name = LayerName,
            Color = Color.FromColorIndex(ColorMethod.ByAci, 8),
            IsPlottable = false
        };
        var id = table.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
        return id;
    }
}
