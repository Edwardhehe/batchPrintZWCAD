using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZwcadBatchPlot;

/// <summary>
/// 「图片处理」对话框：去底色 / 修剪四周空白 / 调整像素尺寸，带实时预览。
/// <para>
/// 只负责<b>收集参数</b>，不写文件、不碰签名库——应用由调用方完成
/// （签名库管理窗口调 <see cref="SignatureLibraryStore.ApplyImageProcessing"/>，
/// 印章录入对话框则把结果当成「换了一张新图」）。这样同一个对话框能同时服务
/// 「已经有库条目的图片」和「刚选进来还没入库的新图」。
/// </para>
/// </summary>
public sealed partial class ImageProcessDialog : Window
{
    private const double PreviewMaxWidth = 620;
    private const double PreviewMaxHeight = 236;

    /// <summary>全分辨率原图（用于最终参数计算与尺寸显示）。</summary>
    private System.Drawing.Bitmap? _source;

    /// <summary>预览用的缩略图；原图本来就够小时为 null（直接用原图预览，参数零折算）。</summary>
    private System.Drawing.Bitmap? _previewBitmap;

    /// <summary>缩略图相对原图的比例。1 = 没缩。</summary>
    private double _previewScale = 1;

    private bool _updating;
    private bool _previewPending;
    private readonly DispatcherTimer _previewTimer;

    /// <summary>
    /// 已标注的物理宽度（毫米）。与「编辑印章」里的印章宽度是同一个数——
    /// 本对话框只在用户以「毫米」为单位改尺寸时才覆盖它。
    /// </summary>
    private double _declaredWidthMm;

    /// <summary>已标注的物理高度（毫米）。约束比例时为按比例推出的值，否则为用户填的值。</summary>
    private double _declaredHeightMm;

