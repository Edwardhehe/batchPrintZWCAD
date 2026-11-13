using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

/// <summary>
/// 签名库管理窗口（菜单「其他设置 → 签名库管理」）。
/// <para>
/// 与 <see cref="StampLibraryForm"/> 完全同构：左侧表格（序号 / 预览 / 签名名 / 删除 /
/// 不透明度 / 有效期起 / 有效期止 / 状态），右侧竖排按钮，底部显示库文件路径。
/// 两者共用同一份数据（Signatures.json）与图片目录，只是按 Kind 过滤、按各自的业务字段呈现；
/// 界面保持一致，用户在两处之间切换时不用重新学一遍。
/// </para>
/// <para>
/// 所有改动即时落盘：表格只读，新增/编辑一律走 <see cref="SignatureEditDialog"/>，
/// 不会出现「改了没保存就关窗」的半吊子状态。
/// </para>
/// </summary>
public sealed partial class SignatureLibraryManagerForm : System.Windows.Window
{
    private const string SignatureKind = "Signature";

    private SignatureLibrary _library = new();

    /// <summary>库里真实的签名行（不含用来铺满空白网格的占位行）。</summary>
    private readonly List<SignatureRow> _rows = new();

    /// <summary>表格实际绑定的行集合 = <see cref="_rows"/> + 占位行。</summary>
    private readonly List<SignatureRow> _displayRows = new();

    private int _placeholderCount;
    private bool _rebuilding;

    // 右键菜单项用代码创建（见 BuildRowMenu）：XAML 里写在 DataGrid.ContextMenu 中的 x:Name
    // 拿不到实例 —— ContextMenu 有自己的 NameScope，生成的字段在运行时全是 null。
    private readonly MenuItem _renameMenuItem = new() { Header = "重命名" };
    private readonly MenuItem _replaceImageMenuItem = new() { Header = "替换图片…" };
    private readonly MenuItem _processImageMenuItem = new() { Header = "图片处理…" };
    private readonly MenuItem _selectAllMenuItem = new() { Header = "全选" };
    private readonly MenuItem _deleteMenuItem = new() { Header = "删除" };

    /// <summary>以模态方式打开签名库管理窗口。</summary>
    public static void ShowManager() => CadDialog.ShowModal(new SignatureLibraryManagerForm());

    public SignatureLibraryManagerForm()
    {
        InitializeComponent();

        _addButton.Click += (_, _) => AddSignature();
        _exportButton.Click += (_, _) => ExportLibrary();
        _importButton.Click += (_, _) => ImportLibrary();
        _scanFolderButton.Click += (_, _) => ScanFolder();
        _openFolderButton.Click += (_, _) => OpenImagesFolder();
        _okButton.Click += (_, _) => Close();
        _cancelButton.Click += (_, _) => Close();

        BuildRowMenu();

        // 窗口拉伸后能塞下的行数变了，占位行要跟着重算，否则底部会留一条灰边。
        _grid.SizeChanged += (_, _) => RebuildRows();

        Reload();
    }

    /// <summary>表格右键菜单：重命名、替换图片、图片处理（单行）；全选、删除（支持多行）。</summary>
    private void BuildRowMenu()
    {
        _renameMenuItem.Click += (_, _) => RenameSelected();
        _replaceImageMenuItem.Click += (_, _) => ReplaceSelectedImage();
        _processImageMenuItem.Click += (_, _) => ProcessSelectedImage();
        _selectAllMenuItem.Click += (_, _) => SelectAllRows();
        _deleteMenuItem.Click += (_, _) => DeleteRows(SelectedRealRows());

        // 不加分隔线：ContextMenu 是独立的弹出树，窗口级主题字典里的画刷它查不到，
        // 分隔线要么透明要么用固定灰色（深色主题下刺眼），干脆五项平铺。
        var menu = new ContextMenu();
        menu.Items.Add(_renameMenuItem);
        menu.Items.Add(_replaceImageMenuItem);
        menu.Items.Add(_processImageMenuItem);
        menu.Items.Add(_selectAllMenuItem);
        menu.Items.Add(_deleteMenuItem);
        _grid.ContextMenu = menu;
    }

