using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

        var dialog = new ScalePickConfirmDialog(shortSide);
        if (CadDialog.ShowModal(dialog) != true)
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
    private sealed class ScalePickConfirmDialog : Window
    {
        // 计算值与整数的相对误差小于该值时吸附为整数，避免框选零头产生 1:143.02 这类脏比例。
        private const double IntegerSnapTolerance = 0.005d;

        private static readonly System.Windows.Media.FontFamily DefaultFont = new("Microsoft YaHei UI");

        private readonly double _frameShortSide;
        private readonly ComboBox _paper = new();
        private readonly TextBox _scaleText = new();

        public double ScaleValue { get; private set; }
        public string PaperName { get; private set; } = "";
        public double PaperShortSideMm { get; private set; }

        public ScalePickConfirmDialog(double frameShortSide)
        {
            _frameShortSide = frameShortSide;
            Title = "比例拾取";
            Width = 380;
            MinWidth = 340;
            Height = 170;
            MinHeight = 150;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = DefaultFont;
            FontSize = 11;

            var root = new Grid { Margin = new Thickness(10) };
            for (var i = 0; i < 2; i++)
            {
                root.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(110) : new GridLength(1, GridUnitType.Star) });
            }
            for (var i = 0; i < 3; i++)
            {
                root.RowDefinitions.Add(new RowDefinition { Height = i < 2 ? new GridLength(32) : new GridLength(1, GridUnitType.Star) });
            }

            AddLabel(root, "图纸图幅：", 0, 0);
            _paper.IsEditable = false;
            _paper.Width = 180;
            _paper.VerticalAlignment = VerticalAlignment.Center;
            _paper.HorizontalAlignment = HorizontalAlignment.Left;
            foreach (var (name, shortSideMm) in PaperOptions)
            {
                _paper.Items.Add($"{name}（短边 {shortSideMm:0.##}mm）");
            }
            _paper.SelectedIndex = GuessPaperIndex();
            _paper.SelectionChanged += (_, _) => RecalculateScaleText();
            Grid.SetColumn(_paper, 1);
            Grid.SetRow(_paper, 0);
            root.Children.Add(_paper);

            AddLabel(root, "比例：", 0, 1);
            _scaleText.Width = 180;
            _scaleText.VerticalAlignment = VerticalAlignment.Center;
            _scaleText.HorizontalAlignment = HorizontalAlignment.Left;
            Grid.SetColumn(_scaleText, 1);
            Grid.SetRow(_scaleText, 1);
            root.Children.Add(_scaleText);

            // 按钮行右对齐，视觉顺序与原 RightToLeft FlowLayoutPanel 一致：确定在最右。
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            var ok = CreateButton("确定", 82);
            ok.Click += (_, _) => Confirm();
            var cancel = CreateButton("取消", 82);
            cancel.Click += (_, _) =>
            {
                DialogResult = false;
                Close();
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            Grid.SetColumnSpan(buttons, 2);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            Content = root;
            RecalculateScaleText();
        }

        private static void AddLabel(Grid root, string text, int column, int row)
        {
            var label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, column);
            Grid.SetRow(label, row);
            root.Children.Add(label);
        }

        private static Button CreateButton(string text, int minWidth)
        {
            return new Button
            {
                Content = text,
                MinWidth = minWidth,
                MinHeight = 22,
                Padding = new Thickness(6, 0, 6, 0)
            };
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
                    Title ?? "",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            ScaleValue = scale;
            var (name, shortSideMm) = PaperOptions[_paper.SelectedIndex];
            PaperName = name;
            PaperShortSideMm = shortSideMm;
            DialogResult = true;
            Close();
        }
    }
}
