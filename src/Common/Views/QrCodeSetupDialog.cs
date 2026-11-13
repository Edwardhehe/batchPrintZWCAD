using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

// CadUsings.cs 的全局 using 让 CAD 命名空间里的同名类型也进了作用域，这里钉死本文件要的那个：
// WinForms 布局的流向枚举。
using FlowDirection = System.Windows.Forms.FlowDirection;

namespace ZwcadBatchPlot;

/// <summary>
/// 图框二维码设置：每个位置一个「框选」按钮，在图上框出二维码要贴的方格。
/// <para>
/// 与签章设置分开成独立窗口，而不是塞进 9 个槽位的表格里：二维码的语义是「内容模板 + 一个正方形位置」，
/// 槽位的语义是「绑定签名 + 可多位置」，两者的编辑动作没有交集，混在一起会让两边都变难懂。
/// </para>
/// <para>
/// 框选结果按**正方形**存：二维码拉伸成矩形就扫不出，所以框完立即以原框中心取较小边归一化，
/// 让红框显示的就是最终贴图范围，不用用户自己对齐。
/// </para>
/// </summary>
public sealed class QrCodeSetupDialog : Form
{
    private readonly Editor _editor;
    private readonly Matrix3d _inverseBlockTransform;
    private readonly Matrix3d _blockTransform;
    private readonly TransientFrameMarkers _markers;
    private readonly List<TitleBlockQrCode> _codes;

    private readonly ListBox _list = new();
    private readonly TextBox _nameBox = new();
    private readonly TextBox _templateBox = new();
    private readonly ComboBox _eccCombo = new();
    private readonly NumericUpDown _rotationBox = new();
    private readonly Label _statusLabel = new();

    /// <summary>把控件值回写到数据时置位，挡住 TextChanged / SelectedIndexChanged 的相互触发。</summary>
    private bool _updating;

    /// <summary>打印范围（块局部坐标），作为坐标换算基准。</summary>
    public LocalRectangle ReferenceFrame { get; }

    public double PaperWidthMm { get; }

    public double PaperHeightMm { get; }

    /// <summary>
    /// 确定后的位置列表。没框位置的条目直接丢弃——库里存一条「有模板没位置」的记录，
    /// 打印时只会每次都写一行「尺寸无效」的日志，不如不存。
    /// </summary>
    public List<TitleBlockQrCode> Codes =>
        _codes.Where(code => code.IsConfigured()).ToList();

    public QrCodeSetupDialog(
        Editor editor,
        Matrix3d inverseBlockTransform,
        Matrix3d blockTransform,
        TransientFrameMarkers markers,
        LocalRectangle referenceFrame,
        double paperWidthMm,
        double paperHeightMm,
        IEnumerable<TitleBlockQrCode>? existing)
    {
        _editor = editor;
        _inverseBlockTransform = inverseBlockTransform;
        _blockTransform = blockTransform;
        _markers = markers;
        ReferenceFrame = referenceFrame;
        PaperWidthMm = paperWidthMm;
        PaperHeightMm = paperHeightMm;

        // 按副本编辑：取消时不能把库里已配好的位置改脏。
        _codes = (existing ?? Enumerable.Empty<TitleBlockQrCode>())
            .Where(code => code != null)
            .Select(CloneCode)
            .ToList();

        foreach (var code in _codes)
        {
            ApplyFrameScale(code, ReferenceFrame, PaperWidthMm, PaperHeightMm);
        }

        Text = "设置二维码";
        UiLayout.ConfigureForm(this, 660, 460, 620, 430);
        ClientSize = new Size(UiLayout.Scale(660), UiLayout.Scale(460));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        // 与父对话框一致：核对红框时不挡 CAD 缩放/平移。
        TopMost = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(10), UiLayout.Scale(12), UiLayout.Scale(10)),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(BuildListArea(), 0, 0);
        root.Controls.Add(BuildFieldArea(), 0, 1);
        root.Controls.Add(BuildStatusArea(), 0, 2);
        root.Controls.Add(BuildButtonArea(), 0, 3);

        Controls.Add(root);