    private void Reload(string? selectId = null)
    {
        SignatureLibraryStore.EnsureStorage();
        _library = SignatureLibraryStore.Load();

        // 旧版 %APPDATA%\...\Signatures 里的图片搬进「签名」「签章」文件夹（复制不移动）。
        SignatureLibraryStore.MigrateLegacyImages(_library);

        var previousId = selectId ?? (_grid.SelectedItem as SignatureRow)?.Item.Id;

        _rows.Clear();
        var index = 0;
        foreach (var item in _library.Items.Where(
                     x => SignatureLibraryStore.NormalizeKind(x.Kind) == SignatureKind))
        {
            index++;
            _rows.Add(SignatureRow.Create(index, item));
        }

        RebuildRows(force: true);

        if (!string.IsNullOrEmpty(previousId))
        {
            SelectById(previousId!);
        }

        // 底部只留一行短路径：完整路径（含图片目录）放 ToolTip，撑不下时由 TextTrimming 收尾。
        _libraryPathText.Text = "当前签名库文件： " + SignatureLibraryStore.DefaultPath;
        _libraryPathText.ToolTip = "库文件：" + SignatureLibraryStore.DefaultPath
            + "\n图片目录：" + SignatureLibraryStore.SignatureDirectory;
        LogImageDiagnostics();
    }

    /// <summary>
    /// 记录图片目录与每个条目的定位结果。图片「找不到」时，日志里能直接看出
    /// 图片根目录是插件目录还是 %APPDATA%、库内记的文件名是什么、最终定位到哪。
    /// </summary>
    private void LogImageDiagnostics()
    {
        PluginLog.Run(
            $"[签名库] 图片根目录={SignatureLibraryStore.ImagesRoot}"
            + $" 签名目录={SignatureLibraryStore.SignatureDirectory}");

        foreach (var item in _library.Items.Where(
                     x => SignatureLibraryStore.NormalizeKind(x.Kind) == SignatureKind))
        {
            var path = SignatureLibraryStore.LocateImageFile(item);
            PluginLog.Run(
                $"[签名库] {item.Name}（{item.Id}）图片={item.ImageFile}"
                + $" 定位={(string.IsNullOrEmpty(path) ? "未找到" : path)}");
        }
    }

    /// <summary>
    /// 重建表格行：真实行 + 若干占位行，把剩余空白也画成格子。
    /// 占位行不可点（见 <see cref="SigRowStyle"/>），双击会穿透到表格 → 录入新签名。
    /// </summary>
    private void RebuildRows(bool force = false)
    {
        if (_rebuilding)
        {
            return;
        }

        var capacity = ComputeRowCapacity();
        var need = Math.Max(0, capacity - _rows.Count);
        if (!force && need == _placeholderCount && _displayRows.Count == _rows.Count + _placeholderCount)
        {
            return;
        }

        var selectedItem = (_grid.SelectedItem as SignatureRow)?.Item;

        _rebuilding = true;
        try
        {
            _placeholderCount = need;
            _displayRows.Clear();
            _displayRows.AddRange(_rows);
            for (var i = 0; i < need; i++)
            {
                _displayRows.Add(SignatureRow.Placeholder());
            }

            // 换 ItemsSource 而不是 Items.Refresh：行对象是重建的，预览图与状态文字都要按最新图片重算。
            _grid.ItemsSource = null;
            _grid.ItemsSource = _displayRows;

            if (selectedItem != null)
            {
                _grid.SelectedItem = _displayRows.FirstOrDefault(
                    row => !row.IsPlaceholder && ReferenceEquals(row.Item, selectedItem));
            }

            _emptyHint.Visibility = need > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>表格当前能完整显示多少行（留一行余量，避免刚好多出半行引出竖向滚动条）。</summary>
    private int ComputeRowCapacity()
    {
        var height = _grid.ActualHeight;
        if (double.IsNaN(height) || height <= 0)
        {
            // 首次布局前还没测量，先按窗口默认高度给个值；SizeChanged 会再纠正。
            return 12;
        }

        var rowHeight = _grid.RowHeight > 0 ? _grid.RowHeight : 22d;
        var usable = height - _grid.ColumnHeaderHeight - 2;
        var capacity = (int)Math.Floor(usable / rowHeight) - 1;
        return Math.Max(1, capacity);
    }

    /// <summary>双击：命中真实行则编辑，命中空白（含占位行穿透上来的点击）则录入新签名。</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject);
        if (row == null || row.IsPlaceholder)
        {
            AddSignature();
            return;
        }

        EditSignature(row);
    }

    // ───────────────────────────── 右键菜单 ─────────────────────────────

    /// <summary>
    /// 右键按下时先把目标行变成"当前选择"。
    /// <para>
    /// 不做这一步的话，先选中第 1 行、再右键第 2 行点「删除」，删掉的会是第 1 行 —— 这类"删错行"
    /// 在只读表格里没有任何撤销手段。若右键的行已经在多选集合里，则保留集合不动，
    /// 这样「先 Ctrl 点选几行、再在任意一行上右键」能直接对整批执行删除。
    /// </para>
    /// </summary>
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject);
        if (row == null || row.IsPlaceholder)
        {
            // 表头 / 空白占位区：清掉选择，菜单在 Opening 里会被关掉。
            _grid.SelectedItems.Clear();
            return;
        }

