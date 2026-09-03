using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ZwcadBatchPlot;

public sealed partial class SinglePlotForm : Window
{
    /// <summary>留白下拉项，语义与 BatchPlotForm 的 MarginOption 一致（正值=扩大纸张，负值=缩比例）。</summary>
    private sealed class MarginOption
    {
        public double Value { get; set; }
        public override string ToString() => Value > 0
            ? $"+ {Value:0.#} mm"
            : $"- {Math.Abs(Value):0.#} mm";
    }

    // _paperCombo/_outputPath/_areaLabel/_scaleLabel/_styleCombo/_styleSettingsButton/
    // _leaveMargin/_marginInput 由 XAML 的 x:Name 生成。
    private readonly IReadOnlyList<PaperDetection> _candidates;

    public PaperDetection SelectedPaper
    {
        get
        {
            if (_paperCombo.SelectedIndex >= 0 && _paperCombo.SelectedIndex < _candidates.Count)
                return _candidates[_paperCombo.SelectedIndex];
            return _candidates[0];
        }
    }

    public string OutputPath => _outputPath.Text;
    public string SelectedStyle => _styleCombo.SelectedItem?.ToString() ?? "";
    public bool LeavePaperMargin => _leaveMargin.IsChecked == true;
    public double PaperMarginMm => ReadMarginValue(_marginInput);

    /// <summary>DialogResult 为 true 时，此属性区分是"打印"还是"预览"。</summary>
    public bool IsPreview { get; private set; }

    public SinglePlotForm(
        string sourceFile,
        double width,
        double height,
        IReadOnlyList<PaperDetection> candidates,
        IReadOnlyList<string> styles,
        string selectedStyle)
    {
        _candidates = candidates.Count > 0
            ? candidates
            : throw new ArgumentException("至少需要一个纸张候选项。", nameof(candidates));

        InitializeComponent();

        InitializeComponents(sourceFile, width, height, styles, selectedStyle);
    }

    private void InitializeComponents(
        string sourceFile,
        double width,
        double height,
        IReadOnlyList<string> styles,
        string selectedStyle)
    {
        // 第一行：打印区域
        _areaLabel.Text = $"打印区域: {width:0.##} × {height:0.##}（图纸单位）";

        // 第二行：比例
        var firstPaper = _candidates[0];
        _scaleLabel.Text = $"预估比例: {firstPaper.ScaleText}";

        // 第三行：纸张选择
        foreach (var candidate in _candidates)
        {
            _paperCombo.Items.Add(
                $"{candidate.PaperName}    {candidate.ScaleText}    {candidate.PaperWidthMm:0.##} × {candidate.PaperHeightMm:0.##} mm");
        }
        _paperCombo.SelectedIndex = 0;
        _paperCombo.SelectionChanged += (_, _) =>
        {
            var paper = SelectedPaper;
            _scaleLabel.Text = $"预估比例: {paper.ScaleText}";
        };

        // 第四行：输出路径
        _outputPath.Text = BuildDefaultPath(sourceFile);

        // 第五行：打印样式。选择值会由调用方写回共享设置，并实际传给预览/打印引擎。
        foreach (var style in styles)
        {
            _styleCombo.Items.Add(style);
        }
        PlotStyleManager.RestoreSavedStyle(_styleCombo, selectedStyle);
        _styleSettingsButton.IsEnabled = _styleCombo.SelectedIndex >= 0;
        _styleCombo.SelectionChanged += (_, _) =>
            _styleSettingsButton.IsEnabled = _styleCombo.SelectedIndex >= 0;

        // 第六行：留白。单张打印的预览和正式输出都读取这个值，保证留白效果一致。
        InitMarginCombo(_marginInput, 72, 1.0);
        _marginInput.IsEnabled = false;
        _leaveMargin.Checked += (_, _) => _marginInput.IsEnabled = _leaveMargin.IsChecked == true;
        _leaveMargin.Unchecked += (_, _) => _marginInput.IsEnabled = _leaveMargin.IsChecked == true;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "pdf",
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            FileName = Path.GetFileName(_outputPath.Text),
            InitialDirectory = Path.GetDirectoryName(_outputPath.Text),
            OverwritePrompt = true,
            Title = "选择 PDF 保存位置"
        };
        if (dialog.ShowDialog(this) == true)
        {
            _outputPath.Text = dialog.FileName;
        }
    }

    private void StyleSettingsButton_Click(object sender, RoutedEventArgs e)
        => PlotStyleManager.EditSelectedStyle(this, _styleCombo.SelectedItem?.ToString());

    private void LeaveMargin_CheckChanged(object sender, RoutedEventArgs e)
        => _marginInput.IsEnabled = _leaveMargin.IsChecked == true;

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        IsPreview = true;
        DialogResult = true;
        Close();
    }

    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        IsPreview = false;
        DialogResult = true;
        Close();
    }

    /// <summary>初始化留白下拉列表，语义与 BatchPlotForm.InitMarginCombo 一致：整数 1~10，每档先 + 再 -，共 20 项，选中与保存值最接近的项。</summary>
    private static void InitMarginCombo(ComboBox combo, int width, double savedValue)
    {
        combo.Width = width;
        combo.Items.Clear();
        for (var n = 1; n <= 10; n++)
        {
            combo.Items.Add(new MarginOption { Value = n });
            combo.Items.Add(new MarginOption { Value = -n });
        }

        var bestIdx = 0;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is MarginOption opt)
            {
                var diff = Math.Abs(opt.Value - savedValue);
                if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
            }
        }
        combo.SelectedIndex = bestIdx;
    }

    /// <summary>读取留白下拉列表的选中值（毫米），语义与 BatchPlotForm.ReadMarginValue 一致。</summary>
    private static double ReadMarginValue(ComboBox combo)
        => combo.SelectedItem is MarginOption opt ? opt.Value : 1.0;

    private static string BuildDefaultPath(string sourceFile)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFile);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "Drawing";

        var directory = Path.GetDirectoryName(sourceFile);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return Path.Combine(directory, baseName + ".pdf");
    }
}
