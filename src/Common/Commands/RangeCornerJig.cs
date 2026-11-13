using System;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.GraphicsInterface;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 三点图签「框选范围」时的第二角点拖动：鼠标一动就按第一角点画红色矩形（含对角线），
/// 点下去之前就能看清这一框到底圈住了哪片文字。
/// <para>
/// 原来第二点走 <c>Editor.GetCorner</c>，图面上只有 CAD 自带的白色橡皮筋，
/// 红框要等回到录入窗、由 <see cref="TransientFrameMarkers"/> 补画才出现——
/// 用户是「点完才看见框到哪」，框偏了只能重来一遍。这里用 <see cref="DrawJig"/>
/// 把红框提前到拖动过程里；提交后画的红框与拖动时同形同位，不会跳变。
/// </para>
/// <para>
/// 坐标系（本文件曾出过错，务必按此约定）：<c>Editor.GetPoint</c> 返回 <b>UCS</b>；
/// <c>JigPrompts.AcquirePoint</c> 返回 <b>WCS</b>；<c>JigPromptPointOptions.BasePoint</c>
/// 也要给 <b>WCS</b>；<c>WorldDraw</c> 画的同样是 WCS。
/// 旧实现把 jig 采到的 WCS 点当 UCS 用、BasePoint 也给了 UCS，
/// 于是「UCS 与 WCS 不一致」的图里橡皮筋从错误基点出发、红框被二次变换到反方向。
/// 现在统一用 <see cref="RectangleGeometry.GetUcsDragCorners"/> 在 WCS 里按 UCS 基轴分解位移，
/// 预览与 <c>PickRectangle</c> 入库的区域调用同一个方法，方向、大小、位置完全一致。
/// </para>
/// </summary>
internal sealed class RangeCornerJig : DrawJig
{
    /// <summary>采样点变化小于这个距离就当没动，避免无意义的整屏重画。</summary>
    private const double SamePointTolerance = 1e-6;

    /// <summary>拖动预览的色号：与提交后 <see cref="TransientFrameMarkers"/> 的红框一致（ACI 1）。</summary>
    private const short PreviewColorIndex = 1;

    private readonly Point3d _anchorUcs;
    private readonly Point3d _anchorWcs;
    private readonly Matrix3d _ucsToWcs;
    private readonly string _label;
    private Point3d _currentUcs;
    private Point3d _currentWcs;
    private bool _hasPoint;

    public RangeCornerJig(Point3d anchorUcs, Matrix3d ucsToWcs, string label)
    {
        _anchorUcs = anchorUcs;
        _ucsToWcs = ucsToWcs;
        _anchorWcs = anchorUcs.TransformBy(ucsToWcs);
        _currentUcs = anchorUcs;
        _currentWcs = _anchorWcs;
        _label = label;
    }

    /// <summary>是否已经采到有效的对角点；结束状态为 OK 但这里是 false 时按取消处理。</summary>
    public bool HasPoint => _hasPoint;

    /// <summary>采到的对角点，已换算回 UCS（供诊断与调用方核对）。</summary>
    public Point3d CurrentUcs => _currentUcs;

    /// <summary>采到的对角点原始值，WCS。最终矩形一律用它计算。</summary>
    public Point3d CurrentWcs => _currentWcs;

    protected override SamplerStatus Sampler(JigPrompts prompts)
    {
        var options = new JigPromptPointOptions($"\n框选「{_label}」- 对角点: ")
        {
            // 与原来的 PromptCornerOptions 保持同样的输入规则：允许三维坐标、不接受空回应/负数回应。
            UserInputControls = UserInputControls.Accept3dCoordinates
                | UserInputControls.NoZeroResponseAccepted
                | UserInputControls.NoNegativeResponseAccepted,
            // jig 的基点必须给 WCS，否则非世界 UCS 下橡皮筋从错误位置拉出。
            BasePoint = _anchorWcs,
            UseBasePoint = true,
            Cursor = CursorType.RubberBand
        };

        var result = prompts.AcquirePoint(options);
        if (result.Status != PromptStatus.OK)
        {
            // Esc、右键、回车都算「没给出角点」：直接收工，红框随 jig 一起消失。
            // 这里不能返回 NoChange——那会让 jig 继续等下一个点，用户按回车会觉得「没反应」。
            return SamplerStatus.Cancel;
        }

        // AcquirePoint 返回 WCS。与上一次的 WCS 值比较，避免每帧整屏刷新。
        if (_hasPoint && result.Value.DistanceTo(_currentWcs) <= SamePointTolerance)
        {
            return SamplerStatus.NoChange;
        }

        _currentWcs = result.Value;
        try
        {
            _currentUcs = result.Value.TransformBy(_ucsToWcs.Inverse());
        }
        catch
        {
            // 逆矩阵异常时不影响预览：预览与入库都只用 CurrentWcs。
            _currentUcs = result.Value;
        }

        _hasPoint = true;
        return SamplerStatus.OK;
    }

    protected override bool WorldDraw(WorldDraw draw)
    {
        if (!_hasPoint)
        {
            return true;
        }

        var geometry = draw.Geometry;
        if (geometry == null)
        {
            return true;
        }

        // 四个角直接由「UCS 第一角点 + WCS 对角点」在 WCS 里算出：
        // 绕 Z 旋转的 UCS 下用户看到的是斜框，与最终存进 LocalRectangle.CornerPoints 的斜矩形一致。
        var corners = RectangleGeometry.GetUcsDragCorners(_anchorUcs, _currentWcs, _ucsToWcs);

        // 退化成一个点/一条线时不画，避免出现闪烁的碎片。
        if (corners[0].DistanceTo(corners[1]) <= SamePointTolerance
            || corners[1].DistanceTo(corners[2]) <= SamePointTolerance)
        {
            return true;
        }

        draw.SubEntityTraits.Color = PreviewColorIndex;
        for (var index = 0; index < 4; index++)
        {
            geometry.WorldLine(corners[index], corners[(index + 1) % 4]);
        }

        // 对角线：与提交后的红框标识长得一样，落地时视觉上不跳。
        geometry.WorldLine(corners[0], corners[2]);
        geometry.WorldLine(corners[1], corners[3]);
        return true;
    }
}