        if (!_grid.SelectedItems.Contains(row))
        {
            _grid.SelectedItems.Clear();
            _grid.SelectedItems.Add(row);
        }
    }

    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var selected = SelectedRealRows();
        if (selected.Count == 0)
        {
            // 没有真实签名行被选中（右键点在空白网格上）→ 不弹菜单，避免"删了个寂寞"。
            e.Handled = true;
            return;
        }

        UpdateRowMenuState(selected);
    }

    /// <summary>按当前选中的行数更新菜单项：重命名/替换/图片处理是单行操作，删除按数量显示。</summary>
    private void UpdateRowMenuState(IReadOnlyList<SignatureRow> selected)
    {
        var single = selected.Count == 1;
        _renameMenuItem.IsEnabled = single;
        _replaceImageMenuItem.IsEnabled = single;

        // 图片都没定位到就不可能处理它，禁用并在提示里说明原因。
        var hasImage = single && !string.IsNullOrEmpty(SignatureLibraryStore.LocateImageFile(selected[0].Item));
        _processImageMenuItem.IsEnabled = hasImage;
        _processImageMenuItem.Header = single && !hasImage ? "图片处理…（图片缺失）" : "图片处理…";

        _deleteMenuItem.Header = single ? "删除" : $"删除（{selected.Count} 个）";
        _selectAllMenuItem.IsEnabled = _rows.Count > 1;
    }

    /// <summary>当前选中的真实签名行（排除用来铺满空白的占位行）。</summary>
    private List<SignatureRow> SelectedRealRows()
    {
        return _grid.SelectedItems
            .OfType<SignatureRow>()
            .Where(row => !row.IsPlaceholder)
            .ToList();
    }

    private void RenameSelected()
    {
        var rows = SelectedRealRows();
        if (rows.Count != 1)
        {
            return;
        }

        var item = rows[0].Item;
        var name = TextInputDialog.Prompt(
            this,
            "重命名签名",
            "签名名：",
            item.Name,
            value => HasSameName(item, value) ? "已有同名签名，请换一个名字。" : null);

        if (name == null)
        {
            return;
        }

        try
        {
            item.Name = name;
            item.UpdatedAt = DateTime.Now;
            SignatureLibraryStore.Save(_library);
            Reload(item.Id);
            PluginLog.Run($"已重命名签名：{item.Id} → {name}");
        }
        catch (Exception ex)
        {
            ReportError("重命名失败", ex);
        }
    }

    /// <summary>同一类（这里就是签名）里是否已有别的条目用了这个名字。重名会让签章槽位的下拉框分不清。</summary>
    private bool HasSameName(SignatureItem self, string name)
    {
        var kind = SignatureLibraryStore.NormalizeKind(self.Kind);
        return _library.Items.Any(x =>
            x != null
            && x.Id != self.Id
            && SignatureLibraryStore.NormalizeKind(x.Kind) == kind
            && string.Equals((x.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    private void ReplaceSelectedImage()
    {
        var rows = SelectedRealRows();
        if (rows.Count != 1)
        {
            return;
        }

        var item = rows[0].Item;
        var sourcePath = PickImageFile();
        if (sourcePath == null)
        {
            return;
        }

        try
        {
            SignatureLibraryStore.ReplaceImage(
                item,
                sourcePath,
                removeBackground: _removeBackgroundCheck.IsChecked == true);
            SignatureLibraryStore.Save(_library);
            Reload(item.Id);
            PluginLog.Run($"已替换签名图片：{item.Name} ← {sourcePath}");
        }
        catch (Exception ex)
        {
            ReportError("替换图片失败", ex);
        }
    }

    /// <summary>
    /// 图片处理：去底色 / 修剪空白 / 调整尺寸 / 清晰度。
    /// <para>
    /// 基准图取「原图备份」而不是当前图：本方法可以被反复调用，每次都从原图重算，
    /// 否则第二次去底会把笔画边缘再削一轮，几次下来签名就糊了。
    /// </para>
    /// </summary>
    private void ProcessSelectedImage()
    {
        var rows = SelectedRealRows();
        if (rows.Count != 1)
        {
            return;
        }

        var item = rows[0].Item;
        var basePath = SignatureLibraryStore.GetProcessingBasePath(item);
        if (string.IsNullOrWhiteSpace(basePath) || !File.Exists(basePath))
        {
            MessageBox.Show(this, "这个条目的图片文件缺失，请先「替换图片…」。", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = ImageProcessDialog.Open(
            this,
            basePath,
            item.ImageProcess,
            SignatureLibraryStore.HasOriginalBackup(item),
            item.Name);
        if (dialog == null || dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            if (dialog.RestoreRequested)
            {
                if (!SignatureLibraryStore.RestoreOriginalImage(item))
                {
                    MessageBox.Show(this, "没有找到原图备份，无法恢复。", Title,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                SignatureLibraryStore.Save(_library);
                Reload(item.Id);
                PluginLog.Run($"签名：已恢复原图 — {item.Name}");
                return;
            }

            if (dialog.Settings == null)
            {
                return;
            }

            var size = SignatureLibraryStore.ApplyImageProcessing(item, dialog.Settings);
            SignatureLibraryStore.Save(_library);
            Reload(item.Id);
            PluginLog.Run($"签名：图片处理 {item.Name} — {dialog.Settings.Describe()} → {size.Width}×{size.Height}px");
        }
        catch (Exception ex)
        {
            ReportError("图片处理失败", ex);
        }
    }

    private void SelectAllRows()
    {
        if (_rows.Count == 0)
        {
            return;
        }

        _grid.SelectedItems.Clear();
        foreach (var row in _rows)
        {
            _grid.SelectedItems.Add(row);
        }

        _grid.ScrollIntoView(_rows[_rows.Count - 1]);
    }

    /// <summary>删除（可多行）：确认文案合并成一次，避免选了 10 个要点 10 次。</summary>
    private void DeleteRows(IReadOnlyList<SignatureRow> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return;
        }

        var references = rows.Sum(row => SignatureLibraryStore.CountSlotReferences(row.Item.Id));
        var names = string.Join("、", rows.Take(4).Select(row => row.Item.Name));
        if (rows.Count > 4)
        {
            names += $" 等 {rows.Count} 个";
        }

        var message = references > 0
            ? $"确定删除 {names} 吗？\n\n其中 {references} 处图框签章槽位正在引用它们，删除后这些槽位将不再盖签名。"
            : $"确定删除 {names} 吗？";

        if (MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            foreach (var row in rows)
            {
                SignatureLibraryStore.DeleteItem(_library, row.Item);
            }

            SignatureLibraryStore.Save(_library);
            Reload();
            PluginLog.Run($"已删除签名 {rows.Count} 个：{names}");
        }
        catch (Exception ex)
        {
            ReportError("删除签名失败", ex);
        }
    }

    /// <summary>按 Id 重新选中某行：改名/换图后表格是重建的，行对象换了新的，得重新定位。</summary>
    private void SelectById(string id)
    {
        var target = _displayRows.FirstOrDefault(row => !row.IsPlaceholder && row.Item.Id == id);
        if (target == null)
        {
            return;
        }

        _grid.SelectedItems.Clear();
        _grid.SelectedItems.Add(target);
        _grid.ScrollIntoView(target);
    }

    private static SignatureRow? FindRow(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is DataGridRow dataGridRow)
            {
                return dataGridRow.Item as SignatureRow;
            }

            // 已经走到表格本身还没碰到行 → 点的是空白区，别再往窗口外找。
            if (current is DataGrid)
            {
                return null;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    // ───────────────────────────── 新增 / 编辑 ─────────────────────────────

    private void AddSignature()
    {
        var sourcePath = PickImageFile();
        if (sourcePath == null)
        {
            return;
        }

        var dialog = SignatureEditDialog.ForNew(sourcePath);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // 在录入框里做过「图片处理」的图已经去过底了，再跑一遍只会削弱笔画边缘。
            var item = SignatureLibraryStore.ImportImage(
                dialog.PickedSourcePath ?? sourcePath,
                SignatureKind,
                dialog.SignatureName,
                removeBackground: !dialog.PickedIsProcessed && _removeBackgroundCheck.IsChecked == true);

            ApplyEditedFields(item, dialog);

            _library.Items.Add(item);
            SignatureLibraryStore.Save(_library);
            Reload(item.Id);
            PluginLog.Run($"已录入签名：{item.Name}（{item.Id}）");
        }
        catch (Exception ex)
        {
            ReportError("录入签名失败", ex);
        }
    }

    private void EditSignature(SignatureRow row)
    {
        if (row == null || row.IsPlaceholder)
        {
            return;
        }

        var dialog = SignatureEditDialog.ForExisting(row.Item);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(dialog.PickedSourcePath))
            {
                SignatureLibraryStore.ReplaceImage(
                    row.Item,
                    dialog.PickedSourcePath!,
                    removeBackground: !dialog.PickedIsProcessed && _removeBackgroundCheck.IsChecked == true);
            }

            ApplyEditedFields(row.Item, dialog);
            SignatureLibraryStore.Save(_library);
            Reload(row.Item.Id);
        }
        catch (Exception ex)
        {
            ReportError("保存签名失败", ex);
        }
    }

    private static void ApplyEditedFields(SignatureItem item, SignatureEditDialog dialog)
    {
        item.Name = dialog.SignatureName;
        item.Opacity = dialog.Opacity;
        item.ValidFrom = dialog.ValidFrom;
        item.ValidTo = dialog.ValidTo;
        item.UpdatedAt = DateTime.Now;
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SignatureRow row || row.IsPlaceholder)
        {
            return;
        }

        DeleteRows(new[] { row });
    }

    // ───────────────────────────── 导出 / 导入 ─────────────────────────────

    /// <summary>
    /// 导出签名库：单文件（JSON + 图片 base64）。
    /// 不打包 zip 是为了零依赖、单文件、记事本能打开核对；签名图都很小，体积代价可以忽略。
    /// </summary>
    private void ExportLibrary()
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show(this, "签名库是空的，没有可导出的内容。", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "签名库 (*.gmsig)|*.gmsig|所有文件 (*.*)|*.*",
            FileName = "签名库_" + DateTime.Now.ToString("yyyyMMdd") + ".gmsig",
            Title = "导出签名库",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var package = new SignaturePackage { ExportedAt = DateTime.Now };
            foreach (var row in _rows)
            {
                package.Items.Add(row.Item);

                var path = SignatureLibraryStore.LocateImageFile(row.Item);
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(row.Item.ImageFile))
                {
                    continue;
                }

                package.Images[row.Item.ImageFile] = Convert.ToBase64String(File.ReadAllBytes(path));
            }

            File.WriteAllText(
                dialog.FileName,
                JsonConvert.SerializeObject(package, Formatting.Indented),
                new UTF8Encoding(false));

            MessageBox.Show(this,
                $"已导出 {package.Items.Count} 个签名：\n{dialog.FileName}",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReportError("导出签名库失败", ex);
        }
    }

    /// <summary>
    /// 导入签名库：逐条追加（新 Id、新图片文件名），不覆盖现有条目。
    /// 这样「我的签名 + 同事的签名」可以合并到一起，而不会互相顶掉。
    /// </summary>
    private void ImportLibrary()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "签名库 (*.gmsig)|*.gmsig|所有文件 (*.*)|*.*",
            Title = "导入签名库",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SignaturePackage? package;
        try
        {
            package = JsonConvert.DeserializeObject<SignaturePackage>(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex)
        {
            ReportError("签名库文件无法解析", ex);
            return;
        }

        if (package?.Items == null || package.Items.Count == 0)
        {
            MessageBox.Show(this, "文件里没有签名条目。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var directory = SignatureLibraryStore.SignatureDirectory;
            Directory.CreateDirectory(directory);

            var added = 0;
            var skipped = 0;
            string? lastId = null;
            foreach (var source in package.Items)
            {
                // 没有图片的条目导入进来只是个看不见的空壳，直接跳过并计数。
                if (string.IsNullOrWhiteSpace(source.ImageFile)
                    || !package.Images.TryGetValue(source.ImageFile, out var base64)
                    || string.IsNullOrWhiteSpace(base64))
                {
                    skipped++;
                    continue;
                }

                var id = "sig-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                var fileName = id + ".png";
                File.WriteAllBytes(Path.Combine(directory, fileName), Convert.FromBase64String(base64));

                _library.Items.Add(new SignatureItem
                {
                    Id = id,
                    Kind = SignatureKind,
                    Name = string.IsNullOrWhiteSpace(source.Name) ? "导入签名" : source.Name,
                    ImageFile = fileName,
                    Opacity = source.Opacity > 0 && source.Opacity <= 1 ? source.Opacity : 1,
                    BackgroundRemoved = source.BackgroundRemoved,
                    SourceFileName = source.SourceFileName ?? "",
                    ValidFrom = source.ValidFrom ?? "",
                    ValidTo = source.ValidTo ?? "",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                });
                lastId = id;
                added++;
            }

            if (added > 0)
            {
                SignatureLibraryStore.Save(_library);
                Reload(lastId);
            }

            var message = $"已导入 {added} 个签名。";
            if (skipped > 0)
            {
                message += $"\n{skipped} 条因缺少图片被跳过。";
            }

            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            PluginLog.Run($"签名库导入：新增 {added} 条，跳过 {skipped} 条（{dialog.FileName}）");
        }
        catch (Exception ex)
        {
            ReportError("导入签名库失败", ex);
        }
    }

    // ───────────────────────────── 文件夹操作 ─────────────────────────────

    /// <summary>
    /// 扫描签名图片文件夹，把还没入库的图片登记进来。
    /// 用户直接往插件目录的「签名」里丢图片时用这个，不必逐个走导入对话框。
    /// </summary>
    private void ScanFolder()
    {
        try
        {
            var added = SignatureLibraryStore.RegisterImagesFromFolder(_library, SignatureKind);
            if (added > 0)
            {
                SignatureLibraryStore.Save(_library);
            }

            Reload();

            var directory = SignatureLibraryStore.SignatureDirectory;
            MessageBox.Show(this,
                added > 0
                    ? $"已从文件夹登记 {added} 张新图片：\n{directory}"
                    : $"文件夹里没有新的图片：\n{directory}\n\n支持的格式：png / jpg / bmp / gif / tif（非 PNG 会自动转成 PNG）。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "扫描文件夹失败：" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            PluginLog.Error("签章", "扫描文件夹失败：" + ex.Message, ex);
        }
    }

    private void OpenImagesFolder()
    {
        try
        {
            var directory = SignatureLibraryStore.SignatureDirectory;
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start("explorer.exe", directory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开目录失败：" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? PickImageFile()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "请选择签名图片…",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
    }

    private void ReportError(string title, Exception ex)
    {
        MessageBox.Show(this, $"{title}：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        PluginLog.Error("签章", $"{title}：{ex.Message}", ex);
    }

    /// <summary>表格一行。字段都在创建时算好：表格是只读的，不需要通知属性变更。</summary>
    private sealed class SignatureRow
    {
        private static readonly Brush UsableBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F));
        private static readonly Brush ProblemBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        private static readonly Brush PendingBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0x86, 0x0B));

        public int Index { get; private set; }

        public SignatureItem Item { get; private set; } = new();

        /// <summary>占位行：只为把空白区也画成格子，没有任何数据，也不可交互。</summary>
        public bool IsPlaceholder { get; private set; }

        public string IndexText => IsPlaceholder ? "" : Index.ToString(CultureInfo.InvariantCulture);

        public bool ShowDelete => !IsPlaceholder;

        public BitmapSource? Preview { get; private set; }

        public string Name => IsPlaceholder ? "" : string.IsNullOrWhiteSpace(Item.Name) ? "（未命名）" : Item.Name;

        public string OpacityText => IsPlaceholder
            ? ""
            : Math.Max(0, Math.Min(1, Item.Opacity)).ToString("0.##", CultureInfo.InvariantCulture);

        public string ValidFromText => IsPlaceholder ? "" : FormatDate(Item.ValidFrom, "不限");

        public string ValidToText => IsPlaceholder ? "" : FormatDate(Item.ValidTo, "长期");

        public string StatusText { get; private set; } = "";

        public Brush StatusBrush { get; private set; } = UsableBrush;

        /// <summary>状态列的鼠标悬停说明：图片缺失时给出查找过的目录，读取失败时给出解码报错。</summary>
        public string StatusDetail { get; private set; } = "";

        /// <summary>没有说明时返回 null —— 空字符串会让 WPF 弹出一个空白提示框。</summary>
        public string? StatusToolTip => string.IsNullOrEmpty(StatusDetail) ? null : StatusDetail;

        public static SignatureRow Placeholder() => new() { IsPlaceholder = true };

        /// <summary>日期显示成 2025.2.12 这种紧凑写法，比 ISO 的短横线更像图签上的日期。</summary>
        private static string FormatDate(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return DateTime.TryParse(value, out var date)
                ? date.ToString("yyyy.M.d", CultureInfo.InvariantCulture)
                : value;
        }

        public static SignatureRow Create(int index, SignatureItem item)
        {
            var path = SignatureLibraryStore.LocateImageFile(item);
            BitmapSource? preview = null;
            var detail = "";

            if (string.IsNullOrEmpty(path))
            {
                // 找不到文件：把「应该在哪个目录、叫什么名字」写进提示里，用户能自己核对。
                detail = $"未找到图片文件：{item.ImageFile}\n"
                    + $"已查找：{SignatureLibraryStore.SignatureDirectory}";
            }
            else
            {
                preview = PreviewImageLoader.Load(path, out var error);
                if (preview == null)
                {
                    detail = $"图片无法读取：{path}\n{error}";
                }
            }

            var row = new SignatureRow
            {
                Index = index,
                Item = item,
                Preview = preview,
                StatusDetail = detail,
            };

            if (preview == null)
            {
                // 区分「文件没了」和「文件在但读不出来」——两者排查方向完全不同。
                row.StatusText = string.IsNullOrEmpty(path) ? "图片缺失" : "读取失败";
                row.StatusBrush = ProblemBrush;
            }
            else if (DateTime.TryParse(item.ValidFrom, out var from) && DateTime.Now.Date < from.Date)
            {
                row.StatusText = "未生效";
                row.StatusBrush = PendingBrush;
            }
            else if (!item.IsValidOn(DateTime.Now))
            {
                row.StatusText = "已过期";
                row.StatusBrush = ProblemBrush;
            }
            else
            {
                row.StatusText = "✓ 可用";
                row.StatusBrush = UsableBrush;
            }

            return row;
        }
    }

    /// <summary>导出/导入用的单文件格式：条目 + 图片 base64（键为库内文件名）。</summary>
    private sealed class SignaturePackage
    {
        public int Version { get; set; } = 1;

        public DateTime ExportedAt { get; set; } = DateTime.Now;

        public List<SignatureItem> Items { get; set; } = new();

        public Dictionary<string, string> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
