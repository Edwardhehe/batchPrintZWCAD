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
/// 印章库管理窗口（菜单「其他设置 → 印章库管理」）。
/// <para>
/// 界面按 MSteel 的印章库复刻：左侧表格（序号 / 预览 / 印章名 / 删除 / 授权码 / 有效期 /
/// 尺寸 / 类型 / 状态），右侧竖排「录入新印章 / 导出 / 导入 / 确定 / 取消」，底部显示库文件路径。
/// 与签名库共用同一份数据（Signatures.json）与图片目录，只是按 Kind=Stamp 过滤并按印章的
/// 业务字段（类型、授权码、标注宽度）呈现。
/// </para>
/// <para>
/// 所有改动即时落盘：表格是只读的，新增/编辑一律走 <see cref="StampEditDialog"/>，
/// 避免像签名库那样「改了没保存就关窗」的半吊子状态。
/// </para>
/// </summary>
public sealed partial class StampLibraryForm : System.Windows.Window
{
    private SignatureLibrary _library = new();

    /// <summary>库里真实的印章行（不含用来铺满空白网格的占位行）。</summary>
    private readonly List<StampRow> _rows = new();

    /// <summary>表格实际绑定的行集合 = <see cref="_rows"/> + 占位行。</summary>
    private readonly List<StampRow> _displayRows = new();

    private int _placeholderCount;
    private bool _rebuilding;

    /// <summary>
    /// 导入时是否把接近纯白的底色置为透明（窗口底部的开关，默认勾选）。
    /// <para>
    /// 印章九成是白底扫描件：不去底直接盖到图纸上就是一块白方块，压住下面的线条。
    /// 但对于本身已经带透明通道的 PNG，去底只是白跑一遍（只动接近纯白的像素，不会伤到笔画），
    /// 所以默认开着最省事。
    /// </para>
    /// </summary>
    private bool AutoRemoveBackground => _removeBackgroundCheck.IsChecked == true;

    // 右键菜单项。用代码创建（见 BuildRowMenu）：XAML 里写在 DataGrid.ContextMenu 中的 x:Name
    // 拿不到实例 —— ContextMenu 有自己的 NameScope，生成的字段在运行时全是 null，
    // 菜单项就没法按"选中几行"启用/禁用。
    private readonly MenuItem _renameMenuItem = new() { Header = "重命名" };
    private readonly MenuItem _replaceImageMenuItem = new() { Header = "替换图片…" };
    private readonly MenuItem _selectAllMenuItem = new() { Header = "全选" };
    private readonly MenuItem _deleteMenuItem = new() { Header = "删除" };

    public static void ShowManager() => CadDialog.ShowModal(new StampLibraryForm());

