namespace ZwcadBatchPlot;

/// <summary>插件自有绘图仪文字输出模式切换结果。</summary>
public sealed class PlotTextGeometryModeResult
{
    public bool Success { get; set; }
    public bool Changed { get; set; }
    public string Message { get; set; } = "";
}
