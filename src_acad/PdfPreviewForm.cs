using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ZwcadBatchPlot;

public sealed class PdfPreviewForm : Form
{
    private readonly List<string> _files = new();
    private readonly Stack<List<RenameRecord>> _renameHistory = new();
    private readonly ListBox _fileList = new();
    private readonly WebView2 _viewer = new();
    private readonly Label _status = new();
    private readonly ComboBox _numberDigits = new();
    private readonly ComboBox _numberSeparator = new();
    private readonly TextBox _replaceFrom = new();
    private readonly TextBox _replaceTo = new();
    private readonly Button _previousButton = UiLayout.CreateButton("上一份", 76);
    private readonly Button _nextButton = UiLayout.CreateButton("下一份", 76);
    private readonly Button _moveUpButton = UiLayout.CreateButton("上移", 68);
    private readonly Button _moveDownButton = UiLayout.CreateButton("下移", 68);
    private bool _webViewReady;

    public PdfPreviewForm(IEnumerable<string> files)
    {
        InitializeComponents();
        AddFiles(files);
    }

    private void InitializeComponents()
    {
        Text = "PDF工具";
        UiLayout.ConfigureForm(this, 1260, 780, 940, 580);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Padding = new Padding(UiLayout.Scale(10))
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(360)));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.ButtonHeight() + UiLayout.Scale(14)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty
        };

        var addFilesButton = UiLayout.CreateButton("添加PDF", 88);
        addFilesButton.Click += (_, _) => AddPdfFiles();
        var addFolderButton = UiLayout.CreateButton("添加文件夹", 104);
        addFolderButton.Click += (_, _) => AddPdfFolder();
        var combineButton = UiLayout.CreateButton("合并PDF", 88);
        combineButton.Click += (_, _) => CombinePdfs();
        var openExternalButton = UiLayout.CreateButton("外部打开", 88);
        openExternalButton.Click += (_, _) => OpenCurrentExternal();
        var removeButton = UiLayout.CreateButton("移除", 68);
        removeButton.Click += (_, _) => RemoveCurrent();
        var clearButton = UiLayout.CreateButton("清空", 68);
        clearButton.Click += (_, _) => ClearFiles();

        _previousButton.Click += (_, _) => MoveSelection(-1);
        _nextButton.Click += (_, _) => MoveSelection(1);
        _moveUpButton.Click += (_, _) => MoveFileOrder(-1);
        _moveDownButton.Click += (_, _) => MoveFileOrder(1);

        toolbar.Controls.Add(addFilesButton);
        toolbar.Controls.Add(addFolderButton);
        toolbar.Controls.Add(combineButton);
        toolbar.Controls.Add(_previousButton);
        toolbar.Controls.Add(_nextButton);
        toolbar.Controls.Add(openExternalButton);
        toolbar.Controls.Add(removeButton);
        toolbar.Controls.Add(clearButton);

        var leftPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(185)));
        leftPanel.Controls.Add(BuildFileListPanel(), 0, 0);
        leftPanel.Controls.Add(BuildRenamePanel(), 0, 1);

        _viewer.Dock = DockStyle.Fill;
        _viewer.DefaultBackgroundColor = Color.White;

        _status.Dock = DockStyle.Bottom;
        _status.Height = Math.Max(UiLayout.Scale(28), Font.Height + UiLayout.Scale(10));
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(UiLayout.Scale(8), 0, 0, 0);
        _status.BackColor = SystemColors.Control;

        var viewerPanel = new Panel { Dock = DockStyle.Fill };
        viewerPanel.Controls.Add(_viewer);
        viewerPanel.Controls.Add(_status);

        root.Controls.Add(toolbar, 0, 0);
        root.SetColumnSpan(toolbar, 2);
        root.Controls.Add(leftPanel, 0, 1);
        root.Controls.Add(viewerPanel, 1, 1);
        Controls.Add(root);

        Shown += async (_, _) =>
        {
            await InitializeWebViewAsync();
            if (_fileList.Items.Count > 0 && _fileList.SelectedIndex < 0)
            {
                _fileList.SelectedIndex = 0;
            }
            else
            {
                RefreshStatus();
            }
        };
    }

    private Control BuildFileListPanel()
    {
        var group = new GroupBox
        {
            Text = "PDF 文件",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(8))
        };

        _fileList.Dock = DockStyle.Fill;
        _fileList.IntegralHeight = false;
        _fileList.HorizontalScrollbar = true;
        _fileList.SelectedIndexChanged += (_, _) => ShowSelectedPdf();
        _fileList.DoubleClick += (_, _) => OpenCurrentExternal();

        var sortButton = UiLayout.CreateButton("自然排序", 92);
        sortButton.Click += (_, _) => SortFilesByName();

        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty
        };
        actionRow.Controls.Add(_moveUpButton);
        actionRow.Controls.Add(_moveDownButton);
        actionRow.Controls.Add(sortButton);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.ButtonHeight() + UiLayout.Scale(8)));
        panel.Controls.Add(_fileList, 0, 0);
        panel.Controls.Add(actionRow, 0, 1);

        group.Controls.Add(panel);
        return group;
    }

    private Control BuildRenamePanel()
    {
        var group = new GroupBox
        {
            Text = "批量改名",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(8))
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 4
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(72)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(72)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _numberSeparator.DropDownStyle = ComboBoxStyle.DropDownList;
        _numberSeparator.Items.AddRange(new object[] { "_", "空格", "无", "+" });
        _numberSeparator.SelectedIndex = 0;
        _numberSeparator.Dock = DockStyle.Fill;

        _numberDigits.DropDownStyle = ComboBoxStyle.DropDownList;
        _numberDigits.Items.AddRange(new object[] { "1", "2", "3", "4" });
        _numberDigits.SelectedIndex = 2;
        _numberDigits.Dock = DockStyle.Fill;

        _replaceFrom.Dock = DockStyle.Fill;
        _replaceTo.Dock = DockStyle.Fill;

        var addNumberButton = UiLayout.CreateButton("文件名加编号", 118);
        addNumberButton.Click += (_, _) => AddNumberPrefix();
        var replaceButton = UiLayout.CreateButton("执行替换", 92);
        replaceButton.Click += (_, _) => ReplaceFileNameText();
        var undoButton = UiLayout.CreateButton("撤回改名", 92);
        undoButton.Click += (_, _) => UndoRename();

        AddCell(panel, "分隔符", 0, 0);
        panel.Controls.Add(_numberSeparator, 1, 0);
        AddCell(panel, "编号位数", 2, 0);
        panel.Controls.Add(_numberDigits, 3, 0);
        AddCell(panel, "替换前", 0, 1);
        panel.Controls.Add(_replaceFrom, 1, 1);
        AddCell(panel, "替换后", 2, 1);
        panel.Controls.Add(_replaceTo, 3, 1);
        panel.Controls.Add(addNumberButton, 0, 2);
        panel.SetColumnSpan(addNumberButton, 2);
        panel.Controls.Add(replaceButton, 2, 2);
        panel.Controls.Add(undoButton, 3, 2);
        panel.Controls.Add(new Label
        {
            Text = "改名按左侧列表顺序执行。撤回只恢复上一次批量改名。",
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 3);
        panel.SetColumnSpan(panel.Controls[panel.Controls.Count - 1], 4);

        group.Controls.Add(panel);
        return group;

        static void AddCell(TableLayoutPanel table, string text, int column, int row)
        {
            table.Controls.Add(new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            }, column, row);
        }
    }

    private async System.Threading.Tasks.Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "WebView2");
            Directory.CreateDirectory(userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _viewer.EnsureCoreWebView2Async(environment);
            _webViewReady = true;
        }
        catch (Exception ex)
        {
            _webViewReady = false;
            _status.Text = "内嵌预览不可用，可使用“外部打开”。" + ex.Message;
        }
    }

    private void AddPdfFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            Multiselect = true,
            Title = "选择 PDF 文件"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void AddPdfFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择包含 PDF 的文件夹"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddFiles(Directory.EnumerateFiles(dialog.SelectedPath, "*.pdf", SearchOption.TopDirectoryOnly));
        }
    }

    private void AddFiles(IEnumerable<string> files)
    {
        var selected = CurrentFile;
        foreach (var file in files.Where(IsPdfFile))
        {
            var fullPath = Path.GetFullPath(file);
            if (_files.Any(x => string.Equals(x, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _files.Add(fullPath);
        }

        _files.Sort(NaturalStringComparer.Instance);
        ReloadList(selected);
    }

    private void ReloadList(string? selected)
    {
        _fileList.BeginUpdate();
        _fileList.Items.Clear();
        foreach (var file in _files)
        {
            _fileList.Items.Add(new PdfListItem(file));
        }
        _fileList.EndUpdate();

        if (_fileList.Items.Count == 0)
        {
            RefreshStatus();
            return;
        }

        var selectedIndex = _files.FindIndex(x => string.Equals(x, selected, StringComparison.OrdinalIgnoreCase));
        _fileList.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
    }

    private void ShowSelectedPdf()
    {
        var file = CurrentFile;
        if (string.IsNullOrWhiteSpace(file))
        {
            RefreshStatus();
            return;
        }

        if (_webViewReady)
        {
            try
            {
                _viewer.Source = new Uri(file);
            }
            catch (Exception ex)
            {
                _status.Text = "预览失败: " + ex.Message;
            }
        }

        RefreshStatus();
    }

    private void MoveSelection(int delta)
    {
        if (_fileList.Items.Count == 0)
        {
            return;
        }

        var index = _fileList.SelectedIndex < 0 ? 0 : _fileList.SelectedIndex + delta;
        index = Math.Max(0, Math.Min(_fileList.Items.Count - 1, index));
        _fileList.SelectedIndex = index;
    }

    private void MoveFileOrder(int delta)
    {
        var index = _fileList.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _files.Count)
        {
            return;
        }

        (_files[index], _files[target]) = (_files[target], _files[index]);
        ReloadList(_files[target]);
    }

    private void SortFilesByName()
    {
        var selected = CurrentFile;
        _files.Sort(NaturalStringComparer.Instance);
        ReloadList(selected);
    }

    private void CombinePdfs()
    {
        if (_files.Count == 0)
        {
            MessageBox.Show("请先添加需要合并的 PDF。", "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "PDF 文件 (*.pdf)|*.pdf",
            FileName = "合并文件.pdf",
            Title = "保存合并 PDF"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            using var output = new PdfDocument();
            output.PageLayout = PdfPageLayout.SinglePage;

            foreach (var file in _files)
            {
                using var input = PdfReader.Open(file, PdfDocumentOpenMode.Import);
                PdfPage? firstPage = null;
                foreach (var page in input.Pages)
                {
                    var newPage = output.AddPage(page);
                    firstPage ??= newPage;
                }

                if (firstPage != null)
                {
                    output.Outlines.Add(Path.GetFileNameWithoutExtension(file), firstPage, true, PdfOutlineStyle.Bold);
                }
            }

            output.Save(dialog.FileName);
            AddFiles(new[] { dialog.FileName });
            MessageBox.Show("PDF 合并完成。", "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("合并 PDF 失败: " + ex.Message, "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddNumberPrefix()
    {
        if (_files.Count == 0)
        {
            return;
        }

        if (!EnsureFilesAvailable())
        {
            return;
        }

        var separator = _numberSeparator.SelectedItem?.ToString() switch
        {
            "空格" => " ",
            "无" => "",
            "+" => "+",
            _ => "_"
        };
        var digits = int.TryParse(_numberDigits.SelectedItem?.ToString(), out var parsed) ? parsed : 3;
        var format = new string('0', Math.Max(1, digits));

        RenameAll((file, index) =>
        {
            var name = Path.GetFileNameWithoutExtension(file);
            return $"{(index + 1).ToString(format)}{separator}{name}.pdf";
        });
    }

    private void ReplaceFileNameText()
    {
        if (_files.Count == 0)
        {
            return;
        }

        var from = _replaceFrom.Text;
        if (string.IsNullOrEmpty(from))
        {
            MessageBox.Show("请输入要替换的字符。", "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!EnsureFilesAvailable())
        {
            return;
        }

        var to = _replaceTo.Text ?? "";
        RenameAll((file, _) =>
        {
            var name = Path.GetFileNameWithoutExtension(file);
            return name.Replace(from, to) + ".pdf";
        });
    }

    private void RenameAll(Func<string, int, string> getNewFileName)
    {
        var records = new List<RenameRecord>();
        var newPaths = new List<string>();

        try
        {
            for (var i = 0; i < _files.Count; i++)
            {
                var oldPath = _files[i];
                var targetName = FileNameSanitizer.Clean(Path.GetFileNameWithoutExtension(getNewFileName(oldPath, i))) + ".pdf";
                var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? "", targetName);
                newPaths.Add(newPath);
            }

            var duplicate = newPaths
                .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(x => x.Count() > 1);
            if (duplicate != null)
            {
                MessageBox.Show("改名后存在重复文件名: " + Path.GetFileName(duplicate.Key), "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            for (var i = 0; i < _files.Count; i++)
            {
                var oldPath = _files[i];
                var newPath = newPaths[i];
                if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
                {
                    MessageBox.Show("目标文件已存在: " + newPath, "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            for (var i = 0; i < _files.Count; i++)
            {
                var oldPath = _files[i];
                var newPath = newPaths[i];
                if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(oldPath, newPath);
                }

                records.Add(new RenameRecord(oldPath, newPath));
            }

            _renameHistory.Push(records);
            _files.Clear();
            _files.AddRange(newPaths);
            ReloadList(newPaths.FirstOrDefault());
        }
        catch (Exception ex)
        {
            MessageBox.Show("改名失败: " + ex.Message, "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UndoRename()
    {
        if (_renameHistory.Count == 0)
        {
            MessageBox.Show("没有可撤回的改名操作。", "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!EnsureFilesAvailable())
        {
            return;
        }

        var records = _renameHistory.Pop();
        try
        {
            foreach (var record in records.AsEnumerable().Reverse())
            {
                if (!string.Equals(record.OldPath, record.NewPath, StringComparison.OrdinalIgnoreCase) && File.Exists(record.NewPath))
                {
                    if (File.Exists(record.OldPath))
                    {
                        throw new IOException("原文件名已被占用: " + record.OldPath);
                    }

                    File.Move(record.NewPath, record.OldPath);
                }
            }

            _files.Clear();
            _files.AddRange(records.Select(x => x.OldPath));
            ReloadList(_files.FirstOrDefault());
        }
        catch (Exception ex)
        {
            MessageBox.Show("撤回失败: " + ex.Message, "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool EnsureFilesAvailable()
    {
        foreach (var file in _files)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch
            {
                MessageBox.Show("文件被占用，请关闭后再操作: " + file, "PDF跨文件阅读", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        return true;
    }

    private void OpenCurrentExternal()
    {
        var file = CurrentFile;
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
    }

    private void RemoveCurrent()
    {
        var file = CurrentFile;
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        var index = _fileList.SelectedIndex;
        _files.RemoveAll(x => string.Equals(x, file, StringComparison.OrdinalIgnoreCase));
        ReloadList(index < _files.Count ? _files[index] : _files.LastOrDefault());
    }

    private void ClearFiles()
    {
        _files.Clear();
        _fileList.Items.Clear();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var index = _fileList.SelectedIndex;
        var current = index >= 0 ? $"{index + 1}/{_files.Count}" : $"0/{_files.Count}";
        _status.Text = _files.Count == 0
            ? "没有可预览的 PDF。可点击“添加PDF”或“添加文件夹”。"
            : $"{current}  共 {_files.Count} 份  {CurrentFile}";
        _previousButton.Enabled = _fileList.SelectedIndex > 0;
        _nextButton.Enabled = _fileList.SelectedIndex >= 0 && _fileList.SelectedIndex < _fileList.Items.Count - 1;
        _moveUpButton.Enabled = _fileList.SelectedIndex > 0;
        _moveDownButton.Enabled = _fileList.SelectedIndex >= 0 && _fileList.SelectedIndex < _fileList.Items.Count - 1;
    }

    private string? CurrentFile => (_fileList.SelectedItem as PdfListItem)?.Path;

    private static bool IsPdfFile(string file)
    {
        return File.Exists(file) && string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PdfListItem
    {
        public PdfListItem(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public override string ToString()
        {
            return System.IO.Path.GetFileNameWithoutExtension(Path);
        }
    }

    private sealed class RenameRecord
    {
        public RenameRecord(string oldPath, string newPath)
        {
            OldPath = oldPath;
            NewPath = newPath;
        }

        public string OldPath { get; }

        public string NewPath { get; }
    }
}