    public StampLibraryForm()
    {
        InitializeComponent();

        _addButton.Click += (_, _) => AddStamp();
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

    /// <summary>表格右键菜单：重命名、替换图片（单行）；全选、删除（支持多行）。</summary>
    private void BuildRowMenu()
    {
        _renameMenuItem.Click += (_, _) => RenameSelected();
        _replaceImageMenuItem.Click += (_, _) => ReplaceSelectedImage();
        _selectAllMenuItem.Click += (_, _) => SelectAllRows();
        _deleteMenuItem.Click += (_, _) => DeleteRows(SelectedRealRows());

        // 不加分隔线：ContextMenu 是独立的弹出树，窗口级主题字典里的画刷它查不到，
        // 分隔线要么透明要么用固定灰色（深色主题下刺眼），干脆四项平铺。
        var menu = new ContextMenu();
        menu.Items.Add(_renameMenuItem);
        menu.Items.Add(_replaceImageMenuItem);
        menu.Items.Add(_selectAllMenuItem);
        menu.Items.Add(_deleteMenuItem);
        _grid.ContextMenu = menu;
    }

    private void Reload()
    {
        SignatureLibraryStore.EnsureStorage();
        _library = SignatureLibraryStore.Load();

        // 旧版 %APPDATA%\...\Signatures 里的图片搬进「签章」文件夹（复制不移动）。
        SignatureLibraryStore.MigrateLegacyImages(_library);

        _rows.Clear();
        var index = 0;
        foreach (var item in _library.Items.Where(
                     x => SignatureLibraryStore.NormalizeKind(x.Kind) == SignatureLibraryStore.StampKind))
        {
            index++;
            _rows.Add(StampRow.Create(index, item));
        }

        RebuildRows(force: true);
        _libraryPathText.Text = "当前印章库文件： " + SignatureLibraryStore.DefaultPath;
        LogImageDiagnostics();
    }

    /// <summary>
    /// 记录图片目录与每个条目的定位结果。印章图片「找不到」时，日志里能直接看出
    /// 图片根目录是插件目录还是 %APPDATA%、库内记的文件名是什么、最终定位到哪。
    /// </summary>
    private void LogImageDiagnostics()
    {
        PluginLog.Run(
            $"[印章库] 图片根目录={SignatureLibraryStore.ImagesRoot}"
            + $" 印章目录={SignatureLibraryStore.StampDirectory}");

        foreach (var item in _library.Items.Where(
                     x => SignatureLibraryStore.NormalizeKind(x.Kind) == SignatureLibraryStore.StampKind))
        {
            var path = SignatureLibraryStore.LocateImageFile(item);
            PluginLog.Run(
                $"[印章库] {item.Name}（{item.Id}）图片={item.ImageFile}"
                + $" 定位={(string.IsNullOrEmpty(path) ? "未找到" : path)}");
        }
    }

    /// <summary>
    /// 重建表格行：真实行 + 若干占位行，把剩余空白也画成格子
    /// （截图里空白区是一整片网格，而不是一片灰底）。
    /// 占位行不可点（见 <see cref="StampRowStyle"/>），双击会穿透到表格 → 录入新印章。
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

        var selectedItem = (_grid.SelectedItem as StampRow)?.Item;

        _rebuilding = true;
        try
        {
            _placeholderCount = need;
            _displayRows.Clear();
            _displayRows.AddRange(_rows);
            for (var i = 0; i < need; i++)
            {
                _displayRows.Add(StampRow.Placeholder());
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

    /// <summary>双击：命中真实行则编辑，命中空白（含占位行穿透上来的点击）则录入新印章。</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindRow(e.OriginalSource as DependencyObject);
        if (row == null || row.IsPlaceholder)
        {
            AddStamp();
            return;
        }

        EditStamp(row);
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
            // 没有真实印章行被选中（右键点在空白网格上）→ 不弹菜单，避免"删了个寂寞"。
            e.Handled = true;
            return;
        }

        UpdateRowMenuState(selected);
    }

    /// <summary>按当前选中的行数更新菜单项：重命名/替换图片是单行操作，删除按数量显示。</summary>
    private void UpdateRowMenuState(IReadOnlyList<StampRow> selected)
    {
        var single = selected.Count == 1;
        _renameMenuItem.IsEnabled = single;
        _replaceImageMenuItem.IsEnabled = single;
        _deleteMenuItem.Header = single ? "删除" : $"删除（{selected.Count} 枚）";
        _selectAllMenuItem.IsEnabled = _rows.Count > 1;
    }

    /// <summary>当前选中的真实印章行（排除用来铺满空白的占位行）。</summary>
    private List<StampRow> SelectedRealRows()
    {
        return _grid.SelectedItems
            .OfType<StampRow>()
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
            "重命名印章",
            "印章名：",
            item.Name,
            value => HasSameName(item, value) ? "已有同名印章，请换一个名字。" : null);

        if (name == null)
        {
            return;
        }

        try
        {
            item.Name = name;
            item.UpdatedAt = DateTime.Now;
            SignatureLibraryStore.Save(_library);
            Reload();
            SelectById(item.Id);
            PluginLog.Run($"已重命名印章：{item.Id} → {name}");
        }
        catch (Exception ex)
        {
            ReportError("重命名失败", ex);
        }
    }

