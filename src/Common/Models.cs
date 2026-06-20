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
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public sealed class TitleBlockLibrary
{
    public int Version { get; set; } = 1;
    public List<TitleBlockDefinition> Blocks { get; set; } = new();
}

public sealed class LocalRectangle
{
    public double MinX { get; set; }
    public double MinY { get; set; }
    public double MaxX { get; set; }
    public double MaxY { get; set; }

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
