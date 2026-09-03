using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>编辑图框库记录时传入的已配置字段与纸张。</summary>
public sealed class FieldBoxSelectInitialState
{
    public LocalRectangle TitleRegion { get; set; } = new();
    public LocalRectangle DrawingNumberRegion { get; set; } = new();
    public LocalRectangle DateRegion { get; set; } = new();
    public LocalRectangle RevisionRegion { get; set; } = new();
    public LocalRectangle PhaseRegion { get; set; } = new();
    public LocalRectangle Info1Region { get; set; } = new();
    public LocalRectangle Info2Region { get; set; } = new();
    public string PaperName { get; set; } = "";
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
}

/// <summary>
/// 新增或编辑图框字段框选对话框。
/// 图名/图号为必选，日期/版次/设计阶段/信息1/信息2可选框选。
/// 每个已框选字段在图中以红色临时矩形框 + 对角线 + 字段名文字标识。
/// </summary>
public sealed partial class FieldBoxSelectDialog : Window, IDisposable
{
    /// <summary>兼容 WinForms Form 的 using/dispose 用法；WPF Window 仅负责关闭。</summary>
    public void Dispose() => Close();

    private readonly Editor _editor;
    private readonly Matrix3d _inverseBlockTransform;
    private readonly Matrix3d _blockTransform;
    private readonly TransientFrameMarkers _markers;

    // 存储每个已选字段的世界坐标角点，用于对话框重新显示时刷新全部临时标识。
    private readonly Dictionary<string, (Point3d Corner1, Point3d Corner2)> _fieldCorners = new(StringComparer.Ordinal);

    // 打印范围（外框）的世界坐标角点，随用户重新框选而更新。
    private (Point3d Corner1, Point3d Corner2) _printAreaCorners;

    /// <summary>打印范围（块局部坐标），对话框关闭后由调用方读取。</summary>
    public LocalRectangle ReferenceFrame { get; set; }

    public LocalRectangle TitleRegion { get; private set; } = new();
    public LocalRectangle DrawingNumberRegion { get; private set; } = new();
    public LocalRectangle DateRegion { get; private set; } = new();
    public LocalRectangle RevisionRegion { get; private set; } = new();
    public LocalRectangle PhaseRegion { get; private set; } = new();
    public LocalRectangle Info1Region { get; private set; } = new();
    public LocalRectangle Info2Region { get; private set; } = new();

    // 纸张设置与矩形框批打共用同一候选识别策略；下拉项直接对应完整纸张结果，
    // 避免名称、物理尺寸和比例被分别修改后彼此不一致。
    private readonly PaperSizeDetector.DetectionOptions _paperDetectionOptions;
    private IReadOnlyList<PaperDetection> _paperOptions = Array.Empty<PaperDetection>();

    private PaperDetection? SelectedPaper =>
        _paperName.SelectedIndex >= 0 && _paperName.SelectedIndex < _paperOptions.Count
            ? _paperOptions[_paperName.SelectedIndex]
            : null;

    public string PaperName => SelectedPaper?.PaperName ?? "";
    public double PaperWidthMm => SelectedPaper?.PaperWidthMm ?? 0d;
    public double PaperHeightMm => SelectedPaper?.PaperHeightMm ?? 0d;

    public FieldBoxSelectDialog(Editor editor, Matrix3d inverseBlockTransform,
        Matrix3d blockTransform, TransientFrameMarkers markers, LocalRectangle referenceFrame,
        IReadOnlyList<PaperDetection> paperOptions,
        PaperSizeDetector.DetectionOptions paperDetectionOptions,
        FieldBoxSelectInitialState? initialState = null)
    {
        InitializeComponent();

        _editor = editor;
        _inverseBlockTransform = inverseBlockTransform;
        _blockTransform = blockTransform;
        _markers = markers;
        _paperDetectionOptions = paperDetectionOptions;
        ReferenceFrame = referenceFrame;

        // 用 4 个局部角点完整变换后取得世界包盒；不能只变换一对对角点，
        // 否则旋转块在窗口重新显示时会把初始红框替换成错误范围。
        var worldFrame = RectangleGeometry.TransformRectangle(referenceFrame, blockTransform);
        _printAreaCorners = (
            new Point3d(worldFrame.MinX, worldFrame.MinY, 0),
            new Point3d(worldFrame.MaxX, worldFrame.MaxY, 0));

        UpdatePrintAreaStatus();

        // 对话框每次重新显示时（例如从 CAD 框选点后返回），刷新所有已选字段的临时红色标识。
        // 对应原 WinForms OnVisibleChanged(Visible) 的刷新职责。
        IsVisibleChanged += (_, args) =>
        {
            if ((bool)args.NewValue)
            {
                RefreshAllMarkers();
            }
        };

        // 图名/图号为必选字段，保存前必须完成框选。
        if (initialState != null)
        {
            ApplyInitialState(initialState);
        }

        // 纸张：默认按打印范围自动识别，重新框选打印范围时同步刷新，也可手动修改。
        ApplyPaperOptions(
            paperOptions,
            initialState?.PaperName,
            initialState?.PaperWidthMm ?? 0d,
            initialState?.PaperHeightMm ?? 0d);
    }

