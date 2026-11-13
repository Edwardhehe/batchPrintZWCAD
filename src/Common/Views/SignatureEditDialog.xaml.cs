using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZwcadBatchPlot;

/// <summary>
/// 「录入新签名 / 编辑签名」对话框，结构与 <see cref="StampEditDialog"/> 完全一致
/// （预览 + 图片处理 + 名称 + 属性 + 确定/取消），只是字段换成签名的业务属性：
/// 不透明度、有效期起、有效期止。
/// <para>
/// 只负责收集与校验，不碰库文件——图片入库、条目写入都由调用方
/// （<see cref="SignatureLibraryManagerForm"/>）完成，新建与编辑共用同一套校验。
/// </para>
/// </summary>
public sealed partial class SignatureEditDialog : Window
{
    /// <summary>新建时用户选中的源图片；编辑时为 null（沿用库里的图片）。</summary>
    private string? _pickedSourcePath;

    /// <summary>当前预览用的图片路径（编辑时是库里的文件，新建时是用户刚选的）。</summary>
    private string? _previewPath;

    /// <summary>最近一次图片处理用的参数；编辑已有条目时从条目里带出来。</summary>
    private ImageProcessSettings? _processSettings;

    public SignatureEditDialog()
    {
        InitializeComponent();

        _okButton.Click += (_, _) => TryAccept();
        _cancelButton.Click += (_, _) => { DialogResult = false; };
        _processButton.Click += (_, _) => ProcessImage();

        // DatePicker 选完日期后把文本改成「2025年2月12日」（详见 NormalizeDateText）。
        _validFromPicker.SelectedDateChanged += (_, _) => NormalizeDateText(_validFromPicker);
        _validToPicker.SelectedDateChanged += (_, _) => NormalizeDateText(_validToPicker);

        Loaded += (_, _) =>
        {
            _nameBox.Focus();
            _nameBox.SelectAll();
            NormalizeDateText(_validFromPicker);
            NormalizeDateText(_validToPicker);
        };
    }

    /// <summary>新建：必须已经选好源图片。</summary>
    public static SignatureEditDialog ForNew(string sourceImagePath)
    {
        var dialog = new SignatureEditDialog
        {
            Title = "录入新签名",
        };
        dialog._pickedSourcePath = sourceImagePath;
        dialog._previewPath = sourceImagePath;
        dialog._nameBox.Text = Path.GetFileNameWithoutExtension(sourceImagePath);
        dialog._opacityBox.Text = "1";
        dialog.RefreshPreview();
        return dialog;
    }

