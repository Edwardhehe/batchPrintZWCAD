using System;
using System.Collections.Generic;
using System.Windows;

namespace ZwcadBatchPlot;

public sealed partial class SinglePlotPaperSelectionForm : Window
{
    private readonly IReadOnlyList<PaperDetection> _candidates;

    public PaperDetection SelectedPaper =>
        _choices.SelectedIndex >= 0 && _choices.SelectedIndex < _candidates.Count
            ? _candidates[_choices.SelectedIndex]
            : _candidates[0];

    public SinglePlotPaperSelectionForm(IReadOnlyList<PaperDetection> candidates)
    {
        _candidates = candidates.Count > 0
            ? candidates
            : throw new ArgumentException("至少需要一个纸张候选项。", nameof(candidates));

        InitializeComponent();

        foreach (var candidate in _candidates)
        {
            _choices.Items.Add(
                $"{candidate.PaperName}    {candidate.ScaleText}    {candidate.PaperWidthMm:0.##} × {candidate.PaperHeightMm:0.##} mm");
        }
        _choices.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