    private ImageProcessDialog()
    {
        // 定时器必须在 InitializeComponent 之前建好：XAML 里挂了 ValueChanged / TextChanged，
        // 构建控件树时就会触发一次，那时若定时器还是 null 会直接崩在构造函数里。
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(70),
        };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            if (_previewPending)
            {
                _previewPending = false;
                RefreshPreview();
            }
        };

        InitializeComponent();

        _removeBgCheck.Checked += (_, _) => OnOptionChanged();
        _removeBgCheck.Unchecked += (_, _) => OnOptionChanged();
        _trimCheck.Checked += (_, _) => OnOptionChanged();
        _trimCheck.Unchecked += (_, _) => OnOptionChanged();
        _resizeCheck.Checked += (_, _) => OnOptionChanged();
        _resizeCheck.Unchecked += (_, _) => OnOptionChanged();
        _sharpnessCheck.Checked += (_, _) =>
        {
            // 加载已有参数时也会触发一次 Checked，那时不能把用户存下的值覆盖成默认 30。
            if (_updating)
            {
                return;
            }

            // 勾上却停在 0 等于什么都没做，用户会以为坏了：默认给一个肉眼可见又不过火的 +30。
            if (ReadSharpness() == 0)
            {
                SetSharpnessValue(30);
            }

            OnOptionChanged();
        };
        _sharpnessCheck.Unchecked += (_, _) =>
        {
            if (_updating)
            {
                return;
            }

            OnOptionChanged();
        };
        _restoreButton.Click += (_, _) =>
        {
            RestoreRequested = true;
            Settings = null;
            DialogResult = true;
        };

        _okButton.Click += (_, _) =>
        {
            Settings = BuildSettings();
            RestoreRequested = false;
            DialogResult = true;
        };

        _cancelButton.Click += (_, _) => { DialogResult = false; };

        Closed += (_, _) => DisposeBitmaps();
    }

    /// <summary>用户点「应用」后的处理参数；点「恢复原图」或取消时为 null。</summary>
    public ImageProcessSettings? Settings { get; private set; }

    /// <summary>用户点了「恢复原图」（放弃全部处理）。</summary>
    public bool RestoreRequested { get; private set; }

    /// <summary>
    /// 打开对话框。
    /// <paramref name="basePath"/> 建议传「原图备份」而不是当前已处理的图，
    /// 这样反复调整不会一层层劣化（见 <see cref="SignatureLibraryStore.GetProcessingBasePath"/>）。
    /// </summary>
    public static ImageProcessDialog? Open(
        Window? owner,
        string? basePath,
        ImageProcessSettings? initial,
        bool canRestore,
        string titleSuffix = "")
    {
        if (string.IsNullOrWhiteSpace(basePath) || !File.Exists(basePath))
        {
            MessageBox.Show(
                "找不到要处理的图片文件，请先重新导入图片。",
                "图片处理",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }

        var dialog = new ImageProcessDialog();
        if (owner != null && owner.IsLoaded)
        {
            dialog.Owner = owner;
        }

        if (!string.IsNullOrWhiteSpace(titleSuffix))
        {
            dialog.Title = "图片处理 — " + titleSuffix;
        }

        if (!dialog.LoadImage(basePath!, initial))
        {
            dialog.DisposeBitmaps();
            return null;
        }

        dialog._restoreButton.IsEnabled = canRestore;
        dialog._restoreButton.Visibility = canRestore ? Visibility.Visible : Visibility.Collapsed;
        return dialog;
    }

    private bool LoadImage(string path, ImageProcessSettings? initial)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var image = System.Drawing.Image.FromStream(stream);
            _source = ImageProcessor.CloneArgb(image);
        }
        catch (Exception ex)
        {
            // 此时窗口还没显示，不能拿自己当 MessageBox 的 owner（没有窗口句柄）。
            MessageBox.Show(
                "图片读取失败：" + ex.Message,
                "图片处理",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        // 大图先缩一份给预览用：手机拍的签名动辄 3000 像素，
        // 每拖一次滑块都在全图上跑一遍去底会明显卡顿。
        var scale = Math.Min(
            PreviewMaxWidth / _source.Width,
            PreviewMaxHeight / _source.Height);
        if (scale < 1)
        {
            _previewScale = scale;
            _previewBitmap = ImageProcessor.ApplyResize(
                _source,
                Math.Max(1, (int)Math.Round(_source.Width * scale)));
        }

        _updating = true;
        try
        {
            var settings = initial ?? new ImageProcessSettings
            {
                // 默认就去底色：签名/印章九成是白底扫描件，这是用户最常做的第一步。
                RemoveBackground = true,
                BackgroundThreshold = ImageProcessor.DefaultThreshold,
                TrimPaddingPx = 2,
            };

            _removeBgCheck.IsChecked = settings.RemoveBackground;
            _thresholdSlider.Value = Math.Max(
                _thresholdSlider.Minimum,
                Math.Min(_thresholdSlider.Maximum, settings.BackgroundThreshold));
            _thresholdBox.Text = ((int)_thresholdSlider.Value).ToString(CultureInfo.InvariantCulture);

            _trimCheck.IsChecked = settings.Trim;
            _trimPadBox.Text = Math.Max(0, settings.TrimPaddingPx).ToString(CultureInfo.InvariantCulture);

            var sharpness = Math.Max(
                ImageProcessor.MinSharpness,
                Math.Min(ImageProcessor.MaxSharpness, settings.Sharpness));
            _sharpnessCheck.IsChecked = sharpness != 0;
            _sharpnessSlider.Value = sharpness;
            _sharpnessBox.Text = sharpness.ToString(CultureInfo.InvariantCulture);

            _dpiBox.Text = ImageProcessor.NormalizeDpi(settings.Dpi).ToString(CultureInfo.InvariantCulture);

            // 出图尺寸独立存（Physical*），先接住；下面若用户以毫米为单位改尺寸会覆盖它。
            _declaredWidthMm = settings.PhysicalWidthMm > 0 ? settings.PhysicalWidthMm : 0;
            _declaredHeightMm = settings.PhysicalHeightMm > 0 ? settings.PhysicalHeightMm : 0;

            var targetWidth = settings.TargetWidthPx > 0 ? settings.TargetWidthPx : _source.Width;
            _resizeCheck.IsChecked = settings.TargetWidthPx > 0 || settings.TargetHeightPx > 0;

            // 有标注尺寸时默认按毫米打开：用户关心的是「打出来多大」，像素数只是手段。
            // 没标注（=跟随槽位）才回到像素，避免平白给一个和出图无关的毫米数。
            _resizeUnitCombo.SelectedIndex = _declaredWidthMm > 0 ? 1 : 0;
            _resizeWidthBox.Text = _declaredWidthMm > 0
                ? _declaredWidthMm.ToString("0.##", CultureInfo.CurrentCulture)
                : targetWidth.ToString(CultureInfo.InvariantCulture);

            // 高度：约束比例时由宽度推出（显示用，勾着的时候不可编辑）。
            _lockRatioCheck.IsChecked = settings.LockAspectRatio;
            var targetHeight = settings.TargetHeightPx > 0
                ? settings.TargetHeightPx
                : (int)Math.Round(targetWidth * _source.Height / (double)Math.Max(1, _source.Width));
            _resizeHeightBox.Text = _declaredHeightMm > 0
                ? _declaredHeightMm.ToString("0.##", CultureInfo.CurrentCulture)
                : targetHeight.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _updating = false;
        }

        UpdateRowState();
        RefreshUnitLabel();
        RefreshPreview();
        return true;
    }

    private void OnOptionChanged()
    {
        if (_updating)
        {
            return;
        }

        UpdateRowState();
        SchedulePreview();
    }

    private void UpdateRowState()
    {
        var removeBg = _removeBgCheck.IsChecked == true;
        var trim = _trimCheck.IsChecked == true;
        var resize = _resizeCheck.IsChecked == true;
        var sharpness = _sharpnessCheck.IsChecked == true;

        _thresholdRow.IsEnabled = removeBg;
        _thresholdSlider.IsEnabled = removeBg;
        _thresholdBox.IsEnabled = removeBg;

        _trimRow.IsEnabled = trim;
        _trimPadBox.IsEnabled = trim;

        _sharpnessRow.IsEnabled = sharpness;
        _sharpnessSlider.IsEnabled = sharpness;
        _sharpnessBox.IsEnabled = sharpness;

        _resizeRow.IsEnabled = resize;
        _resizeWidthBox.IsEnabled = resize;
        // 约束比例时高度框只读（不置灰）：值仍要看，但只能由宽度推，手改会被下一次回填覆盖。
        _resizeHeightBox.IsEnabled = resize;
        _resizeHeightBox.IsReadOnly = _lockRatioCheck.IsChecked == true;
        _lockRatioCheck.IsEnabled = resize;
        _resizeUnitCombo.IsEnabled = resize;
    }

    private void SchedulePreview()
    {
        if (!IsInitialized)
        {
            return;
        }

        _previewPending = true;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void RefreshPreview()
    {
        if (_source == null)
        {
            return;
        }

        var settings = BuildSettings();

        // 缩略图预览：像素类参数按预览比例折算，否则「留 2 像素边」「宽 600」在缩略图上会算出完全不同的效果。
        var previewSettings = settings.Clone();
        if (_previewScale < 1)
        {
            previewSettings.TrimPaddingPx = (int)Math.Round(settings.TrimPaddingPx * _previewScale);
            if (settings.TargetWidthPx > 0)
            {
                previewSettings.TargetWidthPx = Math.Max(
                    1,
                    (int)Math.Round(settings.TargetWidthPx * _previewScale));
            }

            if (settings.TargetHeightPx > 0)
            {
                previewSettings.TargetHeightPx = Math.Max(
                    1,
                    (int)Math.Round(settings.TargetHeightPx * _previewScale));
            }
        }

        var basis = _previewBitmap ?? _source;
        int width;
        int height;
        try
        {
            using var processed = ImageProcessor.Process(basis, previewSettings);
            width = _previewScale < 1 ? (int)Math.Round(processed.Width / _previewScale) : processed.Width;
            height = _previewScale < 1 ? (int)Math.Round(processed.Height / _previewScale) : processed.Height;
            _preview.Source = ToBitmapSource(processed);
        }
        catch (Exception ex)
        {
            _preview.Source = null;
            _previewHint.Text = "预览失败：" + ex.Message;
            _previewHint.Visibility = Visibility.Visible;
            return;
        }

        _previewHint.Visibility = Visibility.Collapsed;
        _sizeText.Text = DescribeSize(width, height, settings);

        // 「约束比例」勾着时，高度框只是宽度的换算结果：这里按当前（去底+修剪后的）比例回填，
        // 用户改宽度就能立刻看到对应高度，也不必自己算。
        var baseSize = ComputeBaseSize(settings);
        if (_lockRatioCheck.IsChecked == true)
        {
            var autoWidth = settings.TargetWidthPx > 0 ? settings.TargetWidthPx : baseSize.Width;
            var autoHeight = baseSize.Width > 0
                ? (int)Math.Round(autoWidth * baseSize.Height / (double)baseSize.Width)
                : baseSize.Height;
            SyncHeightBox(autoWidth, autoHeight);
        }

        var hintWidth = settings.TargetWidthPx > 0 ? settings.TargetWidthPx : baseSize.Width;
        var hintHeight = settings.TargetHeightPx > 0
            ? settings.TargetHeightPx
            : (baseSize.Width > 0
                ? (int)Math.Round(hintWidth * baseSize.Height / (double)baseSize.Width)
                : baseSize.Height);

        var resizeHint = _resizeCheck.IsChecked == true
            ? string.Format(
                CultureInfo.CurrentCulture,
                "≈ {0:0.#}×{1:0.#} mm（{2}dpi）",
                ImageProcessor.PixelsToMm(hintWidth, settings.Dpi),
                ImageProcessor.PixelsToMm(hintHeight, settings.Dpi),
                settings.Dpi)
            : string.Format(
                CultureInfo.CurrentCulture,
                "未缩放，保持 {0}×{1} px",
                baseSize.Width,
                baseSize.Height);

        // 出图尺寸是独立的一个数：不重采样也可能已经标了「打多大」。
        // 以毫米调整时上面已显示同样的数字，就不重复了。
        var showDeclared = _resizeCheck.IsChecked != true || _resizeUnitCombo.SelectedIndex != 1;
        if (showDeclared && settings.TryGetDeclaredSizeMm(out var declaredWidthMm, out var declaredHeightMm))
        {
            resizeHint += "｜出图尺寸 " + (declaredHeightMm > 0
                ? string.Format(CultureInfo.CurrentCulture, "{0:0.#}×{1:0.#} mm", declaredWidthMm, declaredHeightMm)
                : string.Format(CultureInfo.CurrentCulture, "宽 {0:0.#} mm", declaredWidthMm));
        }

        _resizeHint.Text = resizeHint;

        // 分辨率的作用要看得见：同一张图换个 DPI，毫米数就变了，否则用户不知道该填哪个值。
        _dpiHint.Text = string.Format(
            CultureInfo.CurrentCulture,
            "原图宽 {0} px ≈ {1:0.#} mm",
            _source.Width,
            ImageProcessor.PixelsToMm(_source.Width, settings.Dpi));
    }

    /// <summary>
    /// 去底 + 修剪之后、缩放之前的尺寸（换算回原图像素）。
    /// 「约束比例」用它的长宽比推高度——修剪会改变比例，用原图比例算会与实际结果差几个像素。
    /// </summary>
    private (int Width, int Height) ComputeBaseSize(ImageProcessSettings settings)
    {
        if (_source == null)
        {
            return (0, 0);
        }

        var baseSettings = settings.Clone();
        baseSettings.TargetWidthPx = 0;
        baseSettings.TargetHeightPx = 0;
        // 清晰度不改变尺寸，这里只关心长宽比，跳过可以省一遍模糊。
        baseSettings.Sharpness = 0;

        try
        {
            using var processed = ImageProcessor.Process(_previewBitmap ?? _source, baseSettings);
            if (_previewScale >= 1)
            {
                return (processed.Width, processed.Height);
            }

            return (
                (int)Math.Round(processed.Width / _previewScale),
                (int)Math.Round(processed.Height / _previewScale));
        }
        catch
        {
            return (_source.Width, _source.Height);
        }
    }

    /// <summary>按当前单位把推导出来的高度写进高度框（不改用户正在编辑的宽度）。</summary>
    private void SyncHeightBox(int widthPx, int heightPx)
    {
        if (_resizeCheck.IsChecked != true)
        {
            return;
        }

        var isMillimeter = _resizeUnitCombo.SelectedIndex == 1;
        var text = isMillimeter
            ? ImageProcessor.PixelsToMm(heightPx, CurrentDpi).ToString("0.##", CultureInfo.CurrentCulture)
            : heightPx.ToString(CultureInfo.InvariantCulture);

        // 约束比例时高度框就是「标注高度」的可视化：先记住它，
        // 这样即使下面因为文本没变而早退，BuildSettings 也能拿到同一个数。
        if (isMillimeter
            && double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var heightMm)
            && heightMm > 0)
        {
            _declaredHeightMm = heightMm;
        }

        if (string.Equals(_resizeHeightBox.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        _updating = true;
        try
        {
            _resizeHeightBox.Text = text;
        }
        finally
        {
            _updating = false;
        }
    }

    private string DescribeSize(int width, int height, ImageProcessSettings settings)
    {
        var dpiSuffix = settings.Dpi == ImageProcessor.DefaultDpiValue ? "" : $"，{settings.Dpi}dpi";
        var original = string.Format(
            CultureInfo.CurrentCulture,
            "原图 {0}×{1} px（≈ {2:0.#}×{3:0.#} mm{4}）",
            _source!.Width,
            _source.Height,
            ImageProcessor.PixelsToMm(_source.Width, settings.Dpi),
            ImageProcessor.PixelsToMm(_source.Height, settings.Dpi),
            dpiSuffix);

        if (!settings.HasEffect)
        {
            return original;
        }

        return original + string.Format(
            CultureInfo.CurrentCulture,
            "　→　处理后{0} {1}×{2} px（≈ {3:0.#}×{4:0.#} mm）｜{5}",
            // 预览是缩略图上算的，反算回原尺寸会有 ±1 像素误差，大图时标「约」不谎报精度。
            _previewScale < 1 ? "约" : "",
            width,
            height,
            ImageProcessor.PixelsToMm(width, settings.Dpi),
            ImageProcessor.PixelsToMm(height, settings.Dpi),
            settings.Describe());
    }

    /// <summary>把界面上的控件读成处理参数（不要求用户点确定）。</summary>
    private ImageProcessSettings BuildSettings()
    {
        var settings = new ImageProcessSettings
        {
            RemoveBackground = _removeBgCheck.IsChecked == true,
            BackgroundThreshold = (int)Math.Round(_thresholdSlider.Value),
            Trim = _trimCheck.IsChecked == true,
            TrimPaddingPx = ParseNonNegative(_trimPadBox.Text, 0),
            TargetWidthPx = 0,
            TargetHeightPx = 0,
            LockAspectRatio = _lockRatioCheck.IsChecked == true,
            Sharpness = _sharpnessCheck.IsChecked == true ? ReadSharpness() : 0,
            Dpi = CurrentDpi,

            // 出图尺寸：默认沿用已有标注，下面在「毫米」单位下再被用户输入覆盖。
            PhysicalWidthMm = _declaredWidthMm,
            PhysicalHeightMm = _declaredHeightMm,
        };

        if (settings.BackgroundThreshold < ImageProcessor.MinThreshold)
        {
            settings.BackgroundThreshold = ImageProcessor.MinThreshold;
        }

        if (settings.BackgroundThreshold > ImageProcessor.MaxThreshold)
        {
            settings.BackgroundThreshold = ImageProcessor.MaxThreshold;
        }

        if (_resizeCheck.IsChecked == true && _source != null)
        {
            var isMillimeter = _resizeUnitCombo.SelectedIndex == 1;
            var text = (_resizeWidthBox.Text ?? "").Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value > 0)
            {
                settings.TargetWidthPx = isMillimeter
                    ? ImageProcessor.MmToPixels(value, settings.Dpi)
                    : Math.Max(1, (int)Math.Round(value));

                if (isMillimeter)
                {
                    // 以毫米为单位填尺寸 = 直接声明「这枚章打出来多宽」，与「编辑印章」同一个数。
                    // 同时记进字段：用户接着把单位切回像素时，物理尺寸不该跟着丢掉。
                    settings.PhysicalWidthMm = value;
                    _declaredWidthMm = value;
                }
            }
            else
            {
                // 输入框空着/非法时按「原尺寸」处理，避免预览突然变形。
                settings.TargetWidthPx = _source.Width;
            }

            // 约束比例：只认宽度，高度交给 ApplyResize 按当前比例推（图片永不变形）。
            // 取消约束才读高度框；高度空着/非法同样当「按比例自动」。
            if (!settings.LockAspectRatio)
            {
                var heightText = (_resizeHeightBox.Text ?? "").Trim();
                if (double.TryParse(heightText, NumberStyles.Float, CultureInfo.CurrentCulture, out var heightValue)
                    && heightValue > 0)
                {
                    settings.TargetHeightPx = isMillimeter
                        ? ImageProcessor.MmToPixels(heightValue, settings.Dpi)
                        : Math.Max(1, (int)Math.Round(heightValue));

                    if (isMillimeter)
                    {
                        settings.PhysicalHeightMm = heightValue;
                        _declaredHeightMm = heightValue;
                    }
                }
            }
            else if (isMillimeter)
            {
                // 约束比例时高度由宽度按（去底修剪后的）实际比例推出，_declaredHeightMm
                // 已由 SyncHeightBox 更新成同一个换算结果，这里保持它即可。
                var heightText = (_resizeHeightBox.Text ?? "").Trim();
                if (double.TryParse(heightText, NumberStyles.Float, CultureInfo.CurrentCulture, out var autoHeight)
                    && autoHeight > 0)
                {
                    settings.PhysicalHeightMm = autoHeight;
                    _declaredHeightMm = autoHeight;
                }
            }
        }

        return settings;
    }

    /// <summary>界面上填的分辨率（非法值回落到 300dpi）。</summary>
    private int CurrentDpi => _dpiBox == null
        ? ImageProcessor.DefaultDpiValue
        : ImageProcessor.NormalizeDpi(ParseInt(_dpiBox.Text, 0));

    /// <summary>清晰度框里的值（0 或非法都当 0 = 不处理）。</summary>
    private int ReadSharpness()
    {
        var value = ParseInt(_sharpnessBox.Text, 0);
        return Math.Max(ImageProcessor.MinSharpness, Math.Min(ImageProcessor.MaxSharpness, value));
    }

    /// <summary>把清晰度同时写进滑块与输入框（供「勾上时给个默认值」等处调用）。</summary>
    private void SetSharpnessValue(int value)
    {
        value = Math.Max(ImageProcessor.MinSharpness, Math.Min(ImageProcessor.MaxSharpness, value));
        var previous = _updating;
        _updating = true;
        try
        {
            _sharpnessSlider.Value = value;
            _sharpnessBox.Text = value.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            // 恢复而不是直接置 false：本方法可能是在 LoadImage 的「批量赋值」过程中被调用的，
            // 直接置 false 会让后面的赋值逐个触发预览刷新，甚至把高度框冲掉。
            _updating = previous;
        }
    }

    private static int ParseInt(string? text, int fallback)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return fallback;
        }

        return int.TryParse(trimmed, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out var value)
            ? value
            : fallback;
    }

    private static int ParseNonNegative(string? text, int fallback)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return fallback;
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value >= 0
            ? value
            : fallback;
    }

    private void _thresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML 解析时给 Slider 设 Minimum/Maximum 就会触发 ValueChanged，
        // 那一刻控件树还没建完（_thresholdBox 是 null），直接返回。
        if (!IsInitialized || _updating)
        {
            return;
        }

        _updating = true;
        try
        {
            _thresholdBox.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _updating = false;
        }

        SchedulePreview();
    }

    private void _thresholdBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || _updating)
        {
            return;
        }

        if (!int.TryParse((_thresholdBox.Text ?? "").Trim(), out var value))
        {
            return;
        }

        value = Math.Max(ImageProcessor.MinThreshold, Math.Min(ImageProcessor.MaxThreshold, value));
        _updating = true;
        try
        {
            _thresholdSlider.Value = value;
        }
        finally
        {
            _updating = false;
        }

        SchedulePreview();
    }

    private void _trimPadBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || _updating)
        {
            return;
        }

        SchedulePreview();
    }

    private void _sharpnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || _updating)
        {
            return;
        }

        _updating = true;
        try
        {
            _sharpnessBox.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _updating = false;
        }

        SchedulePreview();
    }

    private void _sharpnessBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || _updating)
        {
            return;
        }

        // 输入到「-」这一步还没成数，直接返回，否则每敲一个减号都会被掰回 0。
        var text = (_sharpnessBox.Text ?? "").Trim();
        if (text.Length == 0 || text == "-")
        {
            return;
        }

        var value = ParseInt(text, 0);
        value = Math.Max(ImageProcessor.MinSharpness, Math.Min(ImageProcessor.MaxSharpness, value));

        _updating = true;
        try
        {
            _sharpnessSlider.Value = value;
        }
        finally
        {
            _updating = false;
        }

        SchedulePreview();
    }

    private void _dpiBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || _updating)
        {
            return;
        }

        // 「毫米 ↔ 像素」的换算基准变了，尺寸提示要跟着重算。
        RefreshUnitLabel();
        SchedulePreview();
    }

    /// <summary>
    /// 刷新单位下拉里的 dpi 数字（写着「毫米 (mm，300dpi)」的那个）。
    /// 分辨率一改这里就得跟着变，否则用户填 8.5mm 时看到的是过期的基准。
    /// </summary>
    private void RefreshUnitLabel()
    {
        if (_resizeUnitCombo == null || _resizeUnitCombo.Items.Count < 2)
        {
            return;
        }

        if (_resizeUnitCombo.Items[1] is not ComboBoxItem item)
        {
            return;
        }

        var text = $"毫米 (mm，{CurrentDpi}dpi)";
        if (!string.Equals(item.Content as string, text, StringComparison.Ordinal))
        {
            item.Content = text;
        }
    }

    private void _resizeWidthBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || _updating)
        {
            return;
        }

        SchedulePreview();
    }

    private void _resizeHeightBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized || _updating)
        {
            return;
        }

        SchedulePreview();
    }

    /// <summary>
    /// 勾/取消「约束比例」。
    /// <para>
    /// 勾上：高度框变成只读，立刻按宽度回填一个等比的值（用户不必自己算）；
    /// 取消：高度框可编辑，当前值原样保留作起点，用户想压扁/拉长再自己改。
    /// </para>
    /// </summary>
    private void _lockRatioCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updating)
        {
            return;
        }

        UpdateRowState();
        if (_lockRatioCheck.IsChecked == true)
        {
            RefreshPreview();   // 会顺带回填高度框
        }
        else
        {
            SchedulePreview();
        }
    }

    private void _resizeUnitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || _updating || _source == null)
        {
            return;
        }

        // 切换单位时把已填的数字一起换算，避免「600」从像素变毫米后成了 600 毫米。
        var text = (_resizeWidthBox.Text ?? "").Trim();
        var heightText = (_resizeHeightBox.Text ?? "").Trim();
        var dpi = CurrentDpi;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) && value > 0)
        {
            _updating = true;
            try
            {
                _resizeWidthBox.Text = _resizeUnitCombo.SelectedIndex == 1
                    ? ImageProcessor.PixelsToMm(value, dpi).ToString("0.#", CultureInfo.CurrentCulture)
                    : ImageProcessor.MmToPixels(value, dpi).ToString(CultureInfo.InvariantCulture);

                // 约束比例时高度由宽度重算（RefreshPreview 回填），这里只在自由输入模式下同步换算。
                if (_lockRatioCheck.IsChecked != true
                    && double.TryParse(heightText, NumberStyles.Float, CultureInfo.CurrentCulture, out var heightValue)
                    && heightValue > 0)
                {
                    _resizeHeightBox.Text = _resizeUnitCombo.SelectedIndex == 1
                        ? ImageProcessor.PixelsToMm(heightValue, dpi).ToString("0.#", CultureInfo.CurrentCulture)
                        : ImageProcessor.MmToPixels(heightValue, dpi).ToString(CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                _updating = false;
            }
        }

        SchedulePreview();
    }

    /// <summary>
    /// GDI+ 位图 → WPF 图源。
    /// <para>
    /// 走 LockBits 直接构造 <see cref="BitmapSource"/>，不经过 PNG 编码中转：
    /// 中转既慢（每次拖滑块都要编码一次整张图），又会把 32bppArgb 的 alpha 弄丢
    /// ——去底色后的预览曾因此整片透明，什么都看不见。
    /// </para>
    /// </summary>
    private static BitmapSource? ToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        try
        {
            var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var stride = data.Stride;
                var buffer = new byte[stride * bitmap.Height];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                // GDI+ 的 Format32bppArgb 内存序是 BGRA（非预乘），与 PixelFormats.Bgra32 完全对应。
                var source = BitmapSource.Create(
                    bitmap.Width,
                    bitmap.Height,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null,
                    buffer,
                    stride);
                source.Freeze();
                return source;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
    }

    private void DisposeBitmaps()
    {
        _previewTimer.Stop();
        _preview.Source = null;

        _previewBitmap?.Dispose();
        _previewBitmap = null;

        _source?.Dispose();
        _source = null;
    }
}
