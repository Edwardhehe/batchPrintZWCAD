using System;

namespace ZwcadBatchPlot;

/// <summary>角度规范化：90° 步进量化，以及有符号归一到 (-180, 180]。</summary>
internal static class QuarterRotation
{
    public static int Normalize(int degrees)
    {
        var value = degrees % 360;
        if (value < 0)
        {
            value += 360;
        }

        var quarter = (int)Math.Round(value / 90.0, MidpointRounding.AwayFromZero) % 4;
        if (quarter < 0)
        {
            quarter += 4;
        }

        return quarter * 90;
    }

    public static int Normalize(double degrees)
    {
        return Normalize((int)Math.Round(degrees, MidpointRounding.AwayFromZero));
    }

    public static double NormalizeSigned(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees))
        {
            return 0d;
        }

        var value = degrees % 360d;
        if (value <= -180d)
        {
            value += 360d;
        }
        else if (value > 180d)
        {
            value -= 360d;
        }

        return value;
    }
}