    /// <summary>
    /// CAD 模态窗口建立自己的消息循环并完成首次渲染后再强制重绘一次，
    /// 确保窗口首次出现时红色打印范围已经可见。
    /// 对应原 WinForms OnShown 的刷新职责（IsVisibleChanged 负责从 CAD 框选返回后的刷新，两者职责不同）。
    /// </summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RefreshAllMarkers();
        _markers.RefreshDisplay(regenerate: true);
    }

    /// <summary>
    /// 根据已存储的世界坐标角点，重新绘制所有已选字段及打印范围的临时红色标识。
    /// </summary>
    private void RefreshAllMarkers()
    {
        // 外框标识：不显示文字标签，避免外框过大时文字遮挡图面。
        _markers.SetBox("外框", _printAreaCorners.Corner1, _printAreaCorners.Corner2, null);
        foreach (var kv in _fieldCorners)
        {
            _markers.SetBox(kv.Key, kv.Value.Corner1, kv.Value.Corner2, kv.Key);
        }
    }

    /// <summary>
    /// 重新框选打印范围。用户可在 CAD 中手动框选以修正自动识别的外包框。
    /// </summary>
    private void SelectPrintArea()
    {
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var first = _editor.GetPoint(new PromptPointOptions("\n框选图框打印外边界第一个角点: "));
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var cornerOptions = new PromptCornerOptions("\n框选图框打印外边界对角点: ", first.Value);
            var second = _editor.GetCorner(cornerOptions);
            if (second.Status != PromptStatus.OK)
            {
                return;
            }

            // 更新世界坐标角点（用于外框标记显示）
            _printAreaCorners = (first.Value, second.Value);

            // 转换为块局部坐标，更新 ReferenceFrame
            var p1 = first.Value.TransformBy(_inverseBlockTransform);
            var p2 = second.Value.TransformBy(_inverseBlockTransform);
            ReferenceFrame = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);

            // 立即绘制新的外框临时标识
            _markers.SetBox("外框", first.Value, second.Value, null);
            UpdatePrintAreaStatus();

            // 打印范围变化后重新识别纸张，与原独立纸张界面使用最终外框检测的行为一致。
            // 识别不到标准纸张时要求用户输入绘图比例（对话框内嵌套弹窗，保证置顶）。
            var detectedWidth = Math.Abs(second.Value.X - first.Value.X);
            var detectedHeight = Math.Abs(second.Value.Y - first.Value.Y);
            ApplyPaperOptions(ArbitraryPaperPicker.DetectCandidatesOrPrompt(
                detectedWidth,
                detectedHeight,
                _paperDetectionOptions));
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    /// <summary>
    /// 更新打印范围状态标签，显示当前外框的宽×高（世界坐标单位）。
    /// </summary>
    private void UpdatePrintAreaStatus()
    {
        var w = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
        var h = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
        _printAreaStatus.Text = $"{(int)w} × {(int)h}（点击\"框选\"可修改）";
        _printAreaStatus.Foreground = Brushes.Green;
    }

    private bool ValidateRequiredFields()
    {
        if (!TitleRegion.HasArea() || !DrawingNumberRegion.HasArea())
        {
            System.Windows.MessageBox.Show(this,
                "图名和图号为必选项，请先点击对应\"框选\"按钮完成框选。",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(PaperName))
        {
            System.Windows.MessageBox.Show(this,
                "纸张名称不能为空。",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!ValidateRequiredFields())
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        // 跳过仅留空可选字段，图名/图号仍为必选。
        if (!ValidateRequiredFields())
        {
            return;
        }

        ClearOptionalField("日期", r => DateRegion = r, _dateStatus);
        ClearOptionalField("版次", r => RevisionRegion = r, _revisionStatus);
        ClearOptionalField("设计阶段", r => PhaseRegion = r, _phaseStatus);
        ClearOptionalField("信息1", r => Info1Region = r, _info1Status);
        ClearOptionalField("信息2", r => Info2Region = r, _info2Status);
        DialogResult = true;
        Close();
    }

    private void OnSelectTitleClick(object sender, RoutedEventArgs e) { if (TryBoxSelect("图名", out var r)) { TitleRegion = r; UpdateStatus(_titleStatus, TitleRegion); } }
    private void OnClearTitleClick(object sender, RoutedEventArgs e) { TitleRegion = new LocalRectangle(); _fieldCorners.Remove("图名"); _markers.Remove("图名"); UpdateStatus(_titleStatus, TitleRegion); }

    private void OnSelectNumberClick(object sender, RoutedEventArgs e) { if (TryBoxSelect("图号", out var r)) { DrawingNumberRegion = r; UpdateStatus(_numberStatus, DrawingNumberRegion); } }
    private void OnClearNumberClick(object sender, RoutedEventArgs e) { DrawingNumberRegion = new LocalRectangle(); _fieldCorners.Remove("图号"); _markers.Remove("图号"); UpdateStatus(_numberStatus, DrawingNumberRegion); }

    private void OnSelectDateClick(object sender, RoutedEventArgs e) { if (TryBoxSelect("日期", out var r)) { DateRegion = r; UpdateStatus(_dateStatus, DateRegion); } }
    private void OnClearDateClick(object sender, RoutedEventArgs e) { ClearOptionalField("日期", r => DateRegion = r, _dateStatus); }

    private void OnSelectRevisionClick(object sender, RoutedEventArgs e) { if (TryBoxSelect("版次", out var r)) { RevisionRegion = r; UpdateStatus(_revisionStatus, RevisionRegion); } }
    private void OnClearRevisionClick(object sender, RoutedEventArgs e) { ClearOptionalField("版次", r => RevisionRegion = r, _revisionStatus); }

    private void OnSelectPhaseClick(object sender, RoutedEventArgs e) { if (TryBoxSelect("设计阶段", out var r)) { PhaseRegion = r; UpdateStatus(_phaseStatus, PhaseRegion); } }
    private void OnClearPhaseClick(object sender, RoutedEventArgs e) { ClearOptionalField("设计阶段", r => PhaseRegion = r, _phaseStatus); }

    private void OnSelectInfo1Click(object sender, RoutedEventArgs e) { if (TryBoxSelect("信息1", out var r)) { Info1Region = r; UpdateStatus(_info1Status, Info1Region); } }
    private void OnClearInfo1Click(object sender, RoutedEventArgs e) { ClearOptionalField("信息1", r => Info1Region = r, _info1Status); }

    private void OnSelectInfo2Click(object sender, RoutedEventArgs e) { if (TryBoxSelect("信息2", out var r)) { Info2Region = r; UpdateStatus(_info2Status, Info2Region); } }
    private void OnClearInfo2Click(object sender, RoutedEventArgs e) { ClearOptionalField("信息2", r => Info2Region = r, _info2Status); }

    private void ClearOptionalField(string fieldName, Action<LocalRectangle> setRegion, TextBlock statusText)
    {
        setRegion(new LocalRectangle());
        _fieldCorners.Remove(fieldName);
        _markers.Remove(fieldName);
        UpdateStatus(statusText, new LocalRectangle());
    }

    private bool TryBoxSelect(string fieldName, out LocalRectangle region)
    {
        region = new LocalRectangle();
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var firstPrompt = $"框选{fieldName}区域第一个角点，或右键跳过: ";
            var secondPrompt = $"框选{fieldName}区域对角点: ";

            var first = _editor.GetPoint(new PromptPointOptions(firstPrompt));
            if (first.Status != PromptStatus.OK)
            {
                return false;
            }

            var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
            var second = _editor.GetCorner(cornerOptions);
            if (second.Status != PromptStatus.OK)
            {
                return false;
            }

            var p1 = first.Value.TransformBy(_inverseBlockTransform);
            var p2 = second.Value.TransformBy(_inverseBlockTransform);
            region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);

            // 存储世界坐标角点，用于对话框重新显示时刷新临时标识。
            _fieldCorners[fieldName] = (first.Value, second.Value);

            // 世界坐标绘制红色临时标识（红色矩形框 + 对角线 + 居中字段名文字），
            // 重新框选时自动替换旧标识。
            _markers.SetBox(fieldName, first.Value, second.Value, fieldName);
            return true;
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private static void UpdateStatus(TextBlock label, LocalRectangle region)
    {
        if (region.HasArea())
        {
            label.Text = $"已选择 ({region.MinX:0.###},{region.MinY:0.###})-({region.MaxX:0.###},{region.MaxY:0.###})";
            label.Foreground = Brushes.Green;
        }
        else
        {
            label.Text = "(未选择)";
            label.Foreground = Brushes.DimGray;
        }
    }

    private void ApplyInitialState(FieldBoxSelectInitialState state)
    {
        TitleRegion = state.TitleRegion;
        DrawingNumberRegion = state.DrawingNumberRegion;
        DateRegion = state.DateRegion;
        RevisionRegion = state.RevisionRegion;
        PhaseRegion = state.PhaseRegion;
        Info1Region = state.Info1Region;
        Info2Region = state.Info2Region;

        AddInitialField("图名", TitleRegion, _titleStatus);
        AddInitialField("图号", DrawingNumberRegion, _numberStatus);
        AddInitialField("日期", DateRegion, _dateStatus);
        AddInitialField("版次", RevisionRegion, _revisionStatus);
        AddInitialField("设计阶段", PhaseRegion, _phaseStatus);
        AddInitialField("信息1", Info1Region, _info1Status);
        AddInitialField("信息2", Info2Region, _info2Status);
    }

    private void AddInitialField(string fieldName, LocalRectangle region, TextBlock statusText)
    {
        UpdateStatus(statusText, region);
        if (!region.HasArea())
        {
            return;
        }

        // 局部字段坐标变换为世界坐标后建立首次显示的红色临时框。
        var worldRegion = RectangleGeometry.TransformRectangle(region, _blockTransform);
        _fieldCorners[fieldName] = (
            new Point3d(worldRegion.MinX, worldRegion.MinY, 0),
            new Point3d(worldRegion.MaxX, worldRegion.MaxY, 0));
    }

    /// <summary>
    /// 主动指定任意纸张：按当前打印范围宽高弹出比例对话框。
    /// 长宽比接近标准图幅或 1/8 模数加长图时可选择目标图幅（任意比例）；否则按输入比例生成自定义纸张。
    /// 生成的候选加入下拉并选中（同名同尺寸项去重替换）。
    /// </summary>
    private void PickArbitraryPaper()
    {
        var width = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
        var height = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
        if (width <= 1e-6 || height <= 1e-6)
        {
            return;
        }

        var guessedScale = PaperSizeDetector.GuessScale(width, height);
        var scaleForm = new CustomScaleForm(
            width,
            height,
            guessedScale,
            ArbitraryPaperPicker.HintText,
            allowAspectRatioPapers: !_paperDetectionOptions.IncludeGenericDynamicTitleBlockPaper);
        if (CadDialog.ShowModal(scaleForm) != true)
        {
            return;
        }

        var arbitrary = ArbitraryPaperPicker.CreatePaperFromScaleForm(scaleForm, width, height);

        // 同名同尺寸项去重替换，避免重复添加。
        var options = _paperOptions.ToList();
        var existingIndex = options.FindIndex(x =>
            string.Equals(x.PaperName, arbitrary.PaperName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(x.PaperWidthMm - arbitrary.PaperWidthMm) <= 0.01d
            && Math.Abs(x.PaperHeightMm - arbitrary.PaperHeightMm) <= 0.01d);
        if (existingIndex >= 0)
        {
            options[existingIndex] = arbitrary;
        }
        else
        {
            options.Add(arbitrary);
        }

        ApplyPaperOptions(options, arbitrary.PaperName, arbitrary.PaperWidthMm, arbitrary.PaperHeightMm);
    }

    private void OnSelectPrintAreaClick(object sender, RoutedEventArgs e) => SelectPrintArea();

    private void OnPickArbitraryPaperClick(object sender, RoutedEventArgs e) => PickArbitraryPaper();

    private void ApplyPaperOptions(
        IReadOnlyList<PaperDetection> paperOptions,
        string? preferredPaperName = null,
        double preferredPaperWidthMm = 0d,
        double preferredPaperHeightMm = 0d)
    {
        if (paperOptions.Count == 0)
        {
            throw new ArgumentException("至少需要一个纸张候选项。", nameof(paperOptions));
        }

        _paperOptions = paperOptions;
        _paperName.Items.Clear();
        foreach (var paper in _paperOptions)
        {
            _paperName.Items.Add(PaperSizeDetector.FormatOption(paper));
        }

        var preferredIndex = -1;
        if (!string.IsNullOrWhiteSpace(preferredPaperName))
        {
            for (var i = 0; i < _paperOptions.Count; i++)
            {
                var paper = _paperOptions[i];
                if (string.Equals(paper.PaperName, preferredPaperName, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(paper.PaperWidthMm - preferredPaperWidthMm) <= 0.01d
                    && Math.Abs(paper.PaperHeightMm - preferredPaperHeightMm) <= 0.01d)
                {
                    preferredIndex = i;
                    break;
                }
            }
        }

        _paperName.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
    }
}
