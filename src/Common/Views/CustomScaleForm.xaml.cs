using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ZwcadBatchPlot;

/// <summary>
/// 自定义打印比例对话框。
/// 当图纸尺寸无法按常用比例匹配标准 A0-A4 纸张时弹出。
/// 固定图框若长宽比仍接近标准图幅或 1/8 模数加长图，提供任意比例的图幅下拉；
/// 可自由拉伸动态块不走该分支。其余情况让用户输入绘图比例
/// （支持小数，如 143 / 1:143 / 0.25 表示 4:1）。
/// 比例语义：图面尺寸 / 比例 = 纸张毫米尺寸。自定义尺寸路径仅用于缩小场景，比例必须 ≥ 1。
/// </summary>
public sealed partial class CustomScaleForm : Window
{
    // 换算后纸张短边的常规范围（mm），与 PaperSizeDetector.GuessScale 的判断范围一致。
    private const double PaperShortSideMinMm = 100d;
    private const double PaperShortSideMaxMm = 900d;

    /// <summary>长宽比接近标准纸张时的说明，覆盖“请输入绘图比例”的通用文案。</summary>
    public const string AspectRatioHintText =
        "该图幅长宽比接近标准纸张或加长图，但比例不在常用列表中。请选择目标图幅，比例将按实际尺寸自动计算；也可选自定义尺寸后手动输入比例。";

    private static readonly Color PaperInfoColor = Color.FromRgb(0, 120, 212);
    private static readonly Color PaperWarnColor = Colors.OrangeRed;

    private readonly IReadOnlyList<PaperDetection> _aspectPapers;
    private readonly double _drawingWidth;
    private readonly double _drawingHeight;
    private string _customScaleText;

    /// <summary>用户选择的标准或加长图幅；为 null 表示使用自定义尺寸（仅按输入比例换算）。</summary>
    public PaperDetection? SelectedStandardPaper { get; private set; }

    public double SelectedScale { get; private set; }

    public double PaperWidthMm => SelectedStandardPaper?.PaperWidthMm ?? _drawingWidth / SelectedScale;

    public double PaperHeightMm => SelectedStandardPaper?.PaperHeightMm ?? _drawingHeight / SelectedScale;

    /// <param name="allowAspectRatioPapers">
    /// 为 true 时，若长宽比接近标准图幅或 1/8 模数加长图，提供任意比例的图幅下拉。
    /// 可自由拉伸动态块不会出现这种随便比例，应传 false，只保留手填自定义比例。
    /// </param>
    public CustomScaleForm(
        double drawingWidth,
        double drawingHeight,
        int guessedScale,
        string? hintText = null,
        bool allowAspectRatioPapers = true)
    {
        _drawingWidth = drawingWidth;
        _drawingHeight = drawingHeight;
        _customScaleText = guessedScale > 0 ? guessedScale.ToString() : "100";
        _aspectPapers = allowAspectRatioPapers
            ? PaperSizeDetector.DetectByAspectRatio(drawingWidth, drawingHeight)
            : new PaperDetection[0];
        var hasAspectPapers = _aspectPapers.Count > 0;

        InitializeComponent();

        _info.Text = hasAspectPapers
            ? $"图纸尺寸: {drawingWidth:0.##} x {drawingHeight:0.##} mm（长宽比接近标准纸张或加长图，比例不在常用列表）"
            : $"图纸尺寸: {drawingWidth:0.##} x {drawingHeight:0.##} mm（未匹配到 A0-A4 标准纸张）";

        _scaleInput.Text = _customScaleText;

        _hint.Text = hasAspectPapers
            ? AspectRatioHintText
            : (string.IsNullOrWhiteSpace(hintText) ? "" : hintText);

        if (hasAspectPapers)
        {
            _paperChoicePanel.Visibility = Visibility.Visible;
            _paperChoiceRow.Height = new GridLength(30);
            Height = 220;
            MinHeight = 200;
            CreatePaperComboItems();
        }
        else
        {
            _paperChoicePanel.Visibility = Visibility.Collapsed;
            _paperChoiceRow.Height = new GridLength(0);
            Height = 180;
            MinHeight = 170;
        }

        if (_paperCombo.Items.Count > 0)
        {
            ApplyPaperChoice();
        }
        else
        {
            UpdatePaperSize();
        }
    }

    /// <summary>目标图幅下拉：标准图幅候选项 + 末尾的自定义尺寸。</summary>
    private void CreatePaperComboItems()
    {
        foreach (var paper in _aspectPapers)
        {
            _paperCombo.Items.Add(new PaperChoiceItem(PaperSizeDetector.FormatOption(paper), paper));
        }

        _paperCombo.Items.Add(new PaperChoiceItem("自定义尺寸 | 手动输入比例", null));
        var preferred = PaperSizeDetector.IndexOfPreferredAspectRatioPaper(
            _drawingWidth,
            _drawingHeight,
            _aspectPapers);
        _paperCombo.SelectedIndex = preferred >= 0 ? preferred : _paperCombo.Items.Count - 1;
    }

