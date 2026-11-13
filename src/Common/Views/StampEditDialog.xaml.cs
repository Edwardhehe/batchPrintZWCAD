using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZwcadBatchPlot;

/// <summary>
/// 「录入新印章 / 编辑印章」对话框，按 MSteel 印章录入界面复刻：
/// 图片预览 + 印章名 + 授权码 + 类型（注册章/出图章）+ 印章宽度 + 有效期。
/// <para>
/// 只负责收集与校验，不碰库文件——图片入库、条目写入都由调用方（<see cref="StampLibraryForm"/>）完成，
/// 这样「新建」和「编辑」共用同一套校验，也不必让对话框知道存储细节。
/// </para>
/// </summary>
public sealed partial class StampEditDialog : Window
{
    /// <summary>新建时用户选中的源图片；编辑时为 null（沿用库里的图片）。</summary>
    private string? _pickedSourcePath;

    /// <summary>当前预览用的图片路径（编辑时是库里的文件，新建时是用户刚选的）。</summary>
    private string? _previewPath;

    /// <summary>最近一次图片处理用的参数；编辑已有印章时从条目里带出来。</summary>
    private ImageProcessSettings? _processSettings;

    public StampEditDialog()
    {
        InitializeComponent();

        _okButton.Click += (_, _) => TryAccept();
        _cancelButton.Click += (_, _) => { DialogResult = false; };
        _processButton.Click += (_, _) => ProcessImage();
        _validToPicker.SelectedDateChanged += (_, _) => NormalizeValidToText();

        // 尺寸提示随两个框实时更新：让用户一眼看到「这两个数就是图片处理里那份尺寸」。
        _widthBox.TextChanged += (_, _) => UpdateSizeHint();
        _heightBox.TextChanged += (_, _) => UpdateSizeHint();

        Loaded += (_, _) =>
        {
            _nameBox.Focus();
            NormalizeValidToText();
            UpdateSizeHint();
        };
    }

    /// <summary>
    /// 尺寸来源提示：0 表示跟随图框槽位，否则说明按标注尺寸出图。
    /// 两个框和「图片处理」里的毫米尺寸是同一个值，这里直接说清楚，省得用户以为是两套配置。
    /// </summary>
    private void UpdateSizeHint()
    {
        if (_sizeHint == null)
        {
            return;
        }

        var width = ReadLength(_widthBox.Text);
        var height = ReadLength(_heightBox.Text);
        const string tail = "（与「图片处理」里的尺寸是同一个值）";

        if (width <= 0 && height <= 0)
        {
            _sizeHint.Text = "跟随图框槽位等比内接" + tail;
            return;
        }

        _sizeHint.Text = height > 0
            ? $"出图 {width:0.##}×{height:0.##} mm" + tail
            : $"出图宽 {width:0.##} mm，高度按图片比例" + tail;
    }

    /// <summary>读框里的毫米数；空/非法/负数一律当 0（=跟随槽位）。</summary>
    private static double ReadLength(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return 0;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
    }

