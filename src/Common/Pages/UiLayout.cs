using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

public static class UiLayout
{
    // 全部插件窗体统一使用小一号字体，提升信息密度并保持中文显示清晰。
    public static readonly Font DefaultFont = new("Microsoft YaHei UI", 7F, FontStyle.Regular, GraphicsUnit.Point);

    private static readonly float DpiX;
    private static readonly float DpiY;

    static UiLayout()
    {
        using var graphics = Graphics.FromHwnd(IntPtr.Zero);
        DpiX = graphics.DpiX;
        DpiY = graphics.DpiY;
    }

    public static void ConfigureForm(Form form, int designWidth, int designHeight, int minimumWidth, int minimumHeight,
        FormStartPosition startPosition = FormStartPosition.CenterParent)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.Font = DefaultFont;
        form.StartPosition = startPosition;

        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var maxWidth = workingArea.Width - Scale(80);
        var maxHeight = workingArea.Height - Scale(80);
        var scaledDesignWidth = Scale(designWidth);
        var scaledDesignHeight = Scale(designHeight);
        var scaledMinimumWidth = Scale(minimumWidth);
        var scaledMinimumHeight = Scale(minimumHeight);

        // WinForms 会按 Dpi 自动放大子控件；窗体本身也必须用同一套比例放大，避免高分屏下内容被裁切。
        form.Size = new Size(
            Math.Min(scaledDesignWidth, Math.Max(scaledMinimumWidth, maxWidth)),
            Math.Min(scaledDesignHeight, Math.Max(scaledMinimumHeight, maxHeight)));
        form.MinimumSize = new Size(
            Math.Min(scaledMinimumWidth, workingArea.Width - Scale(40)),
            Math.Min(scaledMinimumHeight, workingArea.Height - Scale(40)));
    }

    public static void ConfigureBatchPlotForm(Form form)
    {
        ConfigureForm(form, Scale(980), Scale(620), Scale(720), Scale(460), FormStartPosition.CenterScreen);
    }

    public static int Scale(int value)
    {
        return Math.Max(1, (int)Math.Round(value * DpiX / 96F));
    }

    public static int ButtonWidth(string text, int minimumWidth)
    {
        var measured = TextRenderer.MeasureText(text, DefaultFont).Width + Scale(16);
        return Math.Max(Scale(minimumWidth), measured);
    }

    public static int ButtonHeight()
    {
        return Math.Max(Scale(22), TextRenderer.MeasureText("批量打印", DefaultFont).Height + Scale(6));
    }

    public static int ActionButtonRowsHeight()
    {
        return ButtonHeight() * 2 + Scale(10);
    }

    public static int ActionPanelHeight()
    {
        return ActionButtonRowsHeight() + ButtonHeight() * 2 + Scale(24);
    }

    public static Button CreateButton(string text, int minimumWidth)
    {
        return new Button
        {
            Text = text,
            Width = ButtonWidth(text, minimumWidth),
            Height = ButtonHeight(),
            Margin = new Padding(0, Scale(1), Scale(5), Scale(1)),
            UseVisualStyleBackColor = true
        };
    }

    /// <summary>向 TableLayoutPanel 添加标签+控件行。</summary>
    public static void AddRow(TableLayoutPanel table, int row, string labelText, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, Scale(34)));
        table.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    /// <summary>将 double 值限定到 NumericUpDown 的范围内。</summary>
    public static decimal Clamp(NumericUpDown input, double value)
    {
        return (decimal)Math.Max((double)input.Minimum, Math.Min((double)input.Maximum, value));
    }

    public static void StyleGrid(DataGridView grid, Font font)
    {
        grid.Dock = DockStyle.Fill;
        grid.AutoGenerateColumns = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = true;
        grid.RowHeadersVisible = false;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.GridColor = Color.FromArgb(220, 220, 220);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = Math.Max(Scale(29), font.Height + Scale(12));
        grid.RowTemplate.Height = Math.Max(Scale(24), font.Height + Scale(9));
        grid.DefaultCellStyle.Font = font;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(51, 122, 183);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(font, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(245, 247, 250);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 250, 250);
    }
}
