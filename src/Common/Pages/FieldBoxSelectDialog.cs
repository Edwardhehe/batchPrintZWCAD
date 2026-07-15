using System;
using System.Drawing;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 新增图框可选字段框选对话框。
/// 图名/图号为已选（不可操作），日期/版次/设计阶段/信息1/信息2可选框选。
/// </summary>
public sealed class FieldBoxSelectDialog : Form
{
    private readonly Editor _editor;
    private readonly Matrix3d _inverseBlockTransform;

    private readonly Label _dateStatus;
    private readonly Label _revisionStatus;
    private readonly Label _phaseStatus;
    private readonly Label _info1Status;
    private readonly Label _info2Status;

    public LocalRectangle DateRegion { get; private set; } = new();
    public LocalRectangle RevisionRegion { get; private set; } = new();
    public LocalRectangle PhaseRegion { get; private set; } = new();
    public LocalRectangle Info1Region { get; private set; } = new();
    public LocalRectangle Info2Region { get; private set; } = new();

    public FieldBoxSelectDialog(Editor editor, Matrix3d inverseBlockTransform)
    {
        _editor = editor;
        _inverseBlockTransform = inverseBlockTransform;

        Text = "选择图框可选字段";
        UiLayout.ConfigureForm(this, 460, 360, 430, 335);
        // 字段状态可能较长，横向保留足够宽度，纵向仅按实际行数配置。
        ClientSize = new Size(UiLayout.Scale(460), UiLayout.Scale(340));
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(8), UiLayout.Scale(12), UiLayout.Scale(8))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Row 0-1: 必选字段（灰色已选）
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        // Row 2: 分隔
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(6)));
        // Row 3-7: 可选字段
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        // Row 8: 提示
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // Row 9: 按钮
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(36)));

        // 图名
        table.Controls.Add(MakeLabel("图名"), 0, 0);
        table.Controls.Add(MakeSelectedLabel(), 1, 0);
        // 图号
        table.Controls.Add(MakeLabel("图号"), 0, 1);
        table.Controls.Add(MakeSelectedLabel(), 1, 1);

        // 分隔
        var sep = new Label { Text = "", Dock = DockStyle.Fill, Height = UiLayout.Scale(8) };
        table.SetColumnSpan(sep, 2);
        table.Controls.Add(sep, 0, 2);

        // 日期
        table.Controls.Add(MakeLabel("日期"), 0, 3);
        _dateStatus = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_dateStatus, SelectDate, ClearDate), 1, 3);

        // 版次
        table.Controls.Add(MakeLabel("版次"), 0, 4);
        _revisionStatus = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_revisionStatus, SelectRevision, ClearRevision), 1, 4);

        // 设计阶段
        table.Controls.Add(MakeLabel("设计阶段"), 0, 5);
        _phaseStatus = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_phaseStatus, SelectPhase, ClearPhase), 1, 5);

        // 信息1/信息2为用户自定义可选字段，可用于后续文件名命名。
        table.Controls.Add(MakeLabel("信息1"), 0, 6);
        _info1Status = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_info1Status, SelectInfo1, ClearInfo1), 1, 6);

        table.Controls.Add(MakeLabel("信息2"), 0, 7);
        _info2Status = MakeStatusLabel();
        table.Controls.Add(MakeFieldRow(_info2Status, SelectInfo2, ClearInfo2), 1, 7);

        // 提示
        var hint = new Label
        {
            Text = "点击\"框选\"在 CAD 中框选对应区域，或点\"跳过\"全部留空",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Font = new Font(Font.FontFamily, Math.Max(Font.Size - 1, 7))
        };
        table.SetColumnSpan(hint, 2);
        table.Controls.Add(hint, 0, 8);

        // 按钮
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, UiLayout.Scale(4), 0, 0)
        };
        var ok = UiLayout.CreateButton("确定", 76);
        var skip = UiLayout.CreateButton("跳过", 76);
        var cancel = UiLayout.CreateButton("取消", 76);
        ok.DialogResult = DialogResult.OK;
        skip.Click += (_, _) =>
        {
            DateRegion = new LocalRectangle();
            RevisionRegion = new LocalRectangle();
            PhaseRegion = new LocalRectangle();
            Info1Region = new LocalRectangle();
            Info2Region = new LocalRectangle();
            DialogResult = DialogResult.OK;
            Close();
        };
        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(skip);
        buttons.Controls.Add(cancel);
        table.SetColumnSpan(buttons, 2);
        table.Controls.Add(buttons, 0, 9);

        Controls.Add(table);
    }

    private void SelectDate() { if (TryBoxSelect("日期", out var r)) { DateRegion = r; UpdateStatus(_dateStatus, DateRegion); } }
    private void ClearDate() { DateRegion = new LocalRectangle(); UpdateStatus(_dateStatus, DateRegion); }

    private void SelectRevision() { if (TryBoxSelect("版次", out var r)) { RevisionRegion = r; UpdateStatus(_revisionStatus, RevisionRegion); } }
    private void ClearRevision() { RevisionRegion = new LocalRectangle(); UpdateStatus(_revisionStatus, RevisionRegion); }

    private void SelectPhase() { if (TryBoxSelect("设计阶段", out var r)) { PhaseRegion = r; UpdateStatus(_phaseStatus, PhaseRegion); } }
    private void ClearPhase() { PhaseRegion = new LocalRectangle(); UpdateStatus(_phaseStatus, PhaseRegion); }

    private void SelectInfo1() { if (TryBoxSelect("信息1", out var r)) { Info1Region = r; UpdateStatus(_info1Status, Info1Region); } }
    private void ClearInfo1() { Info1Region = new LocalRectangle(); UpdateStatus(_info1Status, Info1Region); }

    private void SelectInfo2() { if (TryBoxSelect("信息2", out var r)) { Info2Region = r; UpdateStatus(_info2Status, Info2Region); } }
    private void ClearInfo2() { Info2Region = new LocalRectangle(); UpdateStatus(_info2Status, Info2Region); }

    private bool TryBoxSelect(string fieldName, out LocalRectangle region)
    {
        region = new LocalRectangle();
        Visible = false;
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
            return true;
        }
        finally
        {
            Visible = true;
        }
    }

    private static void UpdateStatus(Label label, LocalRectangle region)
    {
        if (region.HasArea())
        {
            label.Text = $"已选择 ({region.MinX:0.###},{region.MinY:0.###})-({region.MaxX:0.###},{region.MaxY:0.###})";
            label.ForeColor = Color.Green;
        }
        else
        {
            label.Text = "(未选择)";
            label.ForeColor = Color.DimGray;
        }
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = true
    };

    private static Label MakeSelectedLabel() => new()
    {
        Text = "已选择",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.Gray
    };

    private static Label MakeStatusLabel() => new()
    {
        Text = "(未选择)",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DimGray,
        AutoEllipsis = true
    };

    private static Control MakeFieldRow(Label statusLabel, Action select, Action clear)
    {
        // 高 DPI 下状态文字和按钮都会放大，用表格布局让状态列自动收缩，避免右侧“清除”按钮被裁切。
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("框选", 60) + UiLayout.Scale(8)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("清除", 60) + UiLayout.Scale(2)));

        var selectBtn = UiLayout.CreateButton("框选", 60);
        selectBtn.Dock = DockStyle.Fill;
        selectBtn.Click += (_, _) => select();

        var clearBtn = UiLayout.CreateButton("清除", 60);
        clearBtn.Dock = DockStyle.Fill;
        clearBtn.Click += (_, _) => clear();

        panel.Controls.Add(statusLabel, 0, 0);
        panel.Controls.Add(selectBtn, 1, 0);
        panel.Controls.Add(clearBtn, 2, 0);

        return panel;
    }
}