    /// <summary>同一类（这里就是印章）里是否已有别的条目用了这个名字。重名会让签章槽位的下拉框分不清。</summary>
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
            // 与「录入新印章」保持一致的默认处理：印章九成是白底扫描件，去白底后盖章才不糊成方块。
            // 是否去底由底部开关控制（见 AutoRemoveBackground）。
            SignatureLibraryStore.ReplaceImage(item, sourcePath, removeBackground: AutoRemoveBackground);
            SignatureLibraryStore.Save(_library);
            Reload();
            SelectById(item.Id);
            PluginLog.Run($"已替换印章图片：{item.Name} ← {sourcePath}");
        }
        catch (Exception ex)
        {
            ReportError("替换图片失败", ex);
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

    /// <summary>删除（可多行）：确认文案合并成一次，避免选了 10 枚要点 10 次。</summary>
    private void DeleteRows(IReadOnlyList<StampRow> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return;
        }

        var references = rows.Sum(row => SignatureLibraryStore.CountSlotReferences(row.Item.Id));
        var names = string.Join("、", rows.Take(4).Select(row => row.Item.Name));
        if (rows.Count > 4)
        {
            names += $" 等 {rows.Count} 枚";
        }

        var message = references > 0
            ? $"确定删除 {names} 吗？\n\n其中 {references} 处图框签章槽位正在引用它们，删除后这些槽位将不再盖章。"
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
            PluginLog.Run($"已删除印章 {rows.Count} 枚：{names}");
        }
        catch (Exception ex)
        {
            ReportError("删除印章失败", ex);
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

    private static StampRow? FindRow(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is DataGridRow dataGridRow)
            {
                return dataGridRow.Item as StampRow;
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

    private void AddStamp()
    {
        var sourcePath = PickImageFile();
        if (sourcePath == null)
        {
            return;
        }

        var dialog = StampEditDialog.ForNew(sourcePath);
        dialog.Owner = this;
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // 去底由底部开关决定；但用户在录入框里点过「图片处理」时图已经是处理过的，
            // 再跑一遍只会削弱笔画边缘，所以那种情况一律跳过。
            var item = SignatureLibraryStore.ImportImage(
                dialog.PickedSourcePath ?? sourcePath,
                SignatureLibraryStore.StampKind,
                dialog.StampName,
                removeBackground: AutoRemoveBackground && !dialog.PickedIsProcessed);

            ApplyEditedFields(item, dialog);

            _library.Items.Add(item);
            SignatureLibraryStore.Save(_library);
            Reload();
            PluginLog.Run($"已录入印章：{item.Name}（{item.Id}）");
        }
        catch (System.Exception ex)
        {
            ReportError("录入印章失败", ex);
        }
    }

    private void EditStamp(StampRow row)
    {
        if (row == null || row.IsPlaceholder)
        {
            return;
        }

        var dialog = StampEditDialog.ForExisting(row.Item);
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
                    removeBackground: AutoRemoveBackground && !dialog.PickedIsProcessed);
            }

            ApplyEditedFields(row.Item, dialog);
            SignatureLibraryStore.Save(_library);
            Reload();
        }
        catch (System.Exception ex)
        {
            ReportError("保存印章失败", ex);
        }
    }

    private static void ApplyEditedFields(SignatureItem item, StampEditDialog dialog)
    {
        item.Name = dialog.StampName;
        item.AuthCode = dialog.AuthCode;
        item.StampType = dialog.StampType;
        item.ValidTo = dialog.ValidTo;
        item.UpdatedAt = DateTime.Now;

        // 图片处理参数（含标注尺寸）是真值，整体写回；再把尺寸镜像到旧字段，
        // 保证仍在读 WidthMm/HeightMm 的老版本插件看到的是同一个数。
        if (dialog.ProcessSettings != null)
        {
            item.ImageProcess = dialog.ProcessSettings.Clone();
        }

        item.SetEffectiveSizeMm(dialog.WidthMm, dialog.HeightMm);
    }

    private void RowDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StampRow row || row.IsPlaceholder)
        {
            return;
        }

        var references = SignatureLibraryStore.CountSlotReferences(row.Item.Id);
        var message = references > 0
            ? $"「{row.Item.Name}」正被 {references} 个图框签章槽位引用，删除后这些槽位将不再盖章。\n\n确定删除吗？"
            : $"确定删除印章「{row.Item.Name}」吗？";

        if (MessageBox.Show(this, message, Title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SignatureLibraryStore.DeleteItem(_library, row.Item);
            SignatureLibraryStore.Save(_library);
            Reload();
        }
        catch (System.Exception ex)
        {
            ReportError("删除印章失败", ex);
        }
    }

    /// <summary>
    /// 导出印章库：单文件（JSON + 图片 base64）。
    /// 不打包 zip 是为了零依赖、单文件、记事本能打开核对；印章图都很小，体积代价可以忽略。
    /// </summary>
    private void ExportLibrary()
    {
        if (_rows.Count == 0)
        {
            MessageBox.Show(this, "印章库是空的，没有可导出的内容。", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "印章库 (*.gmslib)|*.gmslib|所有文件 (*.*)|*.*",
            FileName = "印章库_" + DateTime.Now.ToString("yyyyMMdd") + ".gmslib",
            Title = "导出印章库",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var package = new StampPackage { ExportedAt = DateTime.Now };
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
                $"已导出 {package.Items.Count} 枚印章：\n{dialog.FileName}",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            ReportError("导出印章库失败", ex);
        }
    }

    /// <summary>
    /// 导入印章库：逐条追加（新 Id、新图片文件名），不覆盖现有条目。
    /// 这样「我的章 + 同事的章」可以合并到一起，而不会互相顶掉。
    /// </summary>
    private void ImportLibrary()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "印章库 (*.gmslib)|*.gmslib|所有文件 (*.*)|*.*",
            Title = "导入印章库",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StampPackage? package;
        try
        {
            package = JsonConvert.DeserializeObject<StampPackage>(File.ReadAllText(dialog.FileName));
        }
        catch (System.Exception ex)
        {
            ReportError("印章库文件无法解析", ex);
            return;
        }

        if (package?.Items == null || package.Items.Count == 0)
        {
            MessageBox.Show(this, "文件里没有印章条目。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var directory = SignatureLibraryStore.StampDirectory;
            Directory.CreateDirectory(directory);

            var added = 0;
            var skipped = 0;
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
                    Kind = SignatureLibraryStore.StampKind,
                    Name = string.IsNullOrWhiteSpace(source.Name) ? "导入印章" : source.Name,
                    ImageFile = fileName,
                    BackgroundRemoved = source.BackgroundRemoved,
                    SourceFileName = source.SourceFileName ?? "",
                    StampType = source.StampType ?? "",
                    AuthCode = source.AuthCode,
                    // 处理参数要一起导入：里面有「打多大」的标注尺寸，丢了就变成跟随槽位。
                    ImageProcess = source.ImageProcess?.Clone(),
                    WidthMm = source.WidthMm,
                    HeightMm = source.HeightMm,
                    ValidFrom = source.ValidFrom ?? "",
                    ValidTo = source.ValidTo ?? "",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                });
                added++;
            }

            if (added > 0)
            {
                SignatureLibraryStore.Save(_library);
                Reload();
            }

            var message = $"已导入 {added} 枚印章。";
            if (skipped > 0)
            {
                message += $"\n{skipped} 条因缺少图片被跳过。";
            }

            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            PluginLog.Run($"印章库导入：新增 {added} 条，跳过 {skipped} 条（{dialog.FileName}）");
        }
        catch (System.Exception ex)
        {
            ReportError("导入印章库失败", ex);
        }
    }

    // ───────────────────────────── 文件夹操作 ─────────────────────────────

    /// <summary>
    /// 扫描「签章」图片文件夹，把还没入库的图片登记成印章（名称取文件名）。
    /// <para>
    /// 与「录入新印章」的分工：录入是「选一张图 → 填名称/类型/尺寸」，适合精雕细琢；
    /// 扫描是「一次把几十张扫描件丢进文件夹 → 点一下全部登记」，名称先用文件名顶着，
    /// 之后再逐条改名或双击编辑。
    /// </para>
    /// </summary>
    private void ScanFolder()
    {
        try
        {
            // 先记下已有的 Id：RegisterImagesFromFolder 只返回新增数量，
            // 要按开关给新条目去白底就得自己认出「哪些是这次新加的」。
            var knownIds = new HashSet<string>(
                _library.Items.Select(x => x.Id ?? ""),
                StringComparer.Ordinal);

            var added = SignatureLibraryStore.RegisterImagesFromFolder(
                _library, SignatureLibraryStore.StampKind);

            var processed = 0;
            if (added > 0 && AutoRemoveBackground)
            {
                var settings = new ImageProcessSettings { RemoveBackground = true };
                var newItems = _library.Items
                    .Where(x => !knownIds.Contains(x.Id ?? "")
                                && SignatureLibraryStore.NormalizeKind(x.Kind) == SignatureLibraryStore.StampKind)
                    .ToList();

                foreach (var item in newItems)
                {
                    try
                    {
                        // 走既有图片处理管道而不是直接改文件：它会先把原图备份成 xxx.orig.png，
                        // 用户觉得去多了还能在「图片处理」里一键还原。
                        SignatureLibraryStore.ApplyImageProcessing(item, settings);
                        processed++;
                    }
                    catch (System.Exception ex)
                    {
                        // 单张失败不能拖垮整批：登记已经完成，只是这枚没去底，手工再处理即可。
                        PluginLog.Error("签章", $"扫描入库后去白底失败：{item.ImageFile} — {ex.Message}", ex);
                    }
                }
            }

            if (added > 0)
            {
                SignatureLibraryStore.Save(_library);
            }

            Reload();

            var directory = SignatureLibraryStore.StampDirectory;
            string message;
            if (added <= 0)
            {
                message = "文件夹里没有新的图片：\n" + directory
                    + "\n\n支持的格式：png / jpg / bmp / gif / tif（非 PNG 会自动转成 PNG）。\n"
                    + "可以点「打开图片目录」把图片拷进去，再点一次「扫描文件夹」。";
            }
            else if (AutoRemoveBackground)
            {
                message = $"已从文件夹登记 {added} 枚印章，其中 {processed} 枚已去白底。\n{directory}"
                    + "\n\n原图已备份为 xxx.orig.png，可在「图片处理」里还原。";
            }
            else
            {
                message = $"已从文件夹登记 {added} 枚印章（未去白底）。\n{directory}";
            }

            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            PluginLog.Run($"印章库扫描文件夹：新增 {added} 枚，去白底 {processed} 枚（{directory}）");
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, "扫描文件夹失败：" + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PluginLog.Error("签章", "扫描文件夹失败：" + ex.Message, ex);
        }
    }

    /// <summary>在资源管理器里打开印章图片目录（不存在就先建出来，否则打开会报"找不到"）。</summary>
    private void OpenImagesFolder()
    {
        try
        {
            var directory = SignatureLibraryStore.StampDirectory;
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start("explorer.exe", directory);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(this, "打开目录失败：" + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            PluginLog.Error("签章", "打开印章图片目录失败：" + ex.Message, ex);
        }
    }

    private static string? PickImageFile()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "请选择印章图片…",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
    }

    private void ReportError(string title, System.Exception ex)
    {
        MessageBox.Show(this, $"{title}：{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Error);
        PluginLog.Error("签章", $"{title}：{ex.Message}", ex);
    }

    private static BitmapSource? LoadImage(string path, out string error)
    {
        // 统一走 PreviewImageLoader：WPF 解码失败会自动用 GDI+ 兜底，
        // 并把「文件不存在 / 解不出来」的原因带回来，便于在状态列里区分。
        var image = PreviewImageLoader.Load(path, out var loadError);
        error = loadError ?? "";
        return image;
    }

    /// <summary>表格一行。字段都在创建时算好：表格是只读的，不需要通知属性变更。</summary>
    private sealed class StampRow
    {
        private static readonly Brush UsableBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F));
        private static readonly Brush ProblemBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));

        public int Index { get; private set; }

        public SignatureItem Item { get; private set; } = new();

        /// <summary>占位行：只为把空白区也画成格子，没有任何数据，也不可交互。</summary>
        public bool IsPlaceholder { get; private set; }

        public string IndexText => IsPlaceholder ? "" : Index.ToString(CultureInfo.InvariantCulture);

        public bool ShowDelete => !IsPlaceholder;

        public BitmapSource? Preview { get; private set; }

        public string Name => IsPlaceholder ? "" : string.IsNullOrWhiteSpace(Item.Name) ? "（未命名）" : Item.Name;

        public string AuthCodeText => IsPlaceholder
            ? ""
            : Item.AuthCode > 0
                ? Item.AuthCode.ToString(CultureInfo.InvariantCulture)
                : "未设置";

        public string ValidToText
        {
            get
            {
                if (IsPlaceholder)
                {
                    return "";
                }

                if (string.IsNullOrWhiteSpace(Item.ValidTo))
                {
                    return "长期";
                }

                // 截图里是 2025.2.12 这种紧凑写法，比 ISO 的短横线更像图签上的日期。
                return DateTime.TryParse(Item.ValidTo, out var date)
                    ? date.ToString("yyyy.M.d", CultureInfo.InvariantCulture)
                    : Item.ValidTo;
            }
        }

        /// <summary>宽 x 高（毫米）。宽度为 0 表示不参与换算、完全按图框槽位大小。</summary>
        public string SizeText { get; private set; } = "";

        public string TypeText => IsPlaceholder
            ? ""
            : string.IsNullOrWhiteSpace(Item.StampType) ? "未分类" : Item.StampType;

        public string StatusText { get; private set; } = "";

        public Brush StatusBrush { get; private set; } = UsableBrush;

        /// <summary>状态列的鼠标悬停说明：图片缺失时给出查找过的目录，读取失败时给出解码报错。</summary>
        public string StatusDetail { get; private set; } = "";

        /// <summary>没有说明时返回 null —— 空字符串会让 WPF 弹出一个空白提示框。</summary>
        public string? StatusToolTip => string.IsNullOrEmpty(StatusDetail) ? null : StatusDetail;

        public static StampRow Placeholder() => new() { IsPlaceholder = true };

        public static StampRow Create(int index, SignatureItem item)
        {
            var path = SignatureLibraryStore.LocateImageFile(item);
            BitmapSource? preview = null;
            var detail = "";

            if (string.IsNullOrEmpty(path))
            {
                // 找不到文件：把「应该在哪个目录、叫什么名字」写进提示里，用户能自己核对。
                detail = $"未找到图片文件：{item.ImageFile}\n"
                    + $"已查找：{SignatureLibraryStore.StampDirectory}";
            }
            else
            {
                preview = LoadImage(path, out var error);
                if (preview == null)
                {
                    detail = $"图片无法读取：{path}\n{error}";
                }
            }

            // 尺寸列：填了宽度/高度就显示标注值；只填其一、另一项由图片纵横比推出（不知道比例时显示 ?）。
            // 取值走统一入口（图片处理里定的尺寸优先），旧条目只有 WidthMm/HeightMm 也能正确显示。
            item.TryGetEffectiveSizeMm(out var sizeWidth, out var sizeHeight);
            var sizeText = "按槽位";
            var hasRatio = preview != null && preview.PixelWidth > 0 && preview.PixelHeight > 0;
            var ratio = hasRatio ? preview!.PixelHeight / (double)preview.PixelWidth : 0d;
            if (sizeWidth > 0.1 && sizeHeight > 0.1)
            {
                sizeText = $"{sizeWidth:0.#} x {sizeHeight:0.#}";
            }
            else if (sizeWidth > 0.1)
            {
                sizeText = hasRatio
                    ? $"{sizeWidth:0.#} x {sizeWidth * ratio:0.#}"
                    : $"{sizeWidth:0.#} x ?";
            }
            else if (sizeHeight > 0.1)
            {
                sizeText = hasRatio && ratio > 0
                    ? $"{sizeHeight / ratio:0.#} x {sizeHeight:0.#}"
                    : $"x {sizeHeight:0.#}";
            }

            var row = new StampRow
            {
                Index = index,
                Item = item,
                Preview = preview,
                SizeText = sizeText,
                StatusDetail = detail,
            };

            if (preview == null)
            {
                // 区分「文件没了」和「文件在但读不出来」——两者排查方向完全不同。
                row.StatusText = string.IsNullOrEmpty(path) ? "图片缺失" : "读取失败";
                row.StatusBrush = ProblemBrush;
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
    private sealed class StampPackage
    {
        public int Version { get; set; } = 1;

        public DateTime ExportedAt { get; set; } = DateTime.Now;

        public List<SignatureItem> Items { get; set; } = new();

        public Dictionary<string, string> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