    private void PaperCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyPaperChoice();
    }

    private void ScaleInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdatePaperSize();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Confirm();
    }

    /// <summary>
    /// 切换目标图幅：标准图幅锁定比例为按短边反推的任意比例；自定义尺寸恢复可编辑输入。
    /// </summary>
    private void ApplyPaperChoice()
    {
        var standard = GetSelectedChoicePaper();
        if (standard == null)
        {
            _scalePrefixLabel.Text = "打印比例  1 : ";
            _scaleInput.IsReadOnly = false;
            _scaleInput.Text = _customScaleText;
            UpdatePaperSize();
            return;
        }

        if (!_scaleInput.IsReadOnly)
        {
            _customScaleText = _scaleInput.Text;
        }

        _scalePrefixLabel.Text = "打印比例  ";
        _scaleInput.IsReadOnly = true;
        _scaleInput.Text = standard.ScaleText;
        ShowStandardPaperSize(standard);
    }

    private void UpdatePaperSize()
    {
        var standard = GetSelectedChoicePaper();
        if (standard != null)
        {
            ShowStandardPaperSize(standard);
            return;
        }

        // 输入无效或比例 <1 时只显示输入提示，不显示纸张尺寸。
        if (!PaperSizeDetector.TryParseScale(_scaleInput.Text, out var scale) || scale < 1)
        {
            _paperSizeLabel.Text = "请输入 ≥1 的比例数字，例如 100";
            _paperSizeLabel.Foreground = new SolidColorBrush(PaperInfoColor);
            return;
        }

        var pw = _drawingWidth / scale;
        var ph = _drawingHeight / scale;
        var shortSide = Math.Min(pw, ph);
        var rangeText = shortSide >= PaperShortSideMinMm && shortSide <= PaperShortSideMaxMm
            ? ""
            : "（超出常规 A4~A0 范围，可能造成超大或过小页面）";
        _paperSizeLabel.Text = $"打印纸张: {pw:0.##} x {ph:0.##} mm（自定义尺寸）{rangeText}";
        _paperSizeLabel.Foreground = new SolidColorBrush(rangeText.Length == 0 ? PaperInfoColor : PaperWarnColor);
    }

    private void ShowStandardPaperSize(PaperDetection paper)
    {
        _paperSizeLabel.Text = $"打印纸张: {paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm（{paper.PaperName}）";
        _paperSizeLabel.Foreground = new SolidColorBrush(PaperInfoColor);
    }

    private PaperDetection? GetSelectedChoicePaper()
    {
        return _paperCombo.SelectedItem is PaperChoiceItem item ? item.Paper : null;
    }

    private void Confirm()
    {
        var standard = GetSelectedChoicePaper();
        if (standard != null)
        {
            SelectedStandardPaper = standard;
            SelectedScale = standard.ScaleValue;
            DialogResult = true;
            Close();
            return;
        }

        if (!PaperSizeDetector.TryParseScale(_scaleInput.Text, out var scale) || scale < 1)
        {
            MessageBox.Show(
                "无法识别比例输入。请直接输入数字（100 表示 1:100），也支持 1:143 形式；比例必须大于等于 1。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // 换算纸张短边超出常规范围时二次确认，防止误输入导致超大纸张或内容缩成针尖（打印表现为空白页）。
        var paperWidthMm = _drawingWidth / scale;
        var paperHeightMm = _drawingHeight / scale;
        var shortSideMm = Math.Min(paperWidthMm, paperHeightMm);
        if (shortSideMm < PaperShortSideMinMm || shortSideMm > PaperShortSideMaxMm)
        {
            var confirm = MessageBox.Show(
                $"按 {PaperSizeDetector.ToScaleText(scale)} 换算的纸张为 {paperWidthMm:0.##} x {paperHeightMm:0.##} mm，"
                + "超出常规 A4~A0 图纸范围（短边 100~900mm），打印可能出现超大或过小页面。是否仍要使用该比例？",
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                _scaleInput.Focus();
                _scaleInput.SelectAll();
                return;
            }
        }

        SelectedStandardPaper = null;
        SelectedScale = scale;
        DialogResult = true;
        Close();
    }

    /// <summary>下拉项：标准图幅候选，或 Paper 为 null 的自定义尺寸。</summary>
    private sealed class PaperChoiceItem
    {
        public PaperChoiceItem(string display, PaperDetection? paper)
        {
            Display = display;
            Paper = paper;
        }

        public string Display { get; }

        public PaperDetection? Paper { get; }

        public override string ToString()
        {
            return Display;
        }
    }
}
