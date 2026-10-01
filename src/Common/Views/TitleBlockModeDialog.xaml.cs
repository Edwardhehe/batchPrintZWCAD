using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace ZwcadBatchPlot;

public sealed partial class TitleBlockModeDialog : Window
{
    public TitleBlockModeDialog(TitleBlockCornerModeResult result)
    {
        InitializeComponent();
        var rows = (result?.Modes ?? new List<TitleBlockCornerMode>())
            .Select(mode => new ModeRow(mode))
            .ToList();
        if (result != null && result.UnmeasuredBlockNames.Count > 0)
        {
            rows.Add(new ModeRow(
                "无法换算",
                result.UnmeasuredBlockNames.Count,
                "",
                "缺少纸张尺寸、打印边界或图名图号范围",
                "",
                string.Join("、", result.UnmeasuredBlockNames)));
        }

        _grid.ItemsSource = rows;
        var modeCount = result?.Modes.Count ?? 0;
        var blockCount = result?.Modes.Sum(x => x.BlockNames.Count) ?? 0;
        _summary.Text = "共 " + modeCount + " 种模式，覆盖 " + blockCount + " 个图框。";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private sealed class ModeRow
    {
        public ModeRow(TitleBlockCornerMode mode)
            : this(
                mode.Label,
                mode.BlockNames.Count,
                string.Join("、", mode.PaperNames),
                mode.Title.Describe(),
                mode.Number.Describe(),
                string.Join("、", mode.BlockNames))
        {
        }

        public ModeRow(string label, int count, string papers, string titleText, string numberText, string blocks)
        {
            Label = label;
            Count = count;
            Papers = papers;
            TitleText = titleText;
            NumberText = numberText;
            Blocks = blocks;
        }

        public string Label { get; }
        public int Count { get; }
        public string Papers { get; }
        public string TitleText { get; }
        public string NumberText { get; }
        public string Blocks { get; }
    }
}
