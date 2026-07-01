using System;
using System.Collections.Generic;
using System.IO;

namespace ZwcadBatchPlot;

public sealed class TitleBlockDefinition
{
    public string BlockName { get; set; } = "";
    public bool HasPrintRegion { get; set; }
    public string CoordinateMode { get; set; } = "Local";
    public LocalRectangle PrintRegion { get; set; } = new();
    public string PaperName { get; set; } = "";
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
    public LocalRectangle TitleRegion { get; set; } = new();
    public LocalRectangle DrawingNumberRegion { get; set; } = new();
    public LocalRectangle DateRegion { get; set; } = new();
    public LocalRectangle RevisionRegion { get; set; } = new();
    public LocalRectangle PhaseRegion { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class TitleBlockLibrary
{
    public int Version { get; set; } = 2;
    public List<TitleBlockDefinition> Blocks { get; set; } = new();
}

public sealed class LocalRectangle
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }

    /// <summary>矩形实际宽度（旋转 UCS 下与包围盒宽度不同），0 表示用 MaxX-MinX。</summary>
    public double ActualWidth { get; set; }
    /// <summary>矩形实际高度，同上。</summary>
    public double ActualHeight { get; set; }
    /// <summary>矩形 4 个实际角点（WCS），格式 [x0,y0,x1,y1,x2,y2,x3,y3]。null 表示无。</summary>
    public double[]? CornerPoints { get; set; }

    public static LocalRectangle FromPoints(double x1, double y1, double x2, double y2)
    {
        return new LocalRectangle
        {
            MinX = Math.Min(x1, x2),
            MinY = Math.Min(y1, y2),
            MaxX = Math.Max(x1, x2),
            MaxY = Math.Max(y1, y2)
        };
    }

    public bool Contains(double x, double y, double tolerance = 1e-6)
    {
        return x >= MinX - tolerance
            && x <= MaxX + tolerance
            && y >= MinY - tolerance
            && y <= MaxY + tolerance;
    }

    /// <summary>区域是否有实际面积（非零区域）。零区域表示该字段未配置。</summary>
    public bool HasArea(double tolerance = 1e-6)
    {
        return Math.Abs(MaxX - MinX) > tolerance
            && Math.Abs(MaxY - MinY) > tolerance;
    }
}

public sealed class PlotJob
{
    public bool Selected { get; set; } = true;
    public bool IsManualWindow { get; set; }
    public string SourceFile { get; set; } = "";
    public string OutputFileName => Path.GetFileName(OutputPath);
    public long SortPriority { get; set; }
    public string SpaceName { get; set; } = "";
    public bool IsPaperSpace { get; set; }
    public string BlockName { get; set; } = "";
    public string BlockHandle { get; set; } = "";
    public int MatchIndex { get; set; }
    public string DrawingNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string CadDrawingNumber { get; set; } = "";
    public string CadTitle { get; set; } = "";
    public string Date { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Phase { get; set; } = "";
    public string CadDate { get; set; } = "";
    public string CadRevision { get; set; } = "";
    public string CadPhase { get; set; } = "";
    public string PaperName { get; set; } = "";
    public string ScaleText { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string PaperSizeText { get; set; } = "";
    public string DetectionNote { get; set; } = "";
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }
    public string OutputPath { get; set; } = "";

    /// <summary>MinX/Y/MaxX/MaxY 已经是 DCS 坐标，GetPlotWindow 跳过 WCS→DCS 变换。</summary>
    public bool IsDcsWindow { get; set; }
    /// <summary>打印区域 4 个实际 WCS 角点，格式 [x0,y0,x1,y1,x2,y2,x3,y3]。null 时用 Min/Max。</summary>
    public double[]? CornerPoints { get; set; }
}

public enum TitleBlockScanScope
{
    AllSpaces,
    PaperLayouts,
    CurrentSpace,
    ModelSpace
}

public sealed class PaperDetection
{
    public string PaperName { get; set; } = "未知";
    public string ScaleText { get; set; } = "未知";
    public double ScaleValue { get; set; }
    public bool IsLong { get; set; }
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
    public string Note { get; set; } = "";
}
