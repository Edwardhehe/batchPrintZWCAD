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
/// 图名/图号为已选（不可操作），日期/版次/设计阶段可选框选。
/// </summary>
public sealed class FieldBoxSelectDialog : Form
{
    private readonly Editor _editor;
    private readonly Matrix3d _inverseBlockTransform;

    private readonly Label _dateStatus;
    private readonly Label _revisionStatus;
    private readonly Label _phaseStatus;

    public LocalRectangle DateRegion { get; private set; } = new();
    public LocalRectangle RevisionRegion { get; private set; } = new();
    public LocalRectangle PhaseRegion { get; private set; } = new();

    public FieldBoxSelectDialog(Editor editor, Matrix3d inverseBlockTransform)
    {
        _editor = editor;
        _inverseBlockTransform = inverseBlockTransform;

        Text = "选择图框可选字段";
        UiLayout.ConfigureForm(this, 500, 360, 460, 320);
        // 新增图框流程会在 CAD 内弹窗，按 DPI 配置窗口后再给足内容区，避免高分屏按钮/状态文字挤压。
        ClientSize = new Size(UiLayout.Scale(500), UiLayout.Scale(340));
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(UiLayout.Scale(16), UiLayout.Scale(12), UiLayout.Scale(16), UiLayout.Scale(12))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Row 0-1: 必选字段（灰色已选）
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(36)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(36)));
        // Row 2: 分隔
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(10)));
        // Row 3-5: 可选字段
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        // Row 6: 提示
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // Row 7: 按钮
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(44)));

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

        // 提示
        var hint = new Label
        {
            Text = "点击\"框选\"在 CAD 中框选对应区域，或点\"跳过\"全部留空",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Font = new Font(Font.FontFamily, Math.Max(Font.Size - 1, 8))
        };
        table.SetColumnSpan(hint, 2);
        table.Controls.Add(hint, 0, 6);

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
        table.Controls.Add(buttons, 0, 7);

        Controls.Add(table);
    }

    private void SelectDate() { if (TryBoxSelect("日期", out var r)) { DateRegion = r; UpdateStatus(_dateStatus, DateRegion); } }
    private void ClearDate() { DateRegion = new LocalRectangle(); UpdateStatus(_dateStatus, DateRegion); }

    private void SelectRevision() { if (TryBoxSelect("版次", out var r)) { RevisionRegion = r; UpdateStatus(_revisionStatus, RevisionRegion); } }
    private void ClearRevision() { RevisionRegion = new LocalRectangle(); UpdateStatus(_revisionStatus, RevisionRegion); }

    private void SelectPhase() { if (TryBoxSelect("设计阶段", out var r)) { PhaseRegion = r; UpdateStatus(_phaseStatus, PhaseRegion); } }
    private void ClearPhase() { PhaseRegion = new LocalRectangle(); UpdateStatus(_phaseStatus, PhaseRegion); }

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
        Dock = DockStyle.Left,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DimGray,
        Width = UiLayout.Scale(300),
        AutoEllipsis = true
    };

    private static Panel MakeFieldRow(Label statusLabel, Action select, Action clear)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        panel.Controls.Add(statusLabel);

        var selectBtn = UiLayout.CreateButton("框选", 60);
        selectBtn.Click += (_, _) => select();
        panel.Controls.Add(selectBtn);

        var clearBtn = UiLayout.CreateButton("清除", 60);
        clearBtn.Click += (_, _) => clear();
        panel.Controls.Add(clearBtn);

        return panel;
    }
}