    /// <summary>
    /// 把有效期文本框改成「2025年2月12日」这种写法。
    /// <para>
    /// DatePicker 的长日期格式在中文下是「2025年2月12日, 星期三」，原界面只到日。
    /// 只改显示文本：选值以 <see cref="DatePicker.SelectedDate"/> 为准，用户在框里手打的日期照常解析。
    /// </para>
    /// </summary>
    private void NormalizeValidToText()
    {
        try
        {
            if (_validToPicker.SelectedDate is not DateTime date)
            {
                return;
            }

            var text = date.ToString("yyyy年M月d日", CultureInfo.CurrentCulture);
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        if (_validToPicker.Template?.FindName("PART_TextBox", _validToPicker)
                            is System.Windows.Controls.Primitives.DatePickerTextBox box)
                        {
                            box.Text = text;
                        }
                    }
                    catch
                    {
                        // 模板还没套上就跳过，等下一次日期变化或 Loaded 再刷。
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch
        {
            // 纯显示修饰，失败就保留系统默认格式，不影响取日期。
        }
    }

    /// <summary>新建：必须已经选好源图片。</summary>
    public static StampEditDialog ForNew(string sourceImagePath)
    {
        var dialog = new StampEditDialog
        {
            Title = "录入新印章",
        };
        dialog._pickedSourcePath = sourceImagePath;
        dialog._previewPath = sourceImagePath;
        dialog._nameBox.Text = Path.GetFileNameWithoutExtension(sourceImagePath);
        dialog._authCodeBox.Password = "0";
        dialog._widthBox.Text = "0";
        dialog._heightBox.Text = "0";
        dialog._registerRadio.IsChecked = true;
        dialog.RefreshPreview();
        return dialog;
    }

    /// <summary>编辑已有条目：图片沿用库里的文件，其他字段读出来预填。</summary>
    public static StampEditDialog ForExisting(SignatureItem item)
    {
        var dialog = new StampEditDialog
        {
            Title = "编辑印章",
        };

        dialog._previewPath = SignatureLibraryStore.ResolveImagePath(item);
        dialog._processSettings = item.ImageProcess;
        dialog._nameBox.Text = item.Name ?? "";
        dialog._authCodeBox.Password = Math.Max(0, item.AuthCode).ToString(CultureInfo.InvariantCulture);

        // 尺寸走统一入口：图片处理里定下的尺寸优先，其次才看旧字段。
        // 这样「图片处理里设了 8.5mm、这里却显示 0」的割裂不会再出现。
        item.TryGetEffectiveSizeMm(out var widthMm, out var heightMm);
        dialog._widthBox.Text = widthMm > 0
            ? widthMm.ToString("0.##", CultureInfo.InvariantCulture)
            : "0";
        dialog._heightBox.Text = heightMm > 0
            ? heightMm.ToString("0.##", CultureInfo.InvariantCulture)
            : "0";

        // 未分类的历史条目默认落在注册章上，避免出现「两个都没选」的空状态。
        if (string.Equals(item.StampType, "出图章", StringComparison.Ordinal))
        {
            dialog._issueRadio.IsChecked = true;
        }
        else
        {
            dialog._registerRadio.IsChecked = true;
        }

        if (DateTime.TryParse(item.ValidTo, out var validTo))
        {
            dialog._validToPicker.SelectedDate = validTo;
        }

        dialog.RefreshPreview();
        return dialog;
    }

    /// <summary>用户选中的源图片；编辑未换图时为 null。</summary>
    public string? PickedSourcePath => _pickedSourcePath;

    /// <summary>
    /// <see cref="PickedSourcePath"/> 是否已经过「图片处理」（去底 / 修剪 / 缩放）。
    /// 入库时据此跳过再次去白底——已经处理过的图再跑一遍会二次削弱笔画边缘。
    /// </summary>
    public bool PickedIsProcessed { get; private set; }

    /// <summary>
    /// 图片处理参数（去底色 / 修剪 / 像素尺寸 / 标注尺寸）。
    /// <para>
    /// 调用方保存时把它整体写回 <see cref="SignatureItem.ImageProcess"/>——尺寸的真值在这里，
    /// 不在 <see cref="WidthMm"/> / <see cref="HeightMm"/> 那两个只读输出属性上。
    /// </para>
    /// </summary>
    public ImageProcessSettings? ProcessSettings => _processSettings;

    public string StampName { get; private set; } = "";

    public int AuthCode { get; private set; }

    public string StampType { get; private set; } = "注册章";

    /// <summary>印章宽度（毫米），0 表示按图框槽位尺寸等比内接。</summary>
    public double WidthMm { get; private set; }

    /// <summary>印章高度（毫米），0 表示由宽度与图片纵横比推出。</summary>
    public double HeightMm { get; private set; }

    /// <summary>有效期止（yyyy-MM-dd），空串表示长期有效。</summary>
    public string ValidTo { get; private set; } = "";

    /// <summary>
    /// 预览框双击换图。截图里的录入界面没有「选择图片」按钮——图片是点「录入新印章」时先选的，
    /// 进来之后只在需要换图时用双击，界面因此能保持和原版一样紧凑。
    /// </summary>
    private void Preview_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            PickImage();
            e.Handled = true;
        }
    }

    private void PickImage()
    {
        using var dialog = new System.Windows.Forms.OpenFileDialog
        {
            Title = "请选择印章图片…",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        _pickedSourcePath = dialog.FileName;
        _previewPath = dialog.FileName;
        PickedIsProcessed = false;

        // 名称还是空的、或者仍是上一张图的名字时，跟着换成新文件名，省得用户手改。
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _nameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }

        RefreshPreview();
    }

    /// <summary>
    /// 图片处理：去底色 / 修剪空白 / 调整尺寸。
    /// <para>
    /// 这里不能直接改库里的文件（新建时条目还不存在），所以把处理结果写成一张临时 PNG，
    /// 当成「换了一张新图」交给调用方入库；<see cref="PickedIsProcessed"/> 标记已处理，
    /// 入库时就不再重复去底。
    /// </para>
    /// </summary>
    private void ProcessImage()
    {
        if (string.IsNullOrWhiteSpace(_previewPath) || !File.Exists(_previewPath))
        {
            Warn("请先选择印章图片。");
            return;
        }

        var dialog = ImageProcessDialog.Open(
            this,
            _previewPath,
            _processSettings,
            canRestore: false,
            string.IsNullOrWhiteSpace(_nameBox.Text) ? "" : _nameBox.Text.Trim());
        if (dialog == null || dialog.ShowDialog() != true || dialog.Settings == null)
        {
            return;
        }

        try
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetFileName(TitleBlockLibraryStore.DefaultDirectory), "ImageProcess");
            Directory.CreateDirectory(tempDirectory);
            var target = Path.Combine(
                tempDirectory,
                "stamp-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");

            using (var source = LoadBitmap(_previewPath!))
            using (var output = ImageProcessor.Process(source, dialog.Settings))
            {
                output.Save(target, System.Drawing.Imaging.ImageFormat.Png);
            }

            _processSettings = dialog.Settings.Clone();
            _pickedSourcePath = target;
            _previewPath = target;
            PickedIsProcessed = true;

            // 图片处理里填的毫米尺寸就是这里的印章宽度/高度，处理完立刻带过来，
            // 免得用户还要在两个对话框之间对一遍数字。
            if (_processSettings.TryGetDeclaredSizeMm(out var widthMm, out var heightMm))
            {
                if (widthMm > 0)
                {
                    _widthBox.Text = widthMm.ToString("0.##", CultureInfo.InvariantCulture);
                }

                if (heightMm > 0)
                {
                    _heightBox.Text = heightMm.ToString("0.##", CultureInfo.InvariantCulture);
                }
            }

            RefreshPreview();
        }
        catch (Exception ex)
        {
            Warn("图片处理失败：" + ex.Message);
        }
    }

    private static System.Drawing.Bitmap LoadBitmap(string path)
    {
        // 先读进内存再解码：直接 Image.FromFile 会一直持有文件句柄。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var image = System.Drawing.Image.FromStream(stream);
        return ImageProcessor.CloneArgb(image);
    }

    private void RefreshPreview()
    {
        var image = PreviewImageLoader.Load(_previewPath, out var error);
        _preview.Source = image;
        _previewHint.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
        _previewHint.Text = string.IsNullOrWhiteSpace(_previewPath)
            ? "未选择图片"
            : "图片读取失败：" + error;
    }

    private void TryAccept()
    {
        var name = (_nameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Warn("请填写印章名。");
            _nameBox.Focus();
            return;
        }

        if (_previewPath == null || !File.Exists(_previewPath))
        {
            Warn("请先选择印章图片。");
            return;
        }

        var authText = (_authCodeBox.Password ?? "").Trim();
        if (authText.Length == 0)
        {
            authText = "0";
        }

        if (!int.TryParse(authText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var authCode)
            || authCode < 0
            || authCode > 999999)
        {
            Warn("授权码必须是 0～999999 之间的整数（0 表示无需授权）。");
            _authCodeBox.Focus();
            return;
        }

        var widthText = (_widthBox.Text ?? "").Trim();
        if (widthText.Length == 0)
        {
            widthText = "0";
        }

        if (!double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            || width < 0
            || width > 2000)
        {
            Warn("印章宽度必须是 0～2000 之间的数字（毫米），0 表示按图框槽位大小自动适配。");
            _widthBox.Focus();
            return;
        }

        var heightText = (_heightBox.Text ?? "").Trim();
        if (heightText.Length == 0)
        {
            heightText = "0";
        }

        if (!double.TryParse(heightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var height)
            || height < 0
            || height > 2000)
        {
            Warn("印章高度必须是 0～2000 之间的数字（毫米），0 表示按宽度与图片比例自动推算。");
            _heightBox.Focus();
            return;
        }

        StampName = name;
        AuthCode = authCode;
        StampType = _issueRadio.IsChecked == true ? "出图章" : "注册章";
        WidthMm = width;
        HeightMm = height;
        ValidTo = _validToPicker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

        // 这两个框就是「标注尺寸」的编辑入口：写回同一份处理参数，保存时由
        // StampLibraryForm 统一落库（ImageProcess 为真值 + 旧字段镜像）。
        // 只改尺寸不动像素——用户想让图更清晰应该去图片处理里调分辨率。
        if (_processSettings == null)
        {
            _processSettings = new ImageProcessSettings();
        }

        _processSettings.PhysicalWidthMm = width;
        _processSettings.PhysicalHeightMm = height;

        DialogResult = true;
    }

    private void Warn(string message)
    {
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