        ReloadList();
        // 打开时按当前列表重画，空列表也要清掉上次留下的「二维码:」红框。
        RedrawMarkers();
    }

    // ---------------- 界面 ----------------

    private Control BuildListArea()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _list.Dock = DockStyle.Fill;
        _list.IntegralHeight = false;
        _list.Font = new Font(_list.Font.FontFamily, _list.Font.Size * 1.05f);
        _list.SelectedIndexChanged += (_, _) => LoadSelectedIntoControls();
        panel.Controls.Add(_list, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(UiLayout.Scale(8), 0, 0, 0),
        };

        var add = UiLayout.CreateButton("新增", 72);
        add.Click += (_, _) => AddCode();
        buttons.Controls.Add(add);

        var remove = UiLayout.CreateButton("删除", 72);
        remove.Click += (_, _) => RemoveSelected();
        buttons.Controls.Add(remove);

        var pick = UiLayout.CreateButton("框选位置", 72);
        pick.Click += (_, _) => PickRegion();
        buttons.Controls.Add(pick);

        panel.Controls.Add(buttons, 1, 0);
        return panel;
    }

    private Control BuildFieldArea()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, UiLayout.Scale(10), 0, 0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(72)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        table.Controls.Add(MakeLabel("名称"), 0, 0);
        _nameBox.Dock = DockStyle.Fill;
        _nameBox.TextChanged += (_, _) => WriteBackSelected();
        table.Controls.Add(_nameBox, 1, 0);

        table.Controls.Add(MakeLabel("内容模板"), 0, 1);
        _templateBox.Dock = DockStyle.Fill;
        _templateBox.TextChanged += (_, _) => WriteBackSelected();
        table.Controls.Add(_templateBox, 1, 1);

        table.Controls.Add(MakeLabel("纠错等级"), 0, 2);
        _eccCombo.Dock = DockStyle.Left;
        _eccCombo.Width = UiLayout.Scale(120);
        _eccCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _eccCombo.Items.AddRange(new object[] { "M（15%，推荐）", "L（7%）", "Q（25%）", "H（30%）" });
        _eccCombo.SelectedIndexChanged += (_, _) => WriteBackSelected();
        table.Controls.Add(_eccCombo, 1, 2);

        table.Controls.Add(MakeLabel("旋转"), 0, 3);
        _rotationBox.Dock = DockStyle.Left;
        _rotationBox.Width = UiLayout.Scale(120);
        _rotationBox.Minimum = -180m;
        _rotationBox.Maximum = 180m;
        _rotationBox.DecimalPlaces = 1;
        _rotationBox.Increment = 90m;
        _rotationBox.ValueChanged += (_, _) => WriteBackSelected();
        table.Controls.Add(_rotationBox, 1, 3);

        var hint = MakeLabel(
            "可用占位符：{图号} {图名} {版本} {日期} {阶段} {文件名} {布局} {块名}\n"
            + "例：{图号}-{版本}。写错的花括号会原样打进二维码，便于发现。");
        hint.ForeColor = SystemColors.GrayText;
        hint.AutoSize = true;
        hint.Margin = new Padding(0, UiLayout.Scale(6), 0, 0);
        table.Controls.Add(hint, 0, 4);
        table.SetColumnSpan(hint, 2);

        return table;
    }

    private Control BuildStatusArea()
    {
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(0, UiLayout.Scale(8), 0, 0);
        _statusLabel.Text = "";
        return _statusLabel;
    }

    private Control BuildButtonArea()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, UiLayout.Scale(10), 0, 0),
        };

        var cancel = UiLayout.CreateButton("取消", 88);
        cancel.DialogResult = DialogResult.Cancel;
        panel.Controls.Add(cancel);

        var ok = UiLayout.CreateButton("确定", 88);
        ok.Click += (_, _) => Accept();
        panel.Controls.Add(ok);

        return panel;
    }

    // ---------------- 数据编辑 ----------------

    private void AddCode()
    {
        var code = new TitleBlockQrCode
        {
            Name = NextDefaultName(),
            ContentTemplate = "{图号}",
            ErrorCorrection = "M",
        };
        _codes.Add(code);
        ReloadList();
        _list.SelectedIndex = _list.Items.Count - 1;
    }

    private string NextDefaultName()
    {
        for (var index = 1; index < 1000; index++)
        {
            var candidate = "二维码" + index;
            if (_codes.All(code => !string.Equals(code.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return "二维码";
    }

    private void RemoveSelected()
    {
        var index = _list.SelectedIndex;
        if (index < 0 || index >= _codes.Count)
        {
            return;
        }

        var name = _codes[index].Name;
        _codes.RemoveAt(index);
        ReloadList();
        _markers.RemoveByPrefix(MarkerPrefix(name));
        RedrawMarkers();
        UpdateStatus();
    }

    private void ReloadList()
    {
        _updating = true;
        try
        {
            var keep = _list.SelectedIndex;
            _list.Items.Clear();
            foreach (var code in _codes)
            {
                _list.Items.Add(Describe(code));
            }

            if (_list.Items.Count > 0)
            {
                _list.SelectedIndex = Math.Min(Math.Max(keep, 0), _list.Items.Count - 1);
            }
        }
        finally
        {
            _updating = false;
        }

        LoadSelectedIntoControls();
    }

    private static string Describe(TitleBlockQrCode code)
    {
        var name = string.IsNullOrWhiteSpace(code.Name) ? "(未命名)" : code.Name;
        var size = code.IsConfigured()
            ? $"{code.WidthMm:0.#}×{code.HeightMm:0.#}mm"
            : "未框位置";
        var template = string.IsNullOrWhiteSpace(code.ContentTemplate) ? "(空模板)" : code.ContentTemplate;
        return $"{name}    {size}    模板：{template}";
    }

    private void LoadSelectedIntoControls()
    {
        if (_updating)
        {
            return;
        }

        var code = SelectedCode();
        _updating = true;
        try
        {
            var enabled = code != null;
            _nameBox.Enabled = enabled;
            _templateBox.Enabled = enabled;
            _eccCombo.Enabled = enabled;
            _rotationBox.Enabled = enabled;

            if (code == null)
            {
                _nameBox.Text = "";
                _templateBox.Text = "";
                _eccCombo.SelectedIndex = 0;
                _rotationBox.Value = 0m;
            }
            else
            {
                _nameBox.Text = code.Name;
                _templateBox.Text = code.ContentTemplate;
                _eccCombo.SelectedIndex = EccIndex(code.ErrorCorrection);
                _rotationBox.Value = ClampRotation(code.Rotation);
            }
        }
        finally
        {
            _updating = false;
        }

        UpdateStatus();
    }

    private void WriteBackSelected()
    {
        if (_updating)
        {
            return;
        }

        var code = SelectedCode();
        if (code == null)
        {
            return;
        }

        var oldName = code.Name;
        code.Name = _nameBox.Text.Trim();
        code.ContentTemplate = _templateBox.Text;
        code.ErrorCorrection = EccValue(_eccCombo.SelectedIndex);
        code.Rotation = (double)_rotationBox.Value;

        // 列表里那行文字要跟着变；但重刷列表会打断正在输入的光标，所以只更新当前项文本。
        var index = _list.SelectedIndex;
        if (index >= 0 && index < _list.Items.Count)
        {
            _updating = true;
            try
            {
                _list.Items[index] = Describe(code);
            }
            finally
            {
                _updating = false;
            }
        }

        if (!string.Equals(oldName, code.Name, StringComparison.Ordinal))
        {
            // 改名要重画标记：标记键里带名称。
            _markers.RemoveByPrefix(MarkerPrefix(oldName));
            RedrawMarkers();
        }

        UpdateStatus();
    }

    private TitleBlockQrCode? SelectedCode()
    {
        var index = _list.SelectedIndex;
        return index >= 0 && index < _codes.Count ? _codes[index] : null;
    }

    /// <summary>
    /// 框选二维码位置：<c>GetPoint → GetCorner</c> 取一个矩形，
    /// 随即以中心为基准归一化为正方形（边长取较小边）——矩形二维码扫不出，不如框完就改好。
    /// </summary>
    private void PickRegion()
    {
        var code = SelectedCode();
        if (code == null)
        {
            MessageBox.Show(this, "请先新增或选中一个二维码位置。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!ReferenceFrame.HasArea() || PaperWidthMm <= 0d || PaperHeightMm <= 0d)
        {
            MessageBox.Show(this,
                "还没有确定打印范围或纸张。\n请先关闭本窗口，在上一个对话框里选好打印范围与纸张。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var name = string.IsNullOrWhiteSpace(code.Name) ? "二维码" : code.Name;
        var picked = false;

        BeginCadPick();
        try
        {
            var first = _editor.GetPoint(new PromptPointOptions(
                $"\n框选「{name}」区域第一个角点（将按正方形取较小边）: "));
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var second = _editor.GetCorner(new PromptCornerOptions(
                $"\n框选「{name}」区域对角点: ", first.Value));
            if (second.Status != PromptStatus.OK)
            {
                return;
            }

            var p1 = first.Value.TransformBy(_inverseBlockTransform);
            var p2 = second.Value.TransformBy(_inverseBlockTransform);
            var local = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
            if (!local.HasArea())
            {
                return;
            }

            code.CadRegion = ToSquareFrameRelative(local);
            ApplyFrameScale(code, ReferenceFrame, PaperWidthMm, PaperHeightMm);
            picked = true;
        }
        finally
        {
            EndCadPick();
        }

        if (!picked)
        {
            return;
        }

        RedrawMarkers();
        UpdateStatus();
        ReloadCurrentItemText();

        PluginLog.Timing(
            $"[二维码] 位置「{code.Name}」本次框选："
            + $"距右 {code.RightInsetMm:F1} 距下 {code.BottomInsetMm:F1} "
            + $"{code.WidthMm:F1}×{code.HeightMm:F1}mm");
    }

    /// <summary>
    /// 把块内绝对矩形转成「相对打印范围右下角」的正方形。
    /// 原点语义与签章槽位一致：X 向右为正、Y 向上为正，所以直接减去 (MaxX, MinY)。
    /// </summary>
    private LocalRectangle ToSquareFrameRelative(LocalRectangle local)
    {
        var side = Math.Min(local.MaxX - local.MinX, local.MaxY - local.MinY);
        if (side <= 0d)
        {
            return new LocalRectangle();
        }

        var centerX = (local.MinX + local.MaxX) / 2d;
        var centerY = (local.MinY + local.MaxY) / 2d;
        var square = new LocalRectangle
        {
            MinX = centerX - side / 2d,
            MaxX = centerX + side / 2d,
            MinY = centerY - side / 2d,
            MaxY = centerY + side / 2d,
        };

        return LocalRectangle.FromPoints(
            square.MinX - ReferenceFrame.MaxX,
            square.MinY - ReferenceFrame.MinY,
            square.MaxX - ReferenceFrame.MaxX,
            square.MaxY - ReferenceFrame.MinY);
    }

    private void Accept()
    {
        foreach (var code in _codes)
        {
            if (string.IsNullOrWhiteSpace(code.Name))
            {
                MessageBox.Show(this, "请给每个二维码位置起一个名称。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(code.ContentTemplate))
            {
                MessageBox.Show(this,
                    $"位置「{code.Name}」的内容模板为空，出图时不会生成二维码。\n请填写模板，或删掉该位置。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!code.IsConfigured())
            {
                MessageBox.Show(this,
                    $"位置「{code.Name}」尚未框选位置，出图时不会生成二维码。\n请先框选，或删掉该位置。",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    // ---------------- 状态与标记 ----------------

    private void UpdateStatus()
    {
        var code = SelectedCode();
        if (code == null)
        {
            _statusLabel.Text = _codes.Count == 0
                ? "还没有二维码位置。点「新增」再点「框选位置」在图签上框一个方格。"
                : "未选中条目。";
            return;
        }

        if (!code.IsConfigured())
        {
            _statusLabel.Text = $"「{code.Name}」尚未框选位置，出图时不会生成。";
            return;
        }

        var side = Math.Min(code.WidthMm, code.HeightMm);
        var hint = side < 20d
            ? "  ← 小于 20mm，打印后可能扫不出，建议放大"
            : "";
        _statusLabel.Text =
            $"「{code.Name}」距纸张右边缘 {code.RightInsetMm:F1}mm、下边缘 {code.BottomInsetMm:F1}mm，"
            + $"边长 {side:F1}mm{hint}";
    }

    private void ReloadCurrentItemText()
    {
        var index = _list.SelectedIndex;
        if (index < 0 || index >= _codes.Count || index >= _list.Items.Count)
        {
            return;
        }

        _updating = true;
        try
        {
            _list.Items[index] = Describe(_codes[index]);
        }
        finally
        {
            _updating = false;
        }
    }

    private void RedrawMarkers(bool refresh = true)
    {
        DrawMarkers(_markers, _codes, ReferenceFrame, _blockTransform, refresh);
    }

    private void BeginCadPick() => SignatureSlotSetupDialog.BeginModelessCadPick(this);

    private void EndCadPick() => SignatureSlotSetupDialog.EndModelessCadPick(this);

    // ---------------- 静态换算（父对话框也调用） ----------------

    /// <summary>标记键前缀。与签章的「签章:」区分开，两者互不清除对方。</summary>
    public static string MarkerPrefix(string name) => "二维码:" + name;

    /// <summary>
    /// 按打印范围与纸张把块内相对矩形换算成纸张毫米偏移。
    /// 与签章槽位同一套公式：块内 X 向右为正、Y 向上为正，原点在打印范围右下角。
    /// </summary>
    public static void ApplyFrameScale(
        TitleBlockQrCode code,
        LocalRectangle frame,
        double paperWidthMm,
        double paperHeightMm)
    {
        if (code == null || !frame.HasArea() || paperWidthMm <= 0d || paperHeightMm <= 0d)
        {
            return;
        }

        var region = code.CadRegion;
        if (region == null || !region.HasArea())
        {
            return;
        }

        var scaleX = paperWidthMm / (frame.MaxX - frame.MinX);
        var scaleY = paperHeightMm / (frame.MaxY - frame.MinY);

        code.WidthMm = (region.MaxX - region.MinX) * scaleX;
        code.HeightMm = (region.MaxY - region.MinY) * scaleY;
        code.RightInsetMm = -region.MaxX * scaleX;
        code.BottomInsetMm = region.MinY * scaleY;
    }

    /// <summary>按位置列表重画红框（父对话框在打印范围/纸张变化后也用它刷新）。</summary>
    public static void DrawMarkers(
        TransientFrameMarkers markers,
        IEnumerable<TitleBlockQrCode> codes,
        LocalRectangle frame,
        Matrix3d blockTransform,
        bool refresh = true)
    {
        if (markers == null)
        {
            return;
        }

        // 必须按统一前缀全部清掉：取消、重开或列表变空时，只按当前条目名删除会留下旧红框。
        markers.RemoveByPrefix(MarkerPrefix(""), refresh: false);

        if (frame.HasArea())
        {
            foreach (var code in codes ?? Enumerable.Empty<TitleBlockQrCode>())
            {
                if (code?.CadRegion == null || !code.CadRegion.HasArea())
                {
                    continue;
                }

                var first = new Point3d(
                    frame.MaxX + code.CadRegion.MinX,
                    frame.MinY + code.CadRegion.MinY,
                    0).TransformBy(blockTransform);
                var second = new Point3d(
                    frame.MaxX + code.CadRegion.MaxX,
                    frame.MinY + code.CadRegion.MaxY,
                    0).TransformBy(blockTransform);
                var label = string.IsNullOrWhiteSpace(code.Name) ? "二维码" : code.Name;
                markers.SetBox(MarkerPrefix(code.Name), first, second, label, refresh: false);
            }
        }

        if (refresh)
        {
            markers.RefreshDisplay();
        }
    }

    public static TitleBlockQrCode CloneCode(TitleBlockQrCode source)
    {
        var region = source.CadRegion;
        return new TitleBlockQrCode
        {
            Name = source.Name,
            Enabled = source.Enabled,
            ContentTemplate = source.ContentTemplate,
            CadRegion = new LocalRectangle
            {
                MinX = region?.MinX ?? 0d,
                MinY = region?.MinY ?? 0d,
                MaxX = region?.MaxX ?? 0d,
                MaxY = region?.MaxY ?? 0d,
                ActualWidth = region?.ActualWidth ?? 0d,
                ActualHeight = region?.ActualHeight ?? 0d,
                CornerPoints = region?.CornerPoints,
            },
            RightInsetMm = source.RightInsetMm,
            BottomInsetMm = source.BottomInsetMm,
            WidthMm = source.WidthMm,
            HeightMm = source.HeightMm,
            Rotation = source.Rotation,
            ErrorCorrection = source.ErrorCorrection,
        };
    }

    /// <summary>非模态压在 CAD 之上等待关闭，与签章设置共用同一套等待策略。</summary>
    public static DialogResult ShowOverCad(Form form) => SignatureSlotSetupDialog.ShowOverCad(form);

    // ---------------- 小工具 ----------------

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, UiLayout.Scale(6), UiLayout.Scale(8), UiLayout.Scale(6)),
    };

    private static int EccIndex(string? value) => (value ?? "").Trim().ToUpperInvariant() switch
    {
        "L" => 1,
        "Q" => 2,
        "H" => 3,
        _ => 0,
    };

    private static string EccValue(int index) => index switch
    {
        1 => "L",
        2 => "Q",
        3 => "H",
        _ => "M",
    };

    private static decimal ClampRotation(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0m;
        }

        return (decimal)Math.Max(-180d, Math.Min(180d, value));
    }
}
