using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

public sealed class BatchPlotForm : Form
{
    private readonly Document _currentDocument;
    private readonly BindingList<PlotJob> _jobs = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _outputDirectory = new();
    private readonly ComboBox _deviceCombo = new();
    private readonly ComboBox _styleCombo = new();
    private readonly Button _printButton = new();
    private readonly Label _statusLabel = new();
    private readonly List<string> _logLines = new();
    private readonly List<string> _selectedDwgFiles = new();
    private readonly AppSettings _settings;
    private string _lastLogPath = "";
    public bool HasPendingPrint { get; private set; }

    public BatchPlotForm(Document currentDocument)
    {
        _currentDocument = currentDocument;
        _settings = AppSettingsStore.Load();
        InitializeComponents();
        LoadPlotOptions();
        if (_settings.AutoScanCurrentDrawing)
        {
            ScanCurrentDrawing();
        }
        else
        {
            RefreshStatus();
        }
    }

    private void InitializeComponents()
    {
        Text = "批量打印";
        UiLayout.ConfigureForm(this, 1320, 820, 1080, 680);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = UiLayout.Scale(160),
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), UiLayout.Scale(10), UiLayout.Scale(6)),
            BackColor = SystemColors.Control
        };
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.ButtonHeight() + UiLayout.Scale(12)));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.ButtonHeight() + UiLayout.Scale(18)));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.ButtonHeight() + UiLayout.Scale(8)));

        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty
        };

        var settingsRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiLayout.Scale(6), 0, 0)
        };
        var pathRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty
        };
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(48)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("浏览...", 84) + UiLayout.Scale(8)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(70)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(260)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(48)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(210)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("开始打印", 104) + UiLayout.Scale(10)));

        Button MakeButton(string text, int width)
        {
            return UiLayout.CreateButton(text, width);
        }

        Label MakeLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            };
        }

        var scanButton = MakeButton("扫描当前图", 108);
        scanButton.Click += (_, _) => ScanCurrentDrawing();

        var scanWindowButton = MakeButton("框选扫描", 92);
        scanWindowButton.Click += (_, _) => ScanSelectedWindow();

        var addFilesButton = MakeButton("添加DWG", 92);
        addFilesButton.Click += (_, _) => AddDwgFiles();

        var selectAllButton = MakeButton("全选", 68);
        selectAllButton.Click += (_, _) => SetAllSelected(true);

        var selectNoneButton = MakeButton("全不选", 78);
        selectNoneButton.Click += (_, _) => SetAllSelected(false);

        var invertButton = MakeButton("反选", 68);
        invertButton.Click += (_, _) => InvertSelected();

        var removeButton = MakeButton("删除选中", 92);
        removeButton.Click += (_, _) => RemoveGridSelection();

        var refreshNameButton = MakeButton("刷新文件名", 104);
        refreshNameButton.Click += (_, _) => SortAndRefreshOutputPaths();

        var exportCsvButton = MakeButton("导出清单", 92);
        exportCsvButton.Click += (_, _) => ExportCsv();

        var openLogButton = MakeButton("打开日志", 92);
        openLogButton.Click += (_, _) => OpenLastLog();

        var manageLibraryButton = MakeButton("图框库管理", 104);
        manageLibraryButton.Click += (_, _) => ManageLibrary();

        var settingsButton = MakeButton("设置", 72);
        settingsButton.Click += (_, _) => ShowSettings();

        var chooseOutputButton = MakeButton("浏览...", 84);
        chooseOutputButton.Click += (_, _) => ChooseOutputDirectory();

        var currentFolderButton = MakeButton("当前文件夹", 96);
        currentFolderButton.Click += (_, _) => SetOutputDirectory(GetSelectedCadDirectory());

        var currentPdfButton = MakeButton("当前文件夹/PDF", 116);
        currentPdfButton.Click += (_, _) => SetOutputDirectory(Path.Combine(GetSelectedCadDirectory(), "PDF"));

        var specifiedFolderButton = MakeButton("指定文件夹", 96);
        specifiedFolderButton.Click += (_, _) => ChooseOutputDirectory();

        var importButton = MakeButton("导入图框库", 104);
        importButton.Click += (_, _) => ImportLibrary();

        var exportButton = MakeButton("导出图框库", 104);
        exportButton.Click += (_, _) => ExportLibrary();

        _printButton.Text = "开始打印";
        _printButton.Width = UiLayout.ButtonWidth(_printButton.Text, 104);
        _printButton.Height = UiLayout.ButtonHeight();
        _printButton.Dock = DockStyle.Fill;
        _printButton.Margin = new Padding(UiLayout.Scale(8), UiLayout.Scale(2), 0, UiLayout.Scale(6));
        _printButton.Click += (_, _) => PrintSelectedJobs();

        _outputDirectory.Dock = DockStyle.Fill;
        _outputDirectory.Margin = new Padding(0, UiLayout.Scale(4), UiLayout.Scale(8), UiLayout.Scale(8));
        _outputDirectory.Text = GetDefaultOutputDirectory();

        _deviceCombo.Dock = DockStyle.Fill;
        _deviceCombo.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(10), UiLayout.Scale(8));
        _deviceCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        _styleCombo.Dock = DockStyle.Fill;
        _styleCombo.Margin = new Padding(0, UiLayout.Scale(3), 0, UiLayout.Scale(8));
        _styleCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        actionRow.Controls.Add(scanButton);
        actionRow.Controls.Add(scanWindowButton);
        actionRow.Controls.Add(addFilesButton);
        actionRow.Controls.Add(selectAllButton);
        actionRow.Controls.Add(selectNoneButton);
        actionRow.Controls.Add(invertButton);
        actionRow.Controls.Add(removeButton);
        actionRow.Controls.Add(refreshNameButton);
        actionRow.Controls.Add(exportCsvButton);
        actionRow.Controls.Add(openLogButton);
        actionRow.Controls.Add(manageLibraryButton);
        actionRow.Controls.Add(settingsButton);
        actionRow.Controls.Add(importButton);
        actionRow.Controls.Add(exportButton);

        settingsRow.Controls.Add(MakeLabel("输出:"), 0, 0);
        settingsRow.Controls.Add(_outputDirectory, 1, 0);
        settingsRow.Controls.Add(chooseOutputButton, 2, 0);
        settingsRow.Controls.Add(MakeLabel("打印机:"), 3, 0);
        settingsRow.Controls.Add(_deviceCombo, 4, 0);
        settingsRow.Controls.Add(MakeLabel("CTB:"), 5, 0);
        settingsRow.Controls.Add(_styleCombo, 6, 0);
        settingsRow.Controls.Add(_printButton, 7, 0);

        top.Controls.Add(actionRow, 0, 0);
        top.Controls.Add(settingsRow, 0, 1);
        pathRow.Controls.Add(new Label
        {
            Text = "保存路径快捷:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, UiLayout.Scale(8), UiLayout.Scale(8), 0)
        });
        pathRow.Controls.Add(currentFolderButton);
        pathRow.Controls.Add(currentPdfButton);
        pathRow.Controls.Add(specifiedFolderButton);
        top.Controls.Add(pathRow, 0, 2);

        UiLayout.StyleGrid(_grid, Font);
        AddColumns();
        _grid.DataSource = _jobs;
        _grid.CellEndEdit += GridCellEndEdit;

        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Height = Math.Max(UiLayout.Scale(28), Font.Height + UiLayout.Scale(10));
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Padding = new Padding(UiLayout.Scale(8), 0, 0, 0);
        _statusLabel.BackColor = SystemColors.Control;

        Controls.Add(_grid);
        Controls.Add(_statusLabel);
        Controls.Add(top);
    }

    private void AddColumns()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(PlotJob.Selected), HeaderText = "打印", Width = UiLayout.Scale(58) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.DrawingNumber), HeaderText = "图号", Width = UiLayout.Scale(160) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.Title), HeaderText = "图名", Width = UiLayout.Scale(240) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.PaperName), HeaderText = "图幅", Width = UiLayout.Scale(82) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.ScaleText), HeaderText = "比例", Width = UiLayout.Scale(82) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.SizeText), HeaderText = "实际尺寸", Width = UiLayout.Scale(150) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.PaperSizeText), HeaderText = "输出纸张", Width = UiLayout.Scale(150) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.BlockName), HeaderText = "块名", Width = UiLayout.Scale(150) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.SpaceName), HeaderText = "空间", Width = UiLayout.Scale(110) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.SourceFile), HeaderText = "文件", Width = UiLayout.Scale(320) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.OutputPath), HeaderText = "输出PDF", Width = UiLayout.Scale(360) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.DetectionNote), HeaderText = "识别说明", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = UiLayout.Scale(220) });
    }

    private void LoadPlotOptions()
    {
        try
        {
            var settings = new PlotSettings(true);
            var validator = PlotSettingsValidator.Current;
            foreach (string device in validator.GetPlotDeviceList())
            {
                _deviceCombo.Items.Add(device);
            }

            foreach (string style in validator.GetPlotStyleSheetList())
            {
                if (style.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase))
                {
                    _styleCombo.Items.Add(style);
                }
            }

            SelectExactOrContaining(_deviceCombo, _settings.LastPlotDevice, "PDF");
            SelectExactOrContaining(_styleCombo, _settings.LastStyleSheet, "monochrome");
            if (_styleCombo.SelectedIndex < 0 && _styleCombo.Items.Count > 0)
            {
                _styleCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("读取打印设备/CTB失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void SelectExactOrContaining(ComboBox combo, string exactValue, string fallbackContains)
    {
        if (!string.IsNullOrWhiteSpace(exactValue))
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                if (string.Equals(combo.Items[i]?.ToString(), exactValue, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }

        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i]?.ToString()?.IndexOf(fallbackContains, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void ScanCurrentDrawing()
    {
        var library = TitleBlockLibraryStore.Load();
        if (library.Blocks.Count == 0)
        {
            MessageBox.Show("图框库为空。请先从“批量打印”菜单点击“新增图框”。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshStatus();
            return;
        }

        _jobs.Clear();
        foreach (var job in TitleBlockScanner.Scan(_currentDocument, library))
        {
            _jobs.Add(job);
        }

        SortAndRefreshOutputPaths();
        AppendLog("INFO", $"扫描当前图完成，识别 {_jobs.Count} 张。");
    }

    private void ScanSelectedWindow()
    {
        var library = TitleBlockLibraryStore.Load();
        if (library.Blocks.Count == 0)
        {
            MessageBox.Show("图框库为空，请先新增图框。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Hide();
        System.Windows.Forms.Application.DoEvents();
        try
        {
            var editor = _currentDocument.Editor;
            var first = editor.GetPoint(new PromptPointOptions("\n框选扫描范围第一个角点: "));
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var second = editor.GetCorner(new PromptCornerOptions("\n框选扫描范围对角点: ", first.Value));
            if (second.Status != PromptStatus.OK)
            {
                return;
            }

            var window = new Extents3d(
                new Point3d(Math.Min(first.Value.X, second.Value.X), Math.Min(first.Value.Y, second.Value.Y), 0),
                new Point3d(Math.Max(first.Value.X, second.Value.X), Math.Max(first.Value.Y, second.Value.Y), 0));

            _jobs.Clear();
            foreach (var job in TitleBlockScanner.Scan(_currentDocument, library, window))
            {
                _jobs.Add(job);
            }

            SortAndRefreshOutputPaths();
            AppendLog("INFO", $"框选扫描当前图完成，识别 {_jobs.Count} 张。");
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void AddDwgFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "DWG 文件 (*.dwg)|*.dwg",
            Multiselect = true,
            Title = "选择需要批量打印的 DWG"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        foreach (var file in dialog.FileNames)
        {
            var fullPath = Path.GetFullPath(file);
            if (!_selectedDwgFiles.Any(x => string.Equals(x, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedDwgFiles.Add(fullPath);
            }
        }

        var library = TitleBlockLibraryStore.Load();
        var added = new List<PlotJob>();
        var errors = new List<string>();

        foreach (var file in dialog.FileNames)
        {
            try
            {
                var scanned = ScanExternalFile(file, library);
                added.AddRange(scanned);
                AppendLog("INFO", $"扫描 {file}，识别 {scanned.Count} 张。");
            }
            catch (Exception ex)
            {
                var message = $"{file}: {ex.Message}";
                errors.Add(message);
                AppendLog("ERROR", "扫描失败，" + message);
            }
        }

        foreach (var job in added)
        {
            _jobs.Add(job);
        }

        SortAndRefreshOutputPaths();
        if (errors.Count > 0)
        {
            MessageBox.Show("部分 DWG 扫描失败:\n" + string.Join("\n", errors), "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private List<PlotJob> ScanExternalFile(string file, TitleBlockLibrary library)
    {
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(_currentDocument.Database.Filename), StringComparison.OrdinalIgnoreCase))
        {
            return TitleBlockScanner.Scan(_currentDocument, library);
        }

        using var db = new Database(false, true);
        db.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        return TitleBlockScanner.Scan(db, library, file);
    }

    private void SortAndRefreshOutputPaths()
    {
        var sorted = _jobs
            .OrderBy(x => x.DrawingNumber, NaturalStringComparer.Instance)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _jobs.Clear();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in sorted)
        {
            job.OutputPath = BuildOutputPath(job, reservedPaths);
            _jobs.Add(job);
        }

        RefreshStatus();
    }

    private void SetAllSelected(bool selected)
    {
        _grid.EndEdit();
        foreach (var job in _jobs)
        {
            job.Selected = selected;
        }

        _grid.Refresh();
        RefreshStatus();
    }

    private void InvertSelected()
    {
        _grid.EndEdit();
        foreach (var job in _jobs)
        {
            job.Selected = !job.Selected;
        }

        _grid.Refresh();
        RefreshStatus();
    }

    private void RemoveGridSelection()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return;
        }

        var selected = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<PlotJob>()
            .ToList();

        foreach (var job in selected)
        {
            _jobs.Remove(job);
        }

        SortAndRefreshOutputPaths();
    }

    private string BuildOutputPath(PlotJob job, ISet<string> reservedPaths)
    {
        var baseName = $"{job.DrawingNumber}_{job.Title}";
        return FileNameSanitizer.MakeUnique(_outputDirectory.Text, baseName, reservedPaths, _settings.AddSequenceWhenPdfExists);
    }

    private string GetDefaultOutputDirectory()
    {
        if (_settings.RememberLastOutputDirectory && !string.IsNullOrWhiteSpace(_settings.LastOutputDirectory))
        {
            return _settings.LastOutputDirectory;
        }

        var file = _currentDocument.Database.Filename;
        var directory = string.IsNullOrWhiteSpace(file)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.GetDirectoryName(file) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return Path.Combine(directory, _settings.DefaultOutputSubfolder);
    }

    private void ChooseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 PDF 输出目录",
            SelectedPath = _outputDirectory.Text
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputDirectory.Text = dialog.SelectedPath;
            SaveCurrentSettings();
            SortAndRefreshOutputPaths();
            AppendLog("INFO", "输出目录切换为 " + dialog.SelectedPath);
        }
    }

    private string GetSelectedCadDirectory()
    {
        var selectedFile = _selectedDwgFiles.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(selectedFile))
        {
            return Path.GetDirectoryName(selectedFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var firstJobFile = _jobs
            .Select(x => x.SourceFile)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x));
        if (!string.IsNullOrWhiteSpace(firstJobFile))
        {
            return Path.GetDirectoryName(firstJobFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var file = _currentDocument.Database.Filename;
        return string.IsNullOrWhiteSpace(file)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.GetDirectoryName(file) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void SetOutputDirectory(string directory)
    {
        _outputDirectory.Text = directory;
        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        AppendLog("INFO", "输出目录切换为 " + directory);
    }

    private void ExportCsv()
    {
        if (_jobs.Count == 0)
        {
            MessageBox.Show("当前没有可导出的图纸清单。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "CSV 清单 (*.csv)|*.csv",
            FileName = "批量打印清单.csv",
            Title = "导出批量打印清单"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        CsvExporter.ExportJobs(dialog.FileName, _jobs);
        AppendLog("INFO", "导出清单 " + dialog.FileName);
        MessageBox.Show("清单已导出。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ManageLibrary()
    {
        using var form = new TitleBlockLibraryManagerForm();
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            ScanCurrentDrawing();
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm();
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            var updated = AppSettingsStore.Load();
            _settings.RememberLastOutputDirectory = updated.RememberLastOutputDirectory;
            _settings.DefaultOutputSubfolder = updated.DefaultOutputSubfolder;
            _settings.AutoScanCurrentDrawing = updated.AutoScanCurrentDrawing;
            _settings.PaperMatchToleranceMm = updated.PaperMatchToleranceMm;
            _settings.AllowStandardPaperNameFallback = updated.AllowStandardPaperNameFallback;
            _settings.ShowPlotProgress = updated.ShowPlotProgress;
            _settings.AddSequenceWhenPdfExists = updated.AddSequenceWhenPdfExists;
            _settings.OpenExternalDwgForPlot = updated.OpenExternalDwgForPlot;
            SortAndRefreshOutputPaths();
            AppendLog("INFO", "设置已更新。");
        }
    }

    private void OpenLastLog()
    {
        if (string.IsNullOrWhiteSpace(_lastLogPath) || !File.Exists(_lastLogPath))
        {
            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
        }

        Process.Start(_lastLogPath);
    }

    private void ImportLibrary()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            Title = "导入图框库"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var library = TitleBlockLibraryStore.Load(dialog.FileName);
        TitleBlockLibraryStore.Save(library);
        MessageBox.Show("图框库已导入。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExportLibrary()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            FileName = "TitleBlockLibrary.json",
            Title = "导出图框库"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        TitleBlockLibraryStore.Save(TitleBlockLibraryStore.Load(), dialog.FileName);
        MessageBox.Show("图框库已导出。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void PrintSelectedJobs()
    {
        var selected = _jobs.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("没有勾选任何图纸。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var device = _deviceCombo.SelectedItem?.ToString() ?? "";
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show("请选择 PDF 打印机。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Directory.CreateDirectory(_outputDirectory.Text);
        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        HasPendingPrint = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    public void ExecutePendingPrint()
    {
        if (!HasPendingPrint)
        {
            return;
        }

        var selected = _jobs.Where(x => x.Selected).ToList();
        var device = _deviceCombo.SelectedItem?.ToString() ?? "";
        var style = _styleCombo.SelectedItem?.ToString() ?? "";

        _printButton.Enabled = false;
        var wasVisible = Visible;
        if (_settings.OpenExternalDwgForPlot)
        {
            Hide();
            System.Windows.Forms.Application.DoEvents();
        }

        try
        {
            var failed = new List<string>();
            var results = PlotterService.PlotMany(
                selected,
                device,
                style,
                _currentDocument,
                _settings,
                job => AppendLog("INFO", $"开始打印 {job.DrawingNumber}_{job.Title} -> {job.OutputPath}"));

            foreach (var result in results)
            {
                var job = result.Job;
                if (result.Succeeded)
                {
                    AppendLog("INFO", $"打印成功 {job.OutputPath}");
                    continue;
                }

                var ex = result.Error!;
                var message = $"{job.DrawingNumber}_{job.Title}: {ex.Message}";
                failed.Add(message);
                AppendLog("ERROR", ex.ToString());
                AppendLog("ERROR", "打印失败，" + message);
            }

            foreach (var skipped in selected.Except(results.Select(x => x.Job)))
            {
                var message = $"{skipped.DrawingNumber}_{skipped.Title}: 文件打开失败，未开始打印。";
                failed.Add(message);
                AppendLog("ERROR", message);
            }

            var printed = results.Count(x => x.Succeeded);
            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
            var summary = $"打印完成: 成功 {printed} 张，失败 {failed.Count} 张。\n日志: {_lastLogPath}";
            if (failed.Count > 0)
            {
                summary += "\n\n失败项:\n" + string.Join("\n", failed);
            }

            MessageBox.Show(summary, "批量打印", MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("打印失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (wasVisible && !Visible)
            {
                Show();
                Activate();
            }

            _printButton.Enabled = true;
            RefreshStatus();
        }
    }

    private void GridCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        if (_grid.Rows[e.RowIndex].DataBoundItem is not PlotJob job)
        {
            return;
        }

        var property = _grid.Columns[e.ColumnIndex].DataPropertyName;
        var titleChanged = property == nameof(PlotJob.Title) && !string.Equals(job.Title, job.CadTitle, StringComparison.Ordinal);
        var numberChanged = property == nameof(PlotJob.DrawingNumber) && !string.Equals(job.DrawingNumber, job.CadDrawingNumber, StringComparison.Ordinal);
        if (!titleChanged && !numberChanged)
        {
            return;
        }

        var ok = CadTextUpdater.TryUpdateOpenDocument(
            job,
            titleChanged ? job.Title : null,
            numberChanged ? job.DrawingNumber : null,
            _currentDocument,
            out var message);

        if (ok)
        {
            if (titleChanged)
            {
                job.CadTitle = job.Title;
            }

            if (numberChanged)
            {
                job.CadDrawingNumber = job.DrawingNumber;
            }
        }

        AppendLog(ok ? "INFO" : "WARN", message);
        SortAndRefreshOutputPaths();
    }

    private void ExecutePendingPrintLegacy()
    {
        if (!HasPendingPrint)
        {
            return;
        }

        var selected = _jobs.Where(x => x.Selected).ToList();
        var device = _deviceCombo.SelectedItem?.ToString() ?? "";
        var style = _styleCombo.SelectedItem?.ToString() ?? "";

        _printButton.Enabled = false;
        var wasVisible = Visible;
        if (_settings.OpenExternalDwgForPlot)
        {
            Hide();
            System.Windows.Forms.Application.DoEvents();
        }
        try
        {
            var printed = 0;
            var failed = new List<string>();
            foreach (var job in selected)
            {
                try
                {
                    AppendLog("INFO", $"开始打印 {job.DrawingNumber}_{job.Title} -> {job.OutputPath}");
                    PlotterService.Plot(job, device, style, _currentDocument, _settings);
                    AppendLog("INFO", $"打印成功 {job.OutputPath}");
                    printed++;
                }
                catch (Exception ex)
                {
                    var message = $"{job.DrawingNumber}_{job.Title}: {ex.Message}";
                    failed.Add(message);
                    AppendLog("ERROR", ex.ToString());
                    AppendLog("ERROR", "打印失败，" + message);
                }
            }

            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
            var summary = $"打印完成: 成功 {printed} 张，失败 {failed.Count} 张。\n日志: {_lastLogPath}";
            if (failed.Count > 0)
            {
                summary += "\n\n失败项:\n" + string.Join("\n", failed);
            }

            MessageBox.Show(summary, "批量打印", MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("打印失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (wasVisible && !Visible)
            {
                Show();
                Activate();
            }

            _printButton.Enabled = true;
            RefreshStatus();
        }
    }

    private void AppendLog(string level, string message)
    {
        _logLines.Add(BatchPlotLogger.Format(level, message));
    }

    private void RefreshStatus()
    {
        var selected = _jobs.Count(x => x.Selected);
        _statusLabel.Text = $"共 {_jobs.Count} 张，已勾选 {selected} 张。图框库: {TitleBlockLibraryStore.DefaultPath}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveCurrentSettings();
        base.OnFormClosing(e);
    }

    private void SaveCurrentSettings()
    {
        _settings.LastOutputDirectory = _outputDirectory.Text;
        _settings.LastPlotDevice = _deviceCombo.SelectedItem?.ToString() ?? "";
        _settings.LastStyleSheet = _styleCombo.SelectedItem?.ToString() ?? "";
        AppSettingsStore.Save(_settings);
    }
}
