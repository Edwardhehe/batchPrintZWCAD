using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed class SettingsForm : Form
{
    private const string DefaultTextStyleDisplay = "(默认)";

    private readonly NumericUpDown _paperTolerance = new();
    private readonly CheckBox _recognizeFourLineRectangleFrames = new();
    private readonly CheckBox _hideFrameBoundaryWhenPlotting = new();
    private readonly ComboBox _longPaperNameFormat = new();
    private readonly NumericUpDown _longPaperSnapTolerance = new();
    private readonly CheckBox _addSequenceWhenPdfExists = new();
    private readonly CheckBox _openExternalDwgForPlot = new();
    private readonly CheckBox _useFileNameAsPdfBookmark = new();
    private readonly CheckBox _mergePdfByPaperSize = new();
    private readonly CheckBox _openOutputDirectoryAfterBatchPrint = new();
    private readonly CheckBox _openMergedPdfAfterMerge = new();
    private readonly CheckBox _generatePrintLog = new();
    private readonly ComboBox _directoryColorIndex = new();
    private readonly NumericUpDown _directoryTextHeight = new();
    private readonly NumericUpDown _directoryTextWidthFactor = new();
    private readonly NumericUpDown _directoryRowHeight = new();
    private readonly ComboBox _directoryTextStyle = new();
    private readonly TextBox _directoryLayerName = new();
    private readonly CheckBox _directoryDrawHeader = new();
    private readonly CheckBox _directoryDrawGridLines = new();
    private readonly DataGridView _directoryColumnsGrid = new();
    private readonly DirectoryPreviewControl _directoryOrderPreview = new();

    // 文件名设置
    private readonly TextBox _fileNamePattern = new();
    private readonly Label _fileNamePreview = new();
    private readonly NumericUpDown _fileNameSequenceStart = new();
    private readonly NumericUpDown _fileNameSequenceDigits = new();
    private readonly CheckBox _autoFileNameSequenceDigits = new();

    // 比例设置
    private readonly ListBox _scaleList = new();
    private readonly TextBox _scaleInput = new();

    public string? RequestedDirectoryColumnKey { get; private set; }
    public bool RequestPickDirectoryRowHeight { get; private set; }
    public bool RequestPickDirectoryTextAppearance { get; private set; }
    public bool RequestPickScaleFromCad { get; private set; }

    /// <summary>
    /// 设置/获取下次打开设置窗口时默认显示的标签页索引（0=常规, 1=文件名, 2=图纸目录, 3=比例设置）。
    /// 调用方在窗体关闭后读取 <see cref="SelectedTabIndex"/> 并传入下一次构造，实现图中交互后回到原标签页。
    /// </summary>
    public static int InitialTabIndex { get; set; }

    /// <summary>
    /// 窗体关闭前记录当前标签页索引，供调用方传给 <see cref="InitialTabIndex"/>。
    /// </summary>
    public int SelectedTabIndex { get; private set; }

    private TabControl _tabs = null!;

    public SettingsForm()
    {
        InitializeComponents();
        LoadSettings();
    }

    private void InitializeComponents()
    {
        Text = "批量打印设置";
        UiLayout.ConfigureForm(this, 760, 600, 680, 540);
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(10))
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(24)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildFileNameTab());
        _tabs.TabPages.Add(BuildDirectoryTab());
        _tabs.TabPages.Add(BuildScaleTab());

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "图纸目录会写入当前 CAD 当前空间；目录列与批量打印实际识别出的图框字段保持一致。"
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = UiLayout.CreateButton("保存", 82);
        save.Click += (_, _) => SaveSettings();
        var reset = UiLayout.CreateButton("恢复默认", 96);
        reset.Click += (_, _) => ResetDefaults();
        var cancel = UiLayout.CreateButton("取消", 82);
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(reset);

        root.Controls.Add(_tabs, 0, 0);
        root.Controls.Add(hint, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        // 恢复上次关闭时的标签页（如从 CAD 交互返回后回到"图纸目录"而非"常规"）
        if (InitialTabIndex >= 0 && InitialTabIndex < _tabs.TabCount)
        {
            _tabs.SelectedIndex = InitialTabIndex;
        }
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("常规")
        {
            Padding = new Padding(UiLayout.Scale(10)),
            AutoScroll = true
        };
        var categories = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        categories.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        ConfigureNumber(_paperTolerance, 0.5M, 20M, 0.5M, 1);
        ConfigureNumber(_longPaperSnapTolerance, 0.5M, 20M, 0.5M, 1);
        _longPaperSnapTolerance.ValueChanged += (_, _) => UpdateFileNamePreview();
        var longPaperSnapTip = new ToolTip();
        longPaperSnapTip.SetToolTip(
            _longPaperSnapTolerance,
            "加长图长边吸附到最近 1/8 模数标准加长图的容差；同时影响实际打印纸张和输出名称。");

        _addSequenceWhenPdfExists.Text = "PDF 已存在时自动加序号";
        _addSequenceWhenPdfExists.AutoSize = true;
        _addSequenceWhenPdfExists.Dock = DockStyle.Fill;

        _openExternalDwgForPlot.Text = "跨文件打印时临时打开 DWG";
        _openExternalDwgForPlot.AutoSize = true;
        _openExternalDwgForPlot.Dock = DockStyle.Fill;

        var paperTable = CreateSettingsTable(2);
        UiLayout.AddRow(paperTable, 0, "纸张匹配容差(mm)", _paperTolerance);
        UiLayout.AddRow(paperTable, 1, "加长图长边吸附容差(mm)", _longPaperSnapTolerance);

        _recognizeFourLineRectangleFrames.Text = "识别四条直线或直线型 PL 首尾相连组成的矩形框";
        _recognizeFourLineRectangleFrames.AutoSize = true;
        _recognizeFourLineRectangleFrames.Dock = DockStyle.Fill;
        var frameRecognitionTip = new ToolTip();
        frameRecognitionTip.SetToolTip(
            _recognizeFourLineRectangleFrames,
            "仅在四个独立实体的端点严格首尾相连并通过矩形几何校验时识别；后续沿用原有 PL 矩形框打印流程。");
        var frameRecognitionTable = CreateSettingsTable(1);
        UiLayout.AddRow(frameRecognitionTable, 0, "", _recognizeFourLineRectangleFrames);

        _hideFrameBoundaryWhenPlotting.Text = "不打印图框的外边框";
        _hideFrameBoundaryWhenPlotting.AutoSize = true;
        _hideFrameBoundaryWhenPlotting.Dock = DockStyle.Fill;
        var hideFrameTip = new ToolTip();
        hideFrameTip.SetToolTip(
            _hideFrameBoundaryWhenPlotting,
            "勾选后，正式打印期间把识别到的图框外边框临时移到“LA-临时不打印层”；打印完成、失败或取消后立即恢复。");

        _generatePrintLog.Text = "生成打印日志";
        _generatePrintLog.AutoSize = true;
        _generatePrintLog.Dock = DockStyle.Fill;
        var printLogTip = new ToolTip();
        printLogTip.SetToolTip(
            _generatePrintLog,
            "插件日志总开关。勾选后允许生成打印、拆图、扫描警告和图框录入诊断日志；默认关闭。日志目录：" + BatchPlotLogger.LogDirectory);

        var plotTable = CreateSettingsTable(3);
        UiLayout.AddRow(plotTable, 0, "", _openExternalDwgForPlot);
        UiLayout.AddRow(plotTable, 1, "", _hideFrameBoundaryWhenPlotting);
        UiLayout.AddRow(plotTable, 2, "", _generatePrintLog);

        var outputTable = CreateSettingsTable(1);
        UiLayout.AddRow(outputTable, 0, "", _addSequenceWhenPdfExists);

        _useFileNameAsPdfBookmark.Text = "文件名作为书签";
        _useFileNameAsPdfBookmark.AutoSize = true;
        _useFileNameAsPdfBookmark.Dock = DockStyle.Fill;

        _mergePdfByPaperSize.Text = "按纸张大小合并";
        _mergePdfByPaperSize.AutoSize = true;
        _mergePdfByPaperSize.Dock = DockStyle.Fill;

        var mergeExplanation = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Text = "同一纸张尺寸合并到一个 PDF；一批图纸包含多种尺寸时，按尺寸分别生成多个 PDF。"
        };
        var mergeTable = CreateSettingsTable(3);
        UiLayout.AddRow(mergeTable, 0, "", _useFileNameAsPdfBookmark);
        UiLayout.AddRow(mergeTable, 1, "", _mergePdfByPaperSize);
        UiLayout.AddRow(mergeTable, 2, "", mergeExplanation);

        _openOutputDirectoryAfterBatchPrint.Text = "批量打印单张后，打开所在文件夹";
        _openOutputDirectoryAfterBatchPrint.AutoSize = true;
        _openOutputDirectoryAfterBatchPrint.Dock = DockStyle.Fill;

        _openMergedPdfAfterMerge.Text = "PDF 合并完成后，打开该文件";
        _openMergedPdfAfterMerge.AutoSize = true;
        _openMergedPdfAfterMerge.Dock = DockStyle.Fill;

        var completedActionTable = CreateSettingsTable(2);
        UiLayout.AddRow(completedActionTable, 0, "", _openOutputDirectoryAfterBatchPrint);
        UiLayout.AddRow(completedActionTable, 1, "", _openMergedPdfAfterMerge);

        categories.Controls.Add(CreateSettingsGroup("纸张匹配", paperTable), 0, 0);
        categories.Controls.Add(CreateSettingsGroup("矩形框识别", frameRecognitionTable), 0, 1);
        categories.Controls.Add(CreateSettingsGroup("打印行为", plotTable), 0, 2);
        categories.Controls.Add(CreateSettingsGroup("输出文件", outputTable), 0, 3);
        categories.Controls.Add(CreateSettingsGroup("PDF 合并", mergeTable), 0, 4);
        categories.Controls.Add(CreateSettingsGroup("完成后操作", completedActionTable), 0, 5);
        page.Controls.Add(categories);
        return page;
    }

    private static GroupBox CreateSettingsGroup(string title, Control content)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(UiLayout.Scale(4)),
            Margin = new Padding(0, 0, 0, UiLayout.Scale(4))
        };
        content.Dock = DockStyle.Top;
        content.AutoSize = true;
        group.Controls.Add(content);
        return group;
    }

    private TabPage BuildScaleTab()
    {
        var page = new TabPage("比例设置")
        {
            Padding = new Padding(UiLayout.Scale(10))
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(64)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(64)));

        _scaleList.Dock = DockStyle.Fill;
        _scaleList.SelectionMode = SelectionMode.MultiExtended;
        _scaleList.IntegralHeight = false;
        var listGroup = new GroupBox
        {
            Text = "支持的比例（内置比例不可删除）",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(6))
        };
        listGroup.Controls.Add(_scaleList);
        root.Controls.Add(listGroup, 0, 0);

        var addTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiLayout.Scale(6), 0, 0)
        };
        addTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(180)));
        addTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(96)));
        addTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addTable.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        addTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _scaleInput.Dock = DockStyle.Fill;
        addTable.Controls.Add(_scaleInput, 0, 0);
        var addButton = UiLayout.CreateButton("添加", 82);
        addButton.Dock = DockStyle.Left;
        addButton.Click += (_, _) => AddCustomScale();
        addTable.Controls.Add(addButton, 1, 0);
        addTable.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "输入 143 表示 1:143；输入 0.25 表示 4:1；也支持 1:143 写法"
        }, 2, 0);
        var addHint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "添加后的比例随“保存”持久化，矩形框和图框块识别都会使用。"
        };
        addTable.Controls.Add(addHint, 0, 1);
        addTable.SetColumnSpan(addHint, 3);
        root.Controls.Add(addTable, 0, 1);

        var pickTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiLayout.Scale(6), 0, 0)
        };
        pickTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(110)));
        pickTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(110)));
        pickTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var pickButton = UiLayout.CreateButton("图中拾取…", 96);
        pickButton.Dock = DockStyle.Left;
        pickButton.Click += (_, _) => RequestScaleFromCad();
        pickTable.Controls.Add(pickButton, 0, 0);
        var removeButton = UiLayout.CreateButton("删除所选", 96);
        removeButton.Dock = DockStyle.Left;
        removeButton.Click += (_, _) => RemoveSelectedCustomScales();
        pickTable.Controls.Add(removeButton, 1, 0);
        pickTable.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "图中拾取：在图中框选一个图框并选择 A0~A4 图幅，按短边自动计算比例"
        }, 2, 0);
        root.Controls.Add(pickTable, 0, 2);

        page.Controls.Add(root);
        return page;
    }

    /// <summary>比例列表项；自定义项可删除，内置项只读展示。</summary>
    private sealed class ScaleListItem
    {
        public ScaleListItem(double value, bool isCustom)
        {
            Value = value;
            IsCustom = isCustom;
        }

        public double Value { get; }
        public bool IsCustom { get; }

        public override string ToString()
        {
            return PaperSizeDetector.ToScaleText(Value) + (IsCustom ? "（自定义）" : "");
        }
    }

    private void ReloadScaleList(AppSettings settings)
    {
        _scaleList.Items.Clear();
        foreach (var scale in PaperSizeDetector.BuiltInScales)
        {
            _scaleList.Items.Add(new ScaleListItem(scale, false));
        }

        foreach (var scale in settings.CustomScales)
        {
            _scaleList.Items.Add(new ScaleListItem(scale, true));
        }
    }

    private List<double> ReadCustomScalesFromList()
    {
        return _scaleList.Items
            .Cast<ScaleListItem>()
            .Where(x => x.IsCustom)
            .Select(x => x.Value)
            .ToList();
    }

    private void AddCustomScale()
    {
        if (!PaperSizeDetector.TryParseScale(_scaleInput.Text, out var scale))
        {
            MessageBox.Show(
                "无法识别比例输入。请输入 143（表示 1:143）、0.25（表示 4:1）或 1:143 形式。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_scaleList.Items.Cast<ScaleListItem>().Any(x => Math.Abs(x.Value - scale) < 1e-6))
        {
            MessageBox.Show(
                $"比例 {PaperSizeDetector.ToScaleText(scale)} 已在列表中，无需重复添加。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // 自定义项按值升序插入，与保存时 NormalizeCustomScales 的排序一致，避免重开窗口后顺序变化。
        var insertIndex = _scaleList.Items.Count;
        for (var i = 0; i < _scaleList.Items.Count; i++)
        {
            if (_scaleList.Items[i] is ScaleListItem existing && existing.IsCustom && existing.Value > scale)
            {
                insertIndex = i;
                break;
            }
        }

        var added = new ScaleListItem(scale, true);
        _scaleList.Items.Insert(insertIndex, added);
        _scaleList.SelectedItem = added;
        _scaleInput.Clear();
        _scaleInput.Focus();
    }

    private void RemoveSelectedCustomScales()
    {
        var selected = _scaleList.SelectedItems.Cast<ScaleListItem>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (selected.Any(x => !x.IsCustom))
        {
            MessageBox.Show(
                "内置比例不可删除，只能移除“（自定义）”比例。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        foreach (var item in selected)
        {
            _scaleList.Items.Remove(item);
        }
    }

    private void RequestScaleFromCad()
    {
        if (GetActiveDocument() == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        // 与目录行高/列宽交互一致：先保存当前页面编辑并退出模态窗体，再回到 CAD 命令上下文框选。
        AppSettingsStore.Save(settings);
        RequestPickScaleFromCad = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private TabPage BuildFileNameTab()
    {
        var page = new TabPage("文件名")
        {
            Padding = new Padding(UiLayout.Scale(12))
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(178)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(68)));

        var instructionPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(8), UiLayout.Scale(12), UiLayout.Scale(8)),
            BackColor = Color.FromArgb(247, 252, 247),
            BorderStyle = BorderStyle.FixedSingle
        };
        var instructionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        instructionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        instructionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(54)));
        instructionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(28)));
        instructionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        instructionLayout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ForeColor = Color.FromArgb(35, 145, 55),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "文件命名用以下字母表示各类信息："
        }, 0, 0);
        var tokenGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (var column = 0; column < 4; column++)
        {
            tokenGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }
        tokenGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        tokenGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var tokens = new[]
        {
            "A：图号", "B：版次", "C：图名", "D：日期",
            "E：信息1", "F：信息2", "G：设计阶段", "T：图幅"
        };
        for (var index = 0; index < tokens.Length; index++)
        {
            tokenGrid.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ForeColor = Color.FromArgb(35, 145, 55),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = tokens[index]
            }, index % 4, index / 4);
        }
        instructionLayout.Controls.Add(tokenGrid, 0, 1);
        instructionLayout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ForeColor = Color.FromArgb(35, 145, 55),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "N：序号（顺序与打印顺序一致）"
        }, 0, 2);
        instructionLayout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(35, 145, 55),
            Text = "输入这些字母本身时请用 \\ 转义，例如 \\A 输出 A。"
        }, 0, 3);
        instructionPanel.Controls.Add(instructionLayout);
        root.Controls.Add(instructionPanel, 0, 0);

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiLayout.Scale(10), 0, 0)
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(125)));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++)
        {
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        }
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label CreateRowLabel(string text, Color? color = null)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Text = text,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = color ?? SystemColors.ControlText
            };
        }

        editor.Controls.Add(CreateRowLabel("文件命名："), 0, 0);
        _fileNamePattern.Dock = DockStyle.None;
        _fileNamePattern.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _fileNamePattern.Margin = Padding.Empty;
        _fileNamePattern.MaxLength = 240;
        _fileNamePattern.TextChanged += (_, _) => UpdateFileNamePreview();
        editor.Controls.Add(_fileNamePattern, 1, 0);

        editor.Controls.Add(CreateRowLabel("开始序号："), 0, 1);
        var startEditor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        startEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(115)));
        startEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ConfigureNumber(_fileNameSequenceStart, 0, 999999999, 1, 0);
        _fileNameSequenceStart.Dock = DockStyle.None;
        _fileNameSequenceStart.Anchor = AnchorStyles.Left;
        _fileNameSequenceStart.Margin = Padding.Empty;
        _fileNameSequenceStart.Width = UiLayout.Scale(105);
        _fileNameSequenceStart.ValueChanged += (_, _) => UpdateFileNamePreview();
        startEditor.Controls.Add(_fileNameSequenceStart, 0, 0);
        startEditor.Controls.Add(CreateRowLabel("例如从 100 开始"), 1, 0);
        editor.Controls.Add(startEditor, 1, 1);

        editor.Controls.Add(CreateRowLabel("序号位数："), 0, 2);
        var digitsEditor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        digitsEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(80)));
        digitsEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(120)));
        digitsEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ConfigureNumber(_fileNameSequenceDigits, 0, 10, 1, 0);
        _fileNameSequenceDigits.Dock = DockStyle.None;
        _fileNameSequenceDigits.Anchor = AnchorStyles.Left;
        _fileNameSequenceDigits.Margin = Padding.Empty;
        _fileNameSequenceDigits.Width = UiLayout.Scale(70);
        _fileNameSequenceDigits.ValueChanged += (_, _) => UpdateFileNamePreview();
        digitsEditor.Controls.Add(_fileNameSequenceDigits, 0, 0);
        digitsEditor.Controls.Add(CreateRowLabel("0 表示不补零"), 1, 0);
        _autoFileNameSequenceDigits.Text = "按清单总张数自动推断";
        _autoFileNameSequenceDigits.AutoSize = true;
        _autoFileNameSequenceDigits.Dock = DockStyle.None;
        _autoFileNameSequenceDigits.Anchor = AnchorStyles.Left;
        _autoFileNameSequenceDigits.Margin = Padding.Empty;
        _autoFileNameSequenceDigits.CheckedChanged += (_, _) =>
        {
            UpdateSequenceDigitsState();
            UpdateFileNamePreview();
        };
        digitsEditor.Controls.Add(_autoFileNameSequenceDigits, 2, 0);
        editor.Controls.Add(digitsEditor, 1, 2);

        editor.Controls.Add(CreateRowLabel("输出示例：", Color.Navy), 0, 3);
        _fileNamePreview.Dock = DockStyle.Fill;
        _fileNamePreview.Margin = Padding.Empty;
        _fileNamePreview.TextAlign = ContentAlignment.MiddleLeft;
        _fileNamePreview.ForeColor = Color.Navy;
        _fileNamePreview.AutoSize = false;
        editor.Controls.Add(_fileNamePreview, 1, 3);
        root.Controls.Add(editor, 0, 1);

        // ── 加长图图名设置 ──
        var longPaperGroup = new GroupBox
        {
            Text = "加长图图名设置",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, UiLayout.Scale(6), 0, 0),
            Padding = new Padding(UiLayout.Scale(8), UiLayout.Scale(4), UiLayout.Scale(8), UiLayout.Scale(4))
        };
        var longPaperRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false
        };
        longPaperRow.Controls.Add(new Label
        {
            Text = "加长图命名格式：",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, UiLayout.Scale(4), UiLayout.Scale(6), 0)
        });
        _longPaperNameFormat.DropDownStyle = ComboBoxStyle.DropDownList;
        _longPaperNameFormat.Width = UiLayout.Scale(240);
        _longPaperNameFormat.Margin = new Padding(0, UiLayout.Scale(2), 0, 0);
        _longPaperNameFormat.Items.AddRange(new object[]
        {
            "配置1（分数）：A3+1/8、A2+3/4（分数形式）",
            "配置2（小数）：A3+0.125、A2+0.75（小数形式）",
            "配置3（预留）",
            "配置4（预留）",
            "配置5（预留）",
            "配置6（预留）",
        });
        _longPaperNameFormat.SelectedIndex = 0;
        _longPaperNameFormat.SelectedIndexChanged += (_, _) => UpdateFileNamePreview();
        longPaperRow.Controls.Add(_longPaperNameFormat);
        longPaperGroup.Controls.Add(longPaperRow);
        root.Controls.Add(longPaperGroup, 0, 2);

        page.Controls.Add(root);
        return page;
    }

    private void UpdateFileNamePreview()
    {
        if (string.IsNullOrWhiteSpace(_fileNamePattern.Text))
        {
            _fileNamePreview.Text = "（请输入文件命名规则）";
            return;
        }

        var example = new PlotJob
        {
            DrawingNumber = "岩施003",
            Revision = "1.0版",
            Title = "基坑支护平面布置图",
            Date = "2026-07-18",
            Info1 = "信息1",
            Info2 = "信息2",
            Phase = "施工图",
            PaperName = "A2"
        };
        var startNumber = (int)_fileNameSequenceStart.Value;
        var sequenceDigits = FileNameSanitizer.ResolveSequenceDigits(
            _autoFileNameSequenceDigits.Checked,
            (int)_fileNameSequenceDigits.Value,
            startNumber,
            1);
        _fileNamePreview.Text = FileNameSanitizer.FormatFileNamePattern(
            _fileNamePattern.Text,
            example,
            startNumber,
            sequenceDigits,
            (LongPaperNameFormat)Math.Max(0, _longPaperNameFormat.SelectedIndex),
            (double)_longPaperSnapTolerance.Value);
        if (_autoFileNameSequenceDigits.Checked)
        {
            _fileNamePreview.Text += "（实际位数按图框列表总张数计算）";
        }
    }

    private void UpdateSequenceDigitsState()
    {
        _fileNameSequenceDigits.Enabled = !_autoFileNameSequenceDigits.Checked;
    }

    private TabPage BuildDirectoryTab()
    {
        var page = new TabPage("图纸目录") { Padding = new Padding(UiLayout.Scale(5)) };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(142)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        ConfigureDirectoryColorIndex();
        ConfigureNumber(_directoryTextHeight, 1, 1000000, 10, 2);
        ConfigureNumber(_directoryTextWidthFactor, 0.1M, 10, 0.05M, 2);
        ConfigureNumber(_directoryRowHeight, 1, 1000000, 10, 2);
        _directoryTextHeight.ValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryTextWidthFactor.ValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryRowHeight.ValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryTextStyle.DropDownStyle = ComboBoxStyle.DropDownList;
        // 与“颜色索引”一致使用 OwnerDrawFixed：普通 ComboBox 高度由字体决定且不可改，
        // 同行的文本框/数字框又是另一套默认高度，导致一排输入框高矮不一。
        _directoryTextStyle.DrawMode = DrawMode.OwnerDrawFixed;
        _directoryTextStyle.ItemHeight = UiLayout.Scale(13);
        _directoryTextStyle.DrawItem += DrawDirectoryTextStyle;
        _directoryTextStyle.SelectedIndexChanged += (_, _) => UpdateDirectoryPreview();
        LoadTextStyles();

        var parameterGroup = new GroupBox
        {
            Text = "目录字体及绘制相关设置",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(6))
        };
        var parameters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 2
        };
        for (var i = 0; i < 6; i++)
        {
            parameters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.666F));
        }
        parameters.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(88)));
        parameters.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(28)));

        var pickTextAppearance = UiLayout.CreateButton("点选目录文字", 108);
        pickTextAppearance.Margin = new Padding(0, UiLayout.Scale(2), 0, 0);
        pickTextAppearance.Click += (_, _) => RequestTextAppearanceFromCad();
        var pickTextTip = new ToolTip();
        pickTextTip.SetToolTip(
            pickTextAppearance,
            "在当前活动图纸中点选文字，自动读取颜色、字高、宽度因子、文字样式和图层。");

        parameters.Controls.Add(
            BuildDirectoryParameter("颜色索引", _directoryColorIndex, pickTextAppearance),
            0,
            0);
        parameters.Controls.Add(BuildDirectoryParameter("文字高度", _directoryTextHeight), 1, 0);
        parameters.Controls.Add(BuildDirectoryParameter("宽度因子", _directoryTextWidthFactor), 2, 0);
        parameters.Controls.Add(BuildDirectoryParameter("文字样式", _directoryTextStyle), 3, 0);
        parameters.Controls.Add(BuildDirectoryParameter("图层名称", _directoryLayerName), 4, 0);
        parameters.Controls.Add(BuildDirectoryRowHeightParameter(), 5, 0);

        _directoryDrawHeader.Text = "绘制目录表头";
        _directoryDrawHeader.AutoSize = true;
        _directoryDrawGridLines.Text = "绘制目录框线";
        _directoryDrawGridLines.AutoSize = true;
        var drawOptions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false
        };
        drawOptions.Controls.Add(_directoryDrawHeader);
        drawOptions.Controls.Add(_directoryDrawGridLines);
        parameters.Controls.Add(drawOptions, 0, 1);
        parameters.SetColumnSpan(drawOptions, 6);
        parameterGroup.Controls.Add(parameters);

        var contentGroup = new GroupBox
        {
            Text = "目录内容设置",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(6))
        };
        ConfigureDirectoryColumnsGrid();
        var contentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(19)));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(76)));
        contentLayout.Controls.Add(_directoryColumnsGrid, 0, 0);
        contentLayout.Controls.Add(new Label
        {
            Text = "顺序预览（按实际列宽、行高和字高等比例缩放）",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.DimGray
        }, 0, 1);
        contentLayout.Controls.Add(_directoryOrderPreview, 0, 2);
        contentGroup.Controls.Add(contentLayout);

        root.Controls.Add(parameterGroup, 0, 0);
        root.Controls.Add(contentGroup, 0, 1);
        page.Controls.Add(root);
        return page;
    }

    private static Control BuildDirectoryParameter(string label, Control input, Control? trailingControl = null)
    {
        // 输入框统一使用固有高度（约 19px）：NumericUpDown 和普通 ComboBox 的高度由字体锁定，
        // 无法拉伸，因此两个下拉框用 OwnerDrawFixed + ItemHeight 压到同一高度。
        // 不能简单地 AutoSize=false + Dock=Fill：TableLayoutPanel 会把多余高度全部分配给
        // 最后一行，文本框会被撑到整行剩余高度。
        input.Dock = DockStyle.None;
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = new Padding(0, UiLayout.Scale(2), UiLayout.Scale(4), 0);
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(20)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(26)));
        // 余量行吸收多余空间，避免输入行被 WinForms 拉高。
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        panel.Controls.Add(input, 0, 1);
        if (trailingControl != null)
        {
            trailingControl.Dock = DockStyle.None;
            trailingControl.Anchor = AnchorStyles.Left;
            panel.Controls.Add(trailingControl, 0, 2);
        }
        return panel;
    }

    private Control BuildDirectoryRowHeightParameter()
    {
        _directoryRowHeight.Dock = DockStyle.None;
        _directoryRowHeight.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _directoryRowHeight.Margin = new Padding(0, UiLayout.Scale(2), UiLayout.Scale(4), 0);
        var pickHeight = UiLayout.CreateButton("图中交互", 68);
        pickHeight.Margin = new Padding(0, UiLayout.Scale(2), 0, 0);
        pickHeight.Enabled = GetActiveDocument() != null;
        pickHeight.Click += (_, _) => RequestRowHeightFromCad();

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(20)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(26)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(27)));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label
        {
            Text = "目录行高",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        panel.Controls.Add(_directoryRowHeight, 0, 1);
        panel.Controls.Add(pickHeight, 0, 2);
        return panel;
    }

    private void ConfigureDirectoryColorIndex()
    {
        _directoryColorIndex.DropDownStyle = ComboBoxStyle.DropDownList;
        _directoryColorIndex.DrawMode = DrawMode.OwnerDrawFixed;
        _directoryColorIndex.ItemHeight = UiLayout.Scale(13);
        _directoryColorIndex.MaxDropDownItems = 14;
        _directoryColorIndex.IntegralHeight = false;
        _directoryColorIndex.DropDownHeight = UiLayout.Scale(282);
        _directoryColorIndex.DrawItem += DrawDirectoryColorIndex;
        for (var index = 0; index <= 256; index++)
        {
            _directoryColorIndex.Items.Add(new DirectoryColorItem(index, GetAciPreviewColor(index)));
        }
    }

    private void DrawDirectoryColorIndex(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _directoryColorIndex.Items.Count
            || _directoryColorIndex.Items[e.Index] is not DirectoryColorItem item)
        {
            return;
        }

        var swatchSize = Math.Max(UiLayout.Scale(12), e.Bounds.Height - UiLayout.Scale(6));
        var swatch = new Rectangle(
            e.Bounds.Left + UiLayout.Scale(3),
            e.Bounds.Top + (e.Bounds.Height - swatchSize) / 2,
            swatchSize,
            swatchSize);
        using (var brush = new SolidBrush(item.Color))
        {
            e.Graphics.FillRectangle(brush, swatch);
        }
        e.Graphics.DrawRectangle(Pens.DimGray, swatch);

        var text = item.Index switch
        {
            0 => "0（随块）",
            256 => "256（随层）",
            _ => item.Index.ToString(CultureInfo.InvariantCulture)
        };
        var textColor = (e.State & DrawItemState.Selected) != 0
            ? SystemColors.HighlightText
            : SystemColors.ControlText;
        TextRenderer.DrawText(
            e.Graphics,
            text,
            e.Font ?? Font,
            new Point(swatch.Right + UiLayout.Scale(5), e.Bounds.Top + (e.Bounds.Height - Font.Height) / 2),
            textColor);
        e.DrawFocusRectangle();
    }

    private void DrawDirectoryTextStyle(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index >= 0 && e.Index < _directoryTextStyle.Items.Count)
        {
            var textColor = (e.State & DrawItemState.Selected) != 0
                ? SystemColors.HighlightText
                : SystemColors.ControlText;
            TextRenderer.DrawText(
                e.Graphics,
                _directoryTextStyle.Items[e.Index]?.ToString() ?? string.Empty,
                e.Font ?? Font,
                new Rectangle(
                    e.Bounds.Left + UiLayout.Scale(3),
                    e.Bounds.Top,
                    Math.Max(0, e.Bounds.Width - UiLayout.Scale(3)),
                    e.Bounds.Height),
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        e.DrawFocusRectangle();
    }

    private static Color GetAciPreviewColor(int index)
    {
        var fixedColors = new[]
        {
            Color.DimGray,
            Color.FromArgb(255, 0, 0),
            Color.FromArgb(255, 255, 0),
            Color.FromArgb(0, 255, 0),
            Color.FromArgb(0, 255, 255),
            Color.FromArgb(0, 0, 255),
            Color.FromArgb(255, 0, 255),
            Color.FromArgb(255, 255, 255),
            Color.FromArgb(128, 128, 128),
            Color.FromArgb(192, 192, 192)
        };
        if (index >= 0 && index < fixedColors.Length)
        {
            return fixedColors[index];
        }

        if (index >= 10 && index <= 249)
        {
            // ACI 10～249 每 10 个索引为一个色相组，偶数为纯色、奇数为同亮度的浅色。
            var hue = ((index - 10) / 10) * 15.0;
            var tone = (index - 10) % 10;
            var brightnessLevels = new[] { 255, 255, 165, 165, 127, 127, 76, 76, 38, 38 };
            var saturation = tone % 2 == 0 ? 1.0 : 0.5;
            return ColorFromHsv(hue, saturation, brightnessLevels[tone] / 255.0);
        }

        var grays = new[] { 51, 80, 105, 130, 190, 255 };
        if (index >= 250 && index <= 255)
        {
            var gray = grays[index - 250];
            return Color.FromArgb(gray, gray, gray);
        }

        return Color.DimGray;
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        var sector = hue / 60.0;
        var wholeSector = (int)Math.Floor(sector) % 6;
        var fraction = sector - Math.Floor(sector);
        var p = value * (1 - saturation);
        var q = value * (1 - fraction * saturation);
        var t = value * (1 - (1 - fraction) * saturation);
        var (red, green, blue) = wholeSector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };
        return Color.FromArgb(
            (int)Math.Round(red * 255),
            (int)Math.Round(green * 255),
            (int)Math.Round(blue * 255));
    }

    private sealed class DirectoryColorItem
    {
        public int Index { get; }
        public Color Color { get; }

        public DirectoryColorItem(int index, Color color)
        {
            Index = index;
            Color = color;
        }
    }

    private void ConfigureDirectoryColumnsGrid()
    {
        UiLayout.StyleGrid(_directoryColumnsGrid, Font);
        _directoryColumnsGrid.MultiSelect = false;
        _directoryColumnsGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _directoryColumnsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _directoryColumnsGrid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _directoryColumnsGrid.CellContentClick += DirectoryColumnsGridCellContentClick;
        _directoryColumnsGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_directoryColumnsGrid.IsCurrentCellDirty)
            {
                _directoryColumnsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _directoryColumnsGrid.CellValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryColumnsGrid.CellEndEdit += (_, _) => UpdateDirectoryPreview();
        _directoryColumnsGrid.DataError += (_, _) => { };

        _directoryColumnsGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "是否启用",
            Width = UiLayout.Scale(70)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Centered",
            HeaderText = "文字居中",
            Width = UiLayout.Scale(70)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Header",
            HeaderText = "目录列名",
            ReadOnly = true,
            Width = UiLayout.Scale(105)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Width",
            HeaderText = "目录列宽",
            Width = UiLayout.Scale(92)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "PickWidth",
            HeaderText = "设置列宽",
            Text = "图中交互",
            UseColumnTextForButtonValue = true,
            Width = UiLayout.Scale(88)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "MoveUp",
            HeaderText = "上移",
            Text = "上移",
            UseColumnTextForButtonValue = true,
            Width = UiLayout.Scale(56)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "MoveDown",
            HeaderText = "下移",
            Text = "下移",
            UseColumnTextForButtonValue = true,
            Width = UiLayout.Scale(56)
        });

        // 列顺序只允许通过“上移/下移”按钮改变，禁止点击表头触发隐式排序。
        foreach (DataGridViewColumn column in _directoryColumnsGrid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        }
    }

    private void DirectoryColumnsGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        var columnName = _directoryColumnsGrid.Columns[e.ColumnIndex].Name;
        if (columnName == "PickWidth")
        {
            RequestColumnWidthFromCad(e.RowIndex);
        }
        else if (columnName == "MoveUp")
        {
            MoveDirectoryColumn(e.RowIndex, -1);
        }
        else if (columnName == "MoveDown")
        {
            MoveDirectoryColumn(e.RowIndex, 1);
        }
    }

    private void MoveDirectoryColumn(int rowIndex, int offset)
    {
        var targetIndex = rowIndex + offset;
        if (rowIndex < 0 || targetIndex < 0 || targetIndex >= _directoryColumnsGrid.Rows.Count)
        {
            return;
        }

        // 直接移动整行可同时保留字段键、启用状态、对齐方式、固定列名和用户输入的列宽。
        var row = _directoryColumnsGrid.Rows[rowIndex];
        _directoryColumnsGrid.Rows.RemoveAt(rowIndex);
        _directoryColumnsGrid.Rows.Insert(targetIndex, row);
        _directoryColumnsGrid.ClearSelection();
        row.Selected = true;
        _directoryColumnsGrid.CurrentCell = row.Cells[2];
        UpdateDirectoryPreview();
    }

    private static TableLayoutPanel CreateSettingsTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows,
            Padding = new Padding(UiLayout.Scale(8))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(145)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void ConfigureNumber(NumericUpDown input, decimal min, decimal max, decimal increment, int decimals)
    {
        input.DecimalPlaces = decimals;
        input.Minimum = min;
        input.Maximum = max;
        input.Increment = increment;
        input.Dock = DockStyle.Left;
        input.Width = UiLayout.Scale(130);
    }


    private void LoadSettings()
    {
        Apply(AppSettingsStore.Load());
    }

    private void Apply(AppSettings settings)
    {
        _paperTolerance.Value = UiLayout.Clamp(_paperTolerance, settings.PaperMatchToleranceMm);
        _recognizeFourLineRectangleFrames.Checked = settings.RecognizeFourLineRectangleFrames;
        _hideFrameBoundaryWhenPlotting.Checked = settings.HideFrameBoundaryWhenPlotting;
        _addSequenceWhenPdfExists.Checked = settings.AddSequenceWhenPdfExists;
        _useFileNameAsPdfBookmark.Checked = settings.UseFileNameAsPdfBookmark;
        _mergePdfByPaperSize.Checked = settings.MergePdfByPaperSize;
        _openOutputDirectoryAfterBatchPrint.Checked = settings.OpenOutputDirectoryAfterBatchPrint;
        _openMergedPdfAfterMerge.Checked = settings.OpenMergedPdfAfterMerge;
        _generatePrintLog.Checked = settings.GeneratePrintLog;
        _fileNamePattern.Text = settings.PdfFileNamePattern;
        _fileNameSequenceStart.Value = Math.Max(
            _fileNameSequenceStart.Minimum,
            Math.Min(_fileNameSequenceStart.Maximum, settings.FileNameSequenceStartNumber));
        _fileNameSequenceDigits.Value = Math.Max(
            _fileNameSequenceDigits.Minimum,
            Math.Min(_fileNameSequenceDigits.Maximum, settings.FileNameSequenceDigits));
        _autoFileNameSequenceDigits.Checked = settings.AutoFileNameSequenceDigits;
        UpdateSequenceDigitsState();
        UpdateFileNamePreview();
        _openExternalDwgForPlot.Checked = settings.OpenExternalDwgForPlot;
        _directoryColorIndex.SelectedIndex = Math.Max(0, Math.Min(256, settings.DirectoryColorIndex));
        _directoryTextHeight.Value = UiLayout.Clamp(_directoryTextHeight, settings.DirectoryTextHeight);
        _directoryTextWidthFactor.Value = UiLayout.Clamp(_directoryTextWidthFactor, settings.DirectoryTextWidthFactor);
        _directoryRowHeight.Value = UiLayout.Clamp(_directoryRowHeight, settings.DirectoryRowHeight);
        _directoryLayerName.Text = settings.DirectoryLayerName;
        _directoryDrawHeader.Checked = settings.DirectoryDrawHeader;
        _directoryDrawGridLines.Checked = settings.DirectoryDrawGridLines;
        SelectTextStyle(settings.DirectoryTextStyleName);
        LoadDirectoryColumns(settings.DirectoryColumns);
        _longPaperNameFormat.SelectedIndex = Math.Max(0, Math.Min(5, (int)settings.LongPaperNameFormat));
        _longPaperSnapTolerance.Value = UiLayout.Clamp(
            _longPaperSnapTolerance,
            settings.LongPaperSnapToleranceMm);
        ReloadScaleList(settings);
    }

    private void SaveSettings()
    {
        if (!TryReadSettingsFromControls(out var current))
        {
            return;
        }

        AppSettingsStore.Save(current);
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool TryReadSettingsFromControls(out AppSettings current)
    {
        current = AppSettingsStore.Load();
        if (!TryReadDirectoryColumns(out var directoryColumns))
        {
            return false;
        }

        current.PaperMatchToleranceMm = (double)_paperTolerance.Value;
        current.RecognizeFourLineRectangleFrames = _recognizeFourLineRectangleFrames.Checked;
        current.HideFrameBoundaryWhenPlotting = _hideFrameBoundaryWhenPlotting.Checked;
        current.AddSequenceWhenPdfExists = _addSequenceWhenPdfExists.Checked;
        current.UseFileNameAsPdfBookmark = _useFileNameAsPdfBookmark.Checked;
        current.MergePdfByPaperSize = _mergePdfByPaperSize.Checked;
        current.OpenOutputDirectoryAfterBatchPrint = _openOutputDirectoryAfterBatchPrint.Checked;
        current.OpenMergedPdfAfterMerge = _openMergedPdfAfterMerge.Checked;
        current.GeneratePrintLog = _generatePrintLog.Checked;
        if (string.IsNullOrWhiteSpace(_fileNamePattern.Text))
        {
            MessageBox.Show("请输入文件命名规则。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        current.PdfFileNamePattern = _fileNamePattern.Text;
        current.FileNameSequenceStartNumber = (int)_fileNameSequenceStart.Value;
        current.FileNameSequenceDigits = (int)_fileNameSequenceDigits.Value;
        current.AutoFileNameSequenceDigits = _autoFileNameSequenceDigits.Checked;
        current.OpenExternalDwgForPlot = _openExternalDwgForPlot.Checked;
        current.DirectoryColorIndex = _directoryColorIndex.SelectedItem is DirectoryColorItem colorItem
            ? colorItem.Index
            : 7;
        current.DirectoryTextHeight = (double)_directoryTextHeight.Value;
        current.DirectoryTextWidthFactor = (double)_directoryTextWidthFactor.Value;
        current.DirectoryRowHeight = (double)_directoryRowHeight.Value;
        current.DirectoryTextHeightRatio = Math.Max(0.01, Math.Min(0.9, current.DirectoryTextHeight / current.DirectoryRowHeight));
        current.DirectoryTextStyleName = _directoryTextStyle.SelectedItem?.ToString() == DefaultTextStyleDisplay
            ? ""
            : _directoryTextStyle.SelectedItem?.ToString() ?? "";
        current.DirectoryLayerName = string.IsNullOrWhiteSpace(_directoryLayerName.Text) ? "0" : _directoryLayerName.Text.Trim();
        current.DirectoryDrawHeader = _directoryDrawHeader.Checked;
        current.DirectoryDrawGridLines = _directoryDrawGridLines.Checked;
        current.DirectoryColumns = directoryColumns;
        current.LongPaperNameFormat = (LongPaperNameFormat)Math.Max(0, Math.Min(5, _longPaperNameFormat.SelectedIndex));
        current.LongPaperSnapToleranceMm = (double)_longPaperSnapTolerance.Value;
        current.CustomScales = ReadCustomScalesFromList();
        return true;
    }

    private void LoadDirectoryColumns(IEnumerable<DirectoryColumnSetting> columns)
    {
        _directoryColumnsGrid.Rows.Clear();
        foreach (var column in columns)
        {
            var rowIndex = _directoryColumnsGrid.Rows.Add(
                column.Enabled,
                column.Centered,
                column.Header,
                column.Width.ToString("0.##", CultureInfo.CurrentCulture));
            _directoryColumnsGrid.Rows[rowIndex].Tag = column.Key;
        }
        UpdateDirectoryPreview();
    }

    private void UpdateDirectoryPreview()
    {
        if (_directoryColumnsGrid.Columns.Count == 0)
        {
            return;
        }

        var columns = new List<DirectoryPreviewColumn>();
        foreach (DataGridViewRow row in _directoryColumnsGrid.Rows)
        {
            if (!Convert.ToBoolean(row.Cells["Enabled"].Value ?? false))
            {
                continue;
            }

            var widthText = row.Cells["Width"].Value?.ToString() ?? "";
            if (!double.TryParse(widthText, NumberStyles.Float, CultureInfo.CurrentCulture, out var width)
                && !double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out width))
            {
                continue;
            }
            if (width <= 0)
            {
                continue;
            }

            columns.Add(new DirectoryPreviewColumn(
                row.Cells["Header"].Value?.ToString() ?? "",
                width,
                Convert.ToBoolean(row.Cells["Centered"].Value ?? false)));
        }

        var styleName = _directoryTextStyle.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(styleName) || styleName == DefaultTextStyleDisplay)
        {
            styleName = Font.Name;
        }
        _directoryOrderPreview.SetPreview(
            columns,
            (double)_directoryRowHeight.Value,
            (double)_directoryTextHeight.Value,
            (double)_directoryTextWidthFactor.Value,
            styleName ?? Font.Name);
    }

    private sealed class DirectoryPreviewColumn
    {
        public string Header { get; }
        public double Width { get; }
        public bool Centered { get; }

        public DirectoryPreviewColumn(string header, double width, bool centered)
        {
            Header = header;
            Width = width;
            Centered = centered;
        }
    }

    private sealed class DirectoryPreviewControl : Control
    {
        private IReadOnlyList<DirectoryPreviewColumn> _columns = Array.Empty<DirectoryPreviewColumn>();
        private double _rowHeight = 1;
        private double _textHeight = 1;
        private double _textWidthFactor = 0.7;
        private string _fontName = "宋体";

        public DirectoryPreviewControl()
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            BackColor = Color.White;
            Margin = Padding.Empty;
        }

        public void SetPreview(
            IReadOnlyList<DirectoryPreviewColumn> columns,
            double rowHeight,
            double textHeight,
            double textWidthFactor,
            string fontName)
        {
            _columns = columns.ToList();
            _rowHeight = Math.Max(1, rowHeight);
            _textHeight = Math.Max(1, textHeight);
            _textWidthFactor = Math.Max(0.1, textWidthFactor);
            _fontName = string.IsNullOrWhiteSpace(fontName) ? "宋体" : fontName;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (_columns.Count == 0)
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    "请勾选需要生成的目录列",
                    Font,
                    ClientRectangle,
                    Color.DimGray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var totalWidth = _columns.Sum(x => x.Width);
            if (totalWidth <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            var padding = UiLayout.Scale(6);
            var availableWidth = Math.Max(1, ClientSize.Width - padding * 2);
            var availableHeight = Math.Max(1, ClientSize.Height - padding * 2);
            // 列宽和行高共用同一个缩放比例，保证预览中的长宽关系与最终 CAD 目录完全一致。
            var scale = Math.Min(availableWidth / totalWidth, availableHeight / _rowHeight);
            var previewWidth = (float)(totalWidth * scale);
            var previewHeight = (float)(_rowHeight * scale);
            var x = (ClientSize.Width - previewWidth) / 2f;
            var y = (ClientSize.Height - previewHeight) / 2f;

            using var linePen = new Pen(Color.FromArgb(70, 70, 70), Math.Max(1, UiLayout.Scale(1)));
            foreach (var column in _columns)
            {
                var cellWidth = (float)(column.Width * scale);
                var cell = new RectangleF(x, y, cellWidth, previewHeight);
                e.Graphics.DrawRectangle(linePen, cell.X, cell.Y, cell.Width, cell.Height);

                // 与目录生成逻辑保持相同的行高和列宽限幅，预览字高即最终实际可用字高的等比结果。
                var byRow = _rowHeight * 0.8;
                var byWidth = column.Width * 0.9 / Math.Max(1, column.Header.Length * _textWidthFactor);
                var fontPixels = (float)(Math.Max(1, Math.Min(_textHeight, Math.Min(byRow, byWidth))) * scale);
                using var previewFont = CreatePreviewFont(_fontName, Math.Max(1, fontPixels), Font);
                using var format = new StringFormat
                {
                    Alignment = column.Centered ? StringAlignment.Center : StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                var textCell = RectangleF.Inflate(cell, -Math.Min(UiLayout.Scale(4), cell.Width * 0.04f), 0);
                e.Graphics.DrawString(column.Header, previewFont, Brushes.Black, textCell, format);
                x += cellWidth;
            }
        }

        private static System.Drawing.Font CreatePreviewFont(string fontName, float size, System.Drawing.Font fallback)
        {
            try
            {
                return new System.Drawing.Font(fontName, size, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch
            {
                return new System.Drawing.Font(fallback.FontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
            }
        }
    }

    private bool TryReadDirectoryColumns(out List<DirectoryColumnSetting> columns)
    {
        columns = new List<DirectoryColumnSetting>();
        _directoryColumnsGrid.EndEdit();
        foreach (DataGridViewRow row in _directoryColumnsGrid.Rows)
        {
            var key = row.Tag?.ToString() ?? "";
            var header = row.Cells["Header"].Value?.ToString()?.Trim() ?? "";
            var widthText = row.Cells["Width"].Value?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(header))
            {
                MessageBox.Show("目录列名不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _directoryColumnsGrid.CurrentCell = row.Cells["Header"];
                return false;
            }

            if (!double.TryParse(widthText, NumberStyles.Float, CultureInfo.CurrentCulture, out var width)
                && !double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out width))
            {
                width = 0;
            }
            if (width <= 0)
            {
                MessageBox.Show($"目录列“{header}”的列宽必须大于 0。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _directoryColumnsGrid.CurrentCell = row.Cells["Width"];
                return false;
            }

            columns.Add(new DirectoryColumnSetting
            {
                Key = key,
                Header = header,
                Enabled = Convert.ToBoolean(row.Cells["Enabled"].Value ?? false),
                Centered = Convert.ToBoolean(row.Cells["Centered"].Value ?? false),
                Width = width
            });
        }

        if (!columns.Any(x => x.Enabled))
        {
            MessageBox.Show("请至少启用一个目录字段。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    private void LoadTextStyles()
    {
        _directoryTextStyle.Items.Clear();
        _directoryTextStyle.Items.Add(DefaultTextStyleDisplay);

        var document = GetActiveDocument();
        if (document != null)
        {
            try
            {
                using var tr = document.Database.TransactionManager.StartTransaction();
                var table = (TextStyleTable)tr.GetObject(document.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var record = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!string.IsNullOrWhiteSpace(record.Name))
                    {
                        _directoryTextStyle.Items.Add(record.Name);
                    }
                }

                tr.Commit();
            }
            catch
            {
            }
        }

        // 即使当前图纸尚未建立“宋体”文字样式，也先在界面提供该默认项；生成目录时会在本图内自动创建。
        if (!_directoryTextStyle.Items.Cast<object>().Any(x =>
            string.Equals(x?.ToString(), "宋体", StringComparison.OrdinalIgnoreCase)))
        {
            _directoryTextStyle.Items.Insert(1, "宋体");
        }

        if (_directoryTextStyle.Items.Count > 0)
        {
            _directoryTextStyle.SelectedIndex = 0;
        }
    }

    private void SelectTextStyle(string? name)
    {
        var target = string.IsNullOrWhiteSpace(name) ? DefaultTextStyleDisplay : name;
        for (var i = 0; i < _directoryTextStyle.Items.Count; i++)
        {
            if (string.Equals(_directoryTextStyle.Items[i]?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                _directoryTextStyle.SelectedIndex = i;
                return;
            }
        }

        if (_directoryTextStyle.Items.Count > 0)
        {
            _directoryTextStyle.SelectedIndex = 0;
        }
    }

    private void RequestColumnWidthFromCad(int rowIndex)
    {
        if (GetActiveDocument() == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (rowIndex < 0 || rowIndex >= _directoryColumnsGrid.Rows.Count
            || !TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        var key = _directoryColumnsGrid.Rows[rowIndex].Tag?.ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        // CAD 取点必须在设置窗体关闭后执行；先保存全部未提交编辑，再由调用方回到命令上下文框选。
        AppSettingsStore.Save(settings);
        RequestedDirectoryColumnKey = key;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RequestRowHeightFromCad()
    {
        if (GetActiveDocument() == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        // 与列宽交互一致，先保存当前页面编辑，再关闭模态窗体回到 CAD 命令上下文量取高度。
        AppSettingsStore.Save(settings);
        RequestPickDirectoryRowHeight = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RequestTextAppearanceFromCad()
    {
        if (GetActiveDocument() == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        // 和列宽/行高一致：先保存页面编辑并退出模态窗体，再回到 CAD 命令上下文点选实体。
        AppSettingsStore.Save(settings);
        RequestPickDirectoryTextAppearance = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private Document? GetActiveDocument()
    {
        try
        {
            // 不缓存 ObjectId 或旧文档：每次按钮状态/点选请求都以当前 MDI 活动图纸为准。
            return CadApp.DocumentManager.MdiActiveDocument;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SelectedTabIndex = _tabs.SelectedIndex;
        InitialTabIndex = SelectedTabIndex;
        base.OnFormClosing(e);
    }

    private void ResetDefaults()
    {
        Apply(AppSettingsStore.Default());
    }
}
