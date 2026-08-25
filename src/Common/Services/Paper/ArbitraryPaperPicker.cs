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
/// 任意纸张录入帮助类：图框外框识别不到 A4~A0 及其加长纸张时，弹出 <see cref="CustomScaleForm"/>。
/// 固定图框若长宽比仍接近标准图幅或 1/8 模数加长图，用户可选择 A0~A4 / A3+1/2 等（比例可为任意值，后续扫描按所选图幅尺寸反推比例）。
/// 可自由拉伸动态块不会出现这种随便比例，仍只按常用比例识别，匹配不到时仅手填自定义尺寸。
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
        // 可自由拉伸动态块只按常用比例识别加长长度，不会以任意比例套标准图幅。
        var allowAspectRatioPapers = !options.IncludeGenericDynamicTitleBlockPaper;
        using var scaleForm = new CustomScaleForm(actualWidth, actualHeight, guessedScale, HintText, allowAspectRatioPapers);
        var dialogResult = owner != null
            ? scaleForm.ShowDialog(owner)
            : ShowScaleDialog(scaleForm);
        if (dialogResult == DialogResult.OK
            && (scaleForm.SelectedStandardPaper != null || scaleForm.SelectedScale >= 1))
        {
            var chosen = CreatePaperFromScaleForm(scaleForm, actualWidth, actualHeight);
            if (string.Equals(chosen.PaperName, PaperSizeDetector.CustomPaperName, StringComparison.OrdinalIgnoreCase)
                || !allowAspectRatioPapers)
            {
                return new[] { chosen };
            }

            // 把用户确认的图幅放在首位，其余长宽比候选一并交给后续纸张下拉，便于改选。
            var ordered = new List<PaperDetection> { chosen };
            foreach (var paper in PaperSizeDetector.DetectByAspectRatio(actualWidth, actualHeight))
            {
                if (!string.Equals(paper.PaperName, chosen.PaperName, StringComparison.OrdinalIgnoreCase))
                {
                    ordered.Add(paper);
                }
            }

            return ordered;
        }

        // 用户取消：沿用原有 GuessScale 兜底，保证候选列表非空。
        return PaperSizeDetector.DetectCandidatesOrFallback(width, height, options);
    }

    /// <summary>
    /// 将比例对话框的确认结果转为纸张候选。
    /// 用户若选择了长宽比匹配的标准或加长图幅，则保存该图幅的标准物理尺寸，后续扫描按该尺寸反推任意比例；
    /// 否则仍按输入比例换算自定义纸张。
    /// </summary>
    public static PaperDetection CreatePaperFromScaleForm(CustomScaleForm form, double width, double height)
    {
        if (form.SelectedStandardPaper != null)
        {
            return form.SelectedStandardPaper;
        }

        var scale = form.SelectedScale;
        var paperWidthMm = width / scale;
        var paperHeightMm = height / scale;
        return new PaperDetection
        {
            PaperName = PaperSizeDetector.CustomPaperName,
            PaperWidthMm = paperWidthMm,
            PaperHeightMm = paperHeightMm,
            ScaleValue = scale,
            ScaleText = PaperSizeDetector.ToScaleText(scale),
            RequiresCustomPaper = true,
            Note = $"任意纸张：按用户输入比例 {PaperSizeDetector.ToScaleText(scale)} 换算，输出纸张 {paperWidthMm:0.##} x {paperHeightMm:0.##} mm"
        };
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
