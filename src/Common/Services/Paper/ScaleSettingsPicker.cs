using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.EditorInput;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// “比例设置”标签页的图中拾取：框选一个图框，选择对应图幅后
/// 按 框选短边(CAD单位) / 纸张短边(mm) 反推比例，确认后写入自定义比例列表。
/// 交互模式与 <see cref="DirectoryTableGenerator.PromptRowHeight"/> 一致：
/// 设置窗体先保存并关闭，回到命令上下文后再执行本方法。
/// </summary>
public static class ScaleSettingsPicker
{
    // A0~A4 短边（mm），与 PaperSizeDetector 的标准纸张表保持一致。
    private static readonly (string Name, double ShortSideMm)[] PaperOptions =
    {
        ("A4", 210d), ("A3", 297d), ("A2", 420d), ("A1", 594d), ("A0", 841d)
    };

    public static bool PromptScaleFromFrame(Document document, AppSettings settings, out AppSettings updated, out string message)
    {
        updated = settings;
        var editor = document.Editor;
        var first = editor.GetPoint(new PromptPointOptions("\n框选图框第一个角点: "));
        if (first.Status != PromptStatus.OK)
        {
            message = "已取消比例拾取。";
            return false;
        }

        var second = editor.GetCorner(new PromptCornerOptions("\n框选图框对角点: ", first.Value));
        if (second.Status != PromptStatus.OK)
        {
            message = "已取消比例拾取。";
            return false;
        }

        var shortSide = Math.Min(
            Math.Abs(second.Value.X - first.Value.X),
            Math.Abs(second.Value.Y - first.Value.Y));
        if (shortSide <= 1e-6)
        {
            message = "框选区域的短边为 0，未添加比例。";
            return false;
        }

        using var dialog = new ScalePickConfirmDialog(shortSide);
#if ACAD_CORE
        if (dialog.ShowDialog() != DialogResult.OK)
#else
        if (CadApp.ShowModalDialog(dialog) != DialogResult.OK)
#endif
        {
            message = "已取消比例拾取。";
            return false;
        }

        var scale = dialog.ScaleValue;
        var scaleText = PaperSizeDetector.ToScaleText(scale);
        if (PaperSizeDetector.BuiltInScales.Any(x => Math.Abs(x - scale) < 1e-6)
            || settings.CustomScales.Any(x => Math.Abs(x - scale) < 1e-6))
        {
            message = $"比例 {scaleText} 已在支持列表中，无需重复添加。";
            return true;
        }

        updated.CustomScales.Add(scale);
        AppSettingsStore.Save(updated);
        message = $"已添加比例 {scaleText}（框选短边 {shortSide:0.##}，按 {dialog.PaperName} 短边 {dialog.PaperShortSideMm:0.##}mm 计算）。";
        return true;
    }

    /// <summary>
    /// 拾取确认窗体：下拉选择 A0~A4 图幅，按框选短边自动计算比例，允许用户微调后再录入。
    /// </summary>
    private sealed class ScalePickConfirmDialog : Form
    {
        // 计算值与整数的相对误差小于该值时吸附为整数，避免框选零头产生 1:143.02 这类脏比例。
        private const double IntegerSnapTolerance = 0.005d;

        private readonly double _frameShortSide;
        private readonly ComboBox _paper = new();
        private readonly TextBox _scaleText = new();

        public double ScaleValue { get; private set; }
        public string PaperName { get; private set; } = "";
        public double PaperShortSideMm { get; private set; }

        public ScalePickConfirmDialog(double frameShortSide)
        {
            _frameShortSide = frameShortSide;
            Text = "比例拾取";
            UiLayout.ConfigureForm(this, 380, 170, 340, 150);
            FormBorderStyle = FormBorderStyle.FixedDialog;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Padding = new Padding(UiLayout.Scale(10))
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(110)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(32)));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(32)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            root.Controls.Add(new Label
            {
                Text = "图纸图幅：",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            _paper.DropDownStyle = ComboBoxStyle.DropDownList;
            _paper.Dock = DockStyle.Left;
            _paper.Width = UiLayout.Scale(180);
            foreach (var (name, shortSideMm) in PaperOptions)
            {
                _paper.Items.Add($"{name}（短边 {shortSideMm:0.##}mm）");
            }
            _paper.SelectedIndex = GuessPaperIndex();
            _paper.SelectedIndexChanged += (_, _) => RecalculateScaleText();
            root.Controls.Add(_paper, 1, 0);

            root.Controls.Add(new Label
            {
                Text = "比例：",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 1);
            _scaleText.Dock = DockStyle.Left;
            _scaleText.Width = UiLayout.Scale(180);
            root.Controls.Add(_scaleText, 1, 1);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };
            root.SetColumnSpan(buttons, 2);
            var ok = UiLayout.CreateButton("确定", 82);
            ok.Click += (_, _) => Confirm();
            var cancel = UiLayout.CreateButton("取消", 82);
            cancel.Click += (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            root.Controls.Add(buttons, 0, 2);

            Controls.Add(root);
            RecalculateScaleText();
        }

        /// <summary>默认选中使比例最接近整数的图幅（框选准确时该项即为正确图幅）。</summary>
        private int GuessPaperIndex()
        {
            var bestIndex = 0;
            var bestDeviation = double.MaxValue;
            for (var i = 0; i < PaperOptions.Length; i++)
            {
                var scale = _frameShortSide / PaperOptions[i].ShortSideMm;
                var reference = scale >= 1 ? scale : 1d / scale;
                var deviation = Math.Abs(reference - Math.Round(reference)) / Math.Max(1, Math.Round(reference));
                if (deviation < bestDeviation)
                {
                    bestDeviation = deviation;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private void RecalculateScaleText()
        {
            var scale = _frameShortSide / PaperOptions[_paper.SelectedIndex].ShortSideMm;
            _scaleText.Text = PaperSizeDetector.ToScaleText(SnapToInteger(scale));
        }

        /// <summary>与整数的相对误差在容差内时吸附为整数；放大比例按倒数判断（0.2499 → 0.25）。</summary>
        private static double SnapToInteger(double scale)
        {
            var reference = scale >= 1 ? scale : 1d / scale;
            var rounded = Math.Round(reference);
            if (rounded >= 1 && Math.Abs(reference - rounded) / rounded <= IntegerSnapTolerance)
            {
                return scale >= 1 ? rounded : 1d / rounded;
            }

            return scale;
        }

        private void Confirm()
        {
            if (!PaperSizeDetector.TryParseScale(_scaleText.Text, out var scale))
            {
                MessageBox.Show(
                    "无法识别比例输入。请输入 143（表示 1:143）、0.25（表示 4:1）或 1:143 形式。",
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            ScaleValue = scale;
            var (name, shortSideMm) = PaperOptions[_paper.SelectedIndex];
            PaperName = name;
            PaperShortSideMm = shortSideMm;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
