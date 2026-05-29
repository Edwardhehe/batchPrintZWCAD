using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
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
    private readonly AppSettings _settings;
    private string _lastLogPath = "";

    public BatchPlotForm(Document currentDocument)
    {
        _currentDocument = currentDocument;
        _settings = AppSettingsStore.Load();
        InitializeComponents();
        LoadPlotOptions();
        ScanCurrentDrawing();
    }

    private void InitializeComponents()
    {
        Text = "批量打印";
        Width = 1160;
        Height = 760;
        StartPosition = FormStartPosition.CenterParent;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 124,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(8)
        };

        var scanButton = new Button { Text = "扫描当前图", Width = 100 };
        scanButton.Click += (_, _) => ScanCurrentDrawing();

        var addFilesButton = new Button { Text = "添加DWG", Width = 88 };
        addFilesButton.Click += (_, _) => AddDwgFiles();

        var selectAllButton = new Button { Text = "全选", Width = 64 };
        selectAllButton.Click += (_, _) => SetAllSelected(true);

        var selectNoneButton = new Button { Text = "全不选", Width = 76 };
        selectNoneButton.Click += (_, _) => SetAllSelected(false);

        var invertButton = new Button { Text = "反选", Width = 64 };
        invertButton.Click += (_, _) => InvertSelected();

        var removeButton = new Button { Text = "删除选中", Width = 88 };
        removeButton.Click += (_, _) => RemoveGridSelection();

        var refreshNameButton = new Button { Text = "刷新文件名", Width = 100 };
        refreshNameButton.Click += (_, _) => SortAndRefreshOutputPaths();

        var exportCsvButton = new Button { Text = "导出清单", Width = 88 };
        exportCsvButton.Click += (_, _) => ExportCsv();

        var openLogButton = new Button { Text = "打开日志", Width = 88 };
        openLogButton.Click += (_, _) => OpenLastLog();

        var chooseOutputButton = new Button { Text = "输出目录", Width = 88 };
        chooseOutputButton.Click += (_, _) => ChooseOutputDirectory();

        var importButton = new Button { Text = "导入图框库", Width = 100 };
        importButton.Click += (_, _) => ImportLibrary();

        var exportButton = new Button { Text = "导出图框库", Width = 100 };
        exportButton.Click += (_, _) => ExportLibrary();

        _printButton.Text = "开始打印";
        _printButton.Width = 100;
        _printButton.Click += (_, _) => PrintSelectedJobs();

        _outputDirectory.Width = 360;
        _outputDirectory.Text = GetDefaultOutputDirectory();

        _deviceCombo.Width = 230;
        _styleCombo.Width = 190;

        top.Controls.Add(scanButton);
        top.Controls.Add(addFilesButton);
        top.Controls.Add(selectAllButton);
        top.Controls.Add(selectNoneButton);
        top.Controls.Add(invertButton);
        top.Controls.Add(removeButton);
        top.Controls.Add(refreshNameButton);
        top.Controls.Add(exportCsvButton);
        top.Controls.Add(openLogButton);
        top.Controls.Add(new Label { Text = "输出:", AutoSize = true, Padding = new Padding(8, 8, 0, 0) });
        top.Controls.Add(_outputDirectory);
        top.Controls.Add(chooseOutputButton);
        top.Controls.Add(new Label { Text = "打印机:", AutoSize = true, Padding = new Padding(8, 8, 0, 0) });
        top.Controls.Add(_deviceCombo);
        top.Controls.Add(new Label { Text = "CTB:", AutoSize = true, Padding = new Padding(8, 8, 0, 0) });
        top.Controls.Add(_styleCombo);
        top.Controls.Add(_printButton);
        top.Controls.Add(importButton);
        top.Controls.Add(exportButton);

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.DataSource = _jobs;
        AddColumns();

        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Height = 26;
        _statusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _statusLabel.Padding = new Padding(8, 0, 0, 0);

        Controls.Add(_grid);
        Controls.Add(_statusLabel);
        Controls.Add(top);
    }

    private void AddColumns()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(PlotJob.Selected), HeaderText = "打印", Width = 52 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.DrawingNumber), HeaderText = "图号", Width = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.Title), HeaderText = "图名", Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.PaperName), HeaderText = "图幅", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.ScaleText), HeaderText = "比例", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.SizeText), HeaderText = "实际尺寸", Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.BlockName), HeaderText = "块名", Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.SpaceName), HeaderText = "空间", Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.SourceFile), HeaderText = "文件", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.OutputPath), HeaderText = "输出PDF", Width = 260 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(PlotJob.DetectionNote), HeaderText = "识别说明", Width = 200 });
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
            MessageBox.Show("图框库为空。请先运行 BPADD 新增图框块。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        var doc = CadApp.DocumentManager.Open(file, false);
        try
        {
            using (doc.LockDocument())
            {
                return TitleBlockScanner.Scan(doc, library);
            }
        }
        finally
        {
            doc.CloseAndDiscard();
        }
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
        return FileNameSanitizer.MakeUnique(_outputDirectory.Text, baseName, reservedPaths);
    }

    private string GetDefaultOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LastOutputDirectory))
        {
            return _settings.LastOutputDirectory;
        }

        var file = _currentDocument.Database.Filename;
        var directory = string.IsNullOrWhiteSpace(file)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.GetDirectoryName(file) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return Path.Combine(directory, "PDF");
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
        selected = _jobs.Where(x => x.Selected).ToList();

        _printButton.Enabled = false;
        try
        {
            var printed = 0;
            var failed = new List<string>();
            foreach (var job in selected)
            {
                try
                {
                    AppendLog("INFO", $"开始打印 {job.DrawingNumber}_{job.Title} -> {job.OutputPath}");
                    PlotterService.Plot(job, device, style, _currentDocument);
                    AppendLog("INFO", $"打印成功 {job.OutputPath}");
                    printed++;
                }
                catch (Exception ex)
                {
                    var message = $"{job.DrawingNumber}_{job.Title}: {ex.Message}";
                    failed.Add(message);
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
