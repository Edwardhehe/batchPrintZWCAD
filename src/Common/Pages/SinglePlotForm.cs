using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

public sealed class SinglePlotForm : Form
{
    private readonly ComboBox _paperCombo = new();
    private readonly IReadOnlyList<PaperDetection> _candidates;
    private readonly TextBox _outputPath = new();
    private readonly Label _areaLabel = new();
    private readonly Label _scaleLabel = new();
    private readonly CheckBox _leaveMargin = new();

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
    public bool LeavePaperMargin => _leaveMargin.Checked;

    /// <summary>DialogResult.OK 时，此属性区分是"打印"还是"预览"。</summary>
    public bool IsPreview { get; private set; }

    public SinglePlotForm(
        string sourceFile,
        double width,
        double height,
        IReadOnlyList<PaperDetection> candidates)
    {
        _candidates = candidates.Count > 0
            ? candidates
            : throw new ArgumentException("至少需要一个纸张候选项。", nameof(candidates));

        InitializeComponents(sourceFile, width, height);
    }

    private void InitializeComponents(string sourceFile, double width, double height)
    {
        Text = "单张打印";
        UiLayout.ConfigureForm(this, 720, 420, 680, 380);
        // 高 DPI 下 Size 包含标题栏和边框，显式设置紧凑 ClientSize 避免控件重叠。
        ClientSize = new Size(UiLayout.Scale(720), UiLayout.Scale(380));
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(UiLayout.Scale(24))
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42))); // 区域
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42))); // 比例
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(50))); // 纸张
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(50))); // 输出
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(38))); // 留白
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));              // 间距
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(52))); // 按钮

        // 第一行：打印区域
        _areaLabel.Text = $"打印区域: {width:0.##} × {height:0.##}（图纸单位）";
        _areaLabel.Dock = DockStyle.Fill;
        _areaLabel.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_areaLabel, 0, 0);

        // 第二行：比例
        var firstPaper = _candidates[0];
        _scaleLabel.Text = $"预估比例: {firstPaper.ScaleText}";
        _scaleLabel.Dock = DockStyle.Fill;
        _scaleLabel.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_scaleLabel, 0, 1);

        // 第三行：纸张选择
        var paperRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        paperRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(96)));
        paperRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        paperRow.Controls.Add(new Label
        {
            Text = "纸张:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _paperCombo.Dock = DockStyle.Fill;
        _paperCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _paperCombo.Margin = new Padding(0, UiLayout.Scale(5), 0, UiLayout.Scale(5));
        _paperCombo.Items.AddRange(_candidates
            .Select(candidate =>
                $"{candidate.PaperName}    {candidate.ScaleText}    {candidate.PaperWidthMm:0.##} × {candidate.PaperHeightMm:0.##} mm")
            .Cast<object>()
            .ToArray());
        _paperCombo.SelectedIndex = 0;
        _paperCombo.SelectedIndexChanged += (_, _) =>
        {
            var paper = SelectedPaper;
            _scaleLabel.Text = $"预估比例: {paper.ScaleText}";
        };
        paperRow.Controls.Add(_paperCombo, 1, 0);
        root.Controls.Add(paperRow, 0, 2);

        // 第四行：输出路径
        var outputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1
        };
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(96)));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(104)));

        outputRow.Controls.Add(new Label
        {
            Text = "输出路径:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _outputPath.Dock = DockStyle.Fill;
        _outputPath.Margin = new Padding(0, UiLayout.Scale(5), UiLayout.Scale(8), UiLayout.Scale(5));
        _outputPath.Text = BuildDefaultPath(sourceFile);
        outputRow.Controls.Add(_outputPath, 1, 0);

        var browseButton = UiLayout.CreateButton("浏览...", 96);
        browseButton.Margin = new Padding(0, UiLayout.Scale(4), 0, UiLayout.Scale(4));
        browseButton.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = "pdf",
                Filter = "PDF 文件 (*.pdf)|*.pdf",
                FileName = Path.GetFileName(_outputPath.Text),
                InitialDirectory = Path.GetDirectoryName(_outputPath.Text),
                OverwritePrompt = true,
                Title = "选择 PDF 保存位置"
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _outputPath.Text = dialog.FileName;
            }
        };
        outputRow.Controls.Add(browseButton, 2, 0);
        root.Controls.Add(outputRow, 0, 3);

        _leaveMargin.Text = "留白（短边两侧各约 3mm）";
        _leaveMargin.AutoSize = true;
        _leaveMargin.Checked = false;
        _leaveMargin.Dock = DockStyle.Fill;
        _leaveMargin.TextAlign = ContentAlignment.MiddleLeft;
        // 单张打印的预览和正式输出都读取这个值，保证留白效果一致。
        root.Controls.Add(_leaveMargin, 0, 4);

        // 第六行弹性空白（上面已有 RowStyles 定义）

        // 第七行：操作按钮（预览 / 打印 / 取消）
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = UiLayout.Scale(42),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, UiLayout.Scale(5), 0, 0)
        };
        var printButton = UiLayout.CreateButton("打印", 96);
        printButton.DialogResult = DialogResult.OK;
        var previewButton = UiLayout.CreateButton("预览", 96);
        previewButton.Click += (_, _) =>
        {
            IsPreview = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = UiLayout.CreateButton("取消", 96);
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(printButton);
        buttons.Controls.Add(previewButton);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 6);

        AcceptButton = printButton;
        CancelButton = cancel;
        Controls.Add(root);
    }

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