    /// <summary>编辑已有条目：图片沿用库里的文件，其他字段读出来预填。</summary>
    public static SignatureEditDialog ForExisting(SignatureItem item)
    {
        var dialog = new SignatureEditDialog
        {
            Title = "编辑签名",
        };

        dialog._previewPath = SignatureLibraryStore.ResolveImagePath(item);
        dialog._processSettings = item.ImageProcess;
        dialog._nameBox.Text = item.Name ?? "";
        dialog._opacityBox.Text = Math.Max(0, Math.Min(1, item.Opacity))
            .ToString("0.##", CultureInfo.CurrentCulture);

        if (DateTime.TryParse(item.ValidFrom, out var validFrom))
        {
            dialog._validFromPicker.SelectedDate = validFrom;
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
    /// <see cref="PickedSourcePath"/> 是否已经过「图片处理」（去底 / 修剪 / 缩放 / 清晰度）。
    /// 入库时据此跳过再次去白底——已经处理过的图再跑一遍会二次削弱笔画边缘。
    /// </summary>
    public bool PickedIsProcessed { get; private set; }

    public string SignatureName { get; private set; } = "";

    /// <summary>绘制不透明度 0~1。</summary>
    public new double Opacity { get; private set; } = 1.0;

    /// <summary>有效期起（yyyy-MM-dd），空串表示不限。</summary>
    public string ValidFrom { get; private set; } = "";

    /// <summary>有效期止（yyyy-MM-dd），空串表示长期有效。</summary>
    public string ValidTo { get; private set; } = "";

    /// <summary>
    /// 把日期框里的文本改成「2025年2月12日」这种写法。
    /// <para>
    /// DatePicker 的长日期格式在中文下是「2025年2月12日, 星期三」，原界面只到日。
    /// 只改显示文本：选值仍以 <see cref="DatePicker.SelectedDate"/> 为准。
    /// </para>
    /// </summary>
    private static void NormalizeDateText(DatePicker picker)
    {
        try
        {
            if (picker.SelectedDate is not DateTime date)
            {
                return;
            }

            var text = date.ToString("yyyy年M月d日", CultureInfo.CurrentCulture);
            picker.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        // 模板还没套上时 FindName 会返回 null，等下一次日期变化或 Loaded 再刷。
                        if (picker.Template?.FindName("PART_TextBox", picker)
                            is System.Windows.Controls.Primitives.DatePickerTextBox box)
                        {
                            box.Text = text;
                        }
                    }
                    catch
                    {
                        // 纯显示修饰，失败就保留系统默认格式，不影响取日期。
                    }
                }),
                DispatcherPriority.Loaded);
        }
        catch
        {
            // 同上：显示失败不影响取值。
        }
    }

    /// <summary>预览框双击换图（截图里的录入界面没有「选择图片」按钮，界面因此能保持紧凑）。</summary>
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
            Title = "请选择签名图片…",
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

        // 名称还是空的就跟着换成新文件名，省得用户手改。
        if (string.IsNullOrWhiteSpace(_nameBox.Text))
        {
            _nameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }

        RefreshPreview();
    }

    /// <summary>
    /// 图片处理：去底色 / 修剪空白 / 调整尺寸 / 清晰度。
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
            Warn("请先选择签名图片。");
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
                "sign-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");

            using (var source = LoadBitmap(_previewPath!))
            using (var output = ImageProcessor.Process(source, dialog.Settings))
            {
                output.Save(target, System.Drawing.Imaging.ImageFormat.Png);
            }

            _processSettings = dialog.Settings.Clone();
            _pickedSourcePath = target;
            _previewPath = target;
            PickedIsProcessed = true;
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
            : "图片读取失败：" + (string.IsNullOrWhiteSpace(error) ? "未知原因" : error);
    }

    private void TryAccept()
    {
        var name = (_nameBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Warn("请填写签名名。");
            _nameBox.Focus();
            return;
        }

        if (_previewPath == null || !File.Exists(_previewPath))
        {
            Warn("请先选择签名图片。");
            return;
        }

        var opacityText = (_opacityBox.Text ?? "").Trim();
        if (opacityText.Length == 0)
        {
            opacityText = "1";
        }

        if (!TryParseOpacity(opacityText, out var opacity))
        {
            Warn("不透明度必须是 0～1 之间的数字（1 为完全不透明）。");
            _opacityBox.Focus();
            return;
        }

        var validFrom = _validFromPicker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        var validTo = _validToPicker.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

        if (validFrom.Length > 0 && validTo.Length > 0)
        {
            var from = _validFromPicker.SelectedDate!.Value.Date;
            var to = _validToPicker.SelectedDate!.Value.Date;
            if (from > to)
            {
                // 起晚于止等于永远不生效，出图时会静默跳过——这种错误必须在录入时拦住。
                Warn("「有效期起」不能晚于「有效期止」，否则这个签名永远不会生效。");
                _validFromPicker.Focus();
                return;
            }
        }

        SignatureName = name;
        Opacity = opacity;
        ValidFrom = validFrom;
        ValidTo = validTo;

        DialogResult = true;
    }

    private void Warn(string message)
    {
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 解析不透明度，允许三种写法：<c>0.85</c>、<c>85</c>、<c>85%</c>。
    /// <para>
    /// 界面标注的范围是 0～1，但实际用户（尤其习惯 Excel 百分比格式的）经常直接填 85，
    /// 严格按 0～1 拒绝会让人反复试错。这里把 1～100 与带 % 的写法一律按百分数换算，
    /// 既不会把 85 夹成 1，也不用弹提示去教用户怎么填。
    /// </para>
    /// </summary>
    private static bool TryParseOpacity(string text, out double opacity)
    {
        opacity = 1;
        var isPercent = text.EndsWith("%", StringComparison.Ordinal);
        var numberText = isPercent ? text.TrimEnd('%').Trim() : text;

        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.CurrentCulture, out var value))
        {
            return false;
        }

        if (isPercent || value > 1.0)
        {
            value /= 100d;
        }

        if (value < 0 || value > 1)
        {
            return false;
        }

        opacity = value;
        return true;
    }
}
