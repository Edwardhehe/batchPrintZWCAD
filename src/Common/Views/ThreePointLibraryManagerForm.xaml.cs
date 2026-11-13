using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace ZwcadBatchPlot;

public partial class ThreePointLibraryManagerForm : Window
{
    private List<ThreePointDefinition> _definitions = new();

    public ThreePointLibraryManagerForm()
    {
        InitializeComponent();
        LoadRows();
    }

    private void LoadRows()
    {
        try
        {
            var library = ThreePointLibraryStore.Load();
            _definitions = library.Items
                .OrderByDescending(x => x.UpdatedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            _definitions = new List<ThreePointDefinition>();
            MessageBox.Show("读取三点图签库失败: " + ex.Message, "批量打印", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _grid.ItemsSource = _definitions
            .Select(x => new Row
            {
                Name = x.Name,
                PriorityText = x.Priority.ToString(),
                AnchorText = x.IsCollinearAnchors ? x.AnchorSummary + "（共线）" : x.AnchorSummary,
                FieldSummary = SummarizeFields(x),
                UpdatedText = x.UpdatedAt.ToString("yyyy-MM-dd HH:mm"),
                Definition = x
            })
            .ToList();

        var collinear = _definitions.Count(x => x.IsCollinearAnchors);
        _status.Text = _definitions.Count == 0
            ? "库为空。点「新建图签信息」开始：先点三个不同的文字（单行/多行均可，尽量分散，共线也可），再在弹出的录入窗里填名称、优先级并逐项点「范围」按钮框选字段。"
            : $"共 {_definitions.Count} 条三点图签。双击某行可试识别；名称不合适可点「重命名」。"
              + (collinear > 0
                  ? $"\n其中 {collinear} 条三点共线，按相似变换识别（等比缩放 + 旋转 + 平移，不支持镜像与非等比缩放）。"
                  : "");
    }

    private static string SummarizeFields(ThreePointDefinition definition)
    {
        if (definition.Fields.Count == 0)
        {
            return "—";
        }

        return string.Join("、", definition.Fields.Keys.Select(ThreePointFieldKeys.Display));
    }

    private ThreePointDefinition? SelectedDefinition()
    {
        return (_grid.SelectedItem as Row)?.Definition;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        RequestNewCapture = true;
        Close();
    }

    /// <summary>
    /// true 表示用户要新建一条图签：本窗先关掉，由命令层在“没有其它插件模态窗”的
    /// 上下文里跑录入向导，跑完再把这个管理窗重新打开刷新列表。
    /// 不能在隐藏的管理窗里再弹一个 CAD 模态窗，嵌套模态的拆装会把 CAD 弄崩。
    /// </summary>
    public bool RequestNewCapture { get; private set; }

    /// <summary>非空表示用户要重新录入该条目，同样先关窗再由命令层跑向导。</summary>
    public ThreePointDefinition? RequestRecapture { get; private set; }

    private void OnRecaptureClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDefinition();
        if (selected == null)
        {
            MessageBox.Show("请先在列表里选择要重新录入的条目。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"将重新点「{selected.Name}」的三个锚点并重框字段范围。\n名称保持不变，是否继续？",
            "批量打印", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        RequestRecapture = selected;
        Close();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDefinition();
        if (selected == null)
        {
            MessageBox.Show("请先选择要删除的条目。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show($"确定删除三点图签「{selected.Name}」？", "批量打印",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            ThreePointLibraryStore.Delete(selected.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show("删除失败: " + ex.Message, "批量打印", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadRows();
    }

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        var selected = SelectedDefinition();
        if (selected == null)
        {
            MessageBox.Show("请先选择要重命名的条目。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = TextInputDialog.Ask(this, "重命名三点图签", "名称：", selected.Name,
            "新建时名称按三个锚点文字自动生成，这里可以改成更好认的名字。");
        if (name == null || string.Equals(name, selected.Name, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var library = ThreePointLibraryStore.Load();
            if (library.Items.Any(x => !string.Equals(x.Id, selected.Id, StringComparison.OrdinalIgnoreCase)
                                       && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"已存在同名图签「{name}」，请换一个名称。", "批量打印",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var target = library.Items.FirstOrDefault(x =>
                string.Equals(x.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                LoadRows();
                return;
            }

            target.Name = name;
            target.UpdatedAt = DateTime.Now;
            ThreePointLibraryStore.Save(library);
        }
        catch (Exception ex)
        {
            MessageBox.Show("重命名失败: " + ex.Message, "批量打印", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LoadRows();
    }

    private void OnProbeClick(object sender, RoutedEventArgs e)
    {
        ProbeSelected();
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_grid.SelectedItem is Row)
        {
            ProbeSelected();
        }
    }

    private void ProbeSelected()
    {
        var selected = SelectedDefinition();
        if (selected == null)
        {
            MessageBox.Show("请先选择要试识别的条目。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var report = BatchPlotCommands.ProbeForManager(selected);
        MessageBox.Show(report, $"试识别 - {selected.Name}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(ThreePointLibraryStore.DefaultPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开配置目录失败: " + ex.Message, "批量打印", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private sealed class Row
    {
        public string Name { get; set; } = "";
        public string PriorityText { get; set; } = "";
        public string AnchorText { get; set; } = "";
        public string FieldSummary { get; set; } = "";
        public string UpdatedText { get; set; } = "";
        public ThreePointDefinition Definition { get; set; } = new();
    }
}
