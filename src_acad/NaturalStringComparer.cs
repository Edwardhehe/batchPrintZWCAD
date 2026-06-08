using System;
using System.Collections.Generic;

namespace ZwcadBatchPlot;

public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= "";
        y ??= "";
        var ix = 0;
        var iy = 0;

        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                var sx = ix;
                var sy = iy;
                while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                while (iy < y.Length && char.IsDigit(y[iy])) iy++;

                var nx = x.Substring(sx, ix - sx).TrimStart('0');
                var ny = y.Substring(sy, iy - sy).TrimStart('0');
                if (nx.Length != ny.Length)
                {
                    return nx.Length.CompareTo(ny.Length);
                }

                var numeric = string.Compare(nx, ny, StringComparison.Ordinal);
                if (numeric != 0)
                {
                    return numeric;
                }
            }
            else
            {
                var cx = char.ToUpperInvariant(x[ix]);
                var cy = char.ToUpperInvariant(y[iy]);
                if (cx != cy)
                {
                    return cx.CompareTo(cy);
                }

                ix++;
                iy++;
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}
