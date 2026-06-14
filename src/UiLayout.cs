using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

public static class UiLayout
{
    public static readonly Font DefaultFont = new("Microsoft YaHei UI", 8F, FontStyle.Regular, GraphicsUnit.Point);

    public static void ConfigureForm(Form form, int designWidth, int designHeight, int minimumWidth, int minimumHeight)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.Font = DefaultFont;
        form.StartPosition = FormStartPosition.CenterParent;

        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = Math.Min(designWidth, Math.Max(minimumWidth, workingArea.Width - Scale(80)));
        var height = Math.Min(designHeight, Math.Max(minimumHeight, workingArea.Height - Scale(80)));

        form.Size = new Size(width, height);
        form.MinimumSize = new Size(Math.Min(minimumWidth, workingArea.Width - Scale(40)), Math.Min(minimumHeight, workingArea.Height - Scale(40)));
    }

    public static void ConfigureBatchPlotForm(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.Font = DefaultFont;
        form.StartPosition = FormStartPosition.CenterScreen;

        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var designWidth = Scale(980);
        var designHeight = Scale(620);
        var minimumWidth = Scale(720);
        var minimumHeight = Scale(460);
        var maxWidth = workingArea.Width - Scale(80);
        var maxHeight = workingArea.Height - Scale(80);
        var width = Math.Min(designWidth, Math.Max(minimumWidth, maxWidth));
        var height = Math.Min(designHeight, Math.Max(minimumHeight, maxHeight));

        form.Size = new Size(width, height);
        form.MinimumSize = new Size(Math.Min(minimumWidth, workingArea.Width - Scale(40)), Math.Min(minimumHeight, workingArea.Height - Scale(40)));
    }

    public static int Scale(int value)
    {
        using var graphics = Graphics.FromHwnd(IntPtr.Zero);
        return Math.Max(1, (int)Math.Round(value * graphics.DpiX / 96F));
    }

    public static int ButtonWidth(string text, int minimumWidth)
    {
        var measured = TextRenderer.MeasureText(text, DefaultFont).Width + Scale(20);
        return Math.Max(Scale(minimumWidth), measured);
    }

    public static int ButtonHeight()
    {
        return Math.Max(Scale(24), TextRenderer.MeasureText("批量打印", DefaultFont).Height + Scale(8));
    }

    public static int ActionButtonRowsHeight()
    {
        return ButtonHeight() * 2 + Scale(14);
    }

    public static int ActionPanelHeight()
    {
        return ActionButtonRowsHeight() + ButtonHeight() * 2 + Scale(30);
    }

    public static Button CreateButton(string text, int minimumWidth)
    {
        return new Button
        {
            Text = text,
            Width = ButtonWidth(text, minimumWidth),
            Height = ButtonHeight(),
            Margin = new Padding(0, Scale(2), Scale(6), Scale(2)),
            UseVisualStyleBackColor = true
        };
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
        grid.ColumnHeadersHeight = Math.Max(Scale(34), font.Height + Scale(16));
        grid.RowTemplate.Height = Math.Max(Scale(28), font.Height + Scale(12));
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
