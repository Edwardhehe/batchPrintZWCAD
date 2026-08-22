using System;
using System.Collections.Generic;
using System.Windows.Forms;
#if AUTOCAD
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 任意纸张录入帮助类：图框外框识别不到 A4~A0 及其加长纸张时，
/// 弹出 <see cref="CustomScaleForm"/> 要求用户输入绘图比例，
/// 按 图面尺寸 / 比例 = 纸张毫米尺寸 生成"自定义"纸张候选（RequiresCustomPaper=true）。
/// 用户取消时沿用 <see cref="PaperSizeDetector.DetectCandidatesOrFallback"/> 的 GuessScale 兜底，
/// 保证候选列表始终非空。
/// </summary>
public static class ArbitraryPaperPicker
{
    /// <summary>
    /// 比例输入框的提示文案：说明比例仅用于把图幅转化为接近常规纸张大小的图纸，
    /// 避免打印出超大尺寸的图。
    /// </summary>
    public const string HintText =
        "该图幅未匹配到 A4~A0 及其加长纸张。请输入绘图比例，仅为将该图幅转化为接近常规纸张大小的图纸，避免打印出超大尺寸的图。";

    /// <summary>
    /// 检测纸张候选；识别不到任何标准纸张时弹窗要求用户输入比例。
    /// <paramref name="owner"/> 非空时（对话框内二次弹窗）以 WinForms 嵌套模态显示，保证置顶。
    /// </summary>
    public static IReadOnlyList<PaperDetection> DetectCandidatesOrPrompt(
        double width,
        double height,
        PaperSizeDetector.DetectionOptions options,
        Form? owner = null)
    {
        var candidates = PaperSizeDetector.DetectCandidates(width, height, options);
        if (candidates.Count > 0)
        {
            return candidates;
        }

        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var guessedScale = PaperSizeDetector.GuessScale(actualWidth, actualHeight);
        using var scaleForm = new CustomScaleForm(actualWidth, actualHeight, guessedScale, HintText);
        var dialogResult = owner != null
            ? scaleForm.ShowDialog(owner)
            : ShowScaleDialog(scaleForm);
        if (dialogResult == DialogResult.OK && scaleForm.SelectedScale >= 1)
        {
            var scale = scaleForm.SelectedScale;
            var paperWidthMm = actualWidth / scale;
            var paperHeightMm = actualHeight / scale;
            return new[]
            {
                new PaperDetection
                {
                    PaperName = PaperSizeDetector.CustomPaperName,
                    PaperWidthMm = paperWidthMm,
                    PaperHeightMm = paperHeightMm,
                    ScaleValue = scale,
                    ScaleText = PaperSizeDetector.ToScaleText(scale),
                    RequiresCustomPaper = true,
                    Note = $"任意纸张：按用户输入比例 {PaperSizeDetector.ToScaleText(scale)} 换算，输出纸张 {paperWidthMm:0.##} x {paperHeightMm:0.##} mm"
                }
            };
        }

        // 用户取消：沿用原有 GuessScale 兜底，保证候选列表非空。
        return PaperSizeDetector.DetectCandidatesOrFallback(width, height, options);
    }

    private static DialogResult ShowScaleDialog(Form form)
    {
#if ACAD_CORE
        return form.ShowDialog();
#else
        return CadApp.ShowModalDialog(form);
#endif
    }
}
