namespace DiskMap.Core.Layout;

public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public double Area => W * H;
    public bool Contains(double px, double py) => px >= X && px < X + W && py >= Y && py < Y + H;
    public RectD Inflate(double d) => new(X - d, Y - d, Math.Max(0, W + 2 * d), Math.Max(0, H + 2 * d));

    public static RectD Lerp(RectD a, RectD b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.W + (b.W - a.W) * t, a.H + (b.H - a.H) * t);
}

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk 2000).</summary>
public static class Squarify
{
    /// <summary>
    /// Lays out <paramref name="weights"/> (sorted largest first) inside <paramref name="bounds"/>
    /// so each rectangle's area is proportional to its weight and aspect ratios stay close to 1.
    /// </summary>
    public static void Layout(ReadOnlySpan<double> weights, RectD bounds, Span<RectD> result)
    {
        double total = 0;
        foreach (var weight in weights) total += weight;
        if (total <= 0 || bounds.W <= 0 || bounds.H <= 0)
        {
            result.Clear();
            return;
        }

        double scale = bounds.W * bounds.H / total;
        double x = bounds.X, y = bounds.Y, w = bounds.W, h = bounds.H;
        int start = 0;
        while (start < weights.Length)
        {
            double side = Math.Max(1e-9, Math.Min(w, h));
            int end = start + 1;
            double rowArea = weights[start] * scale;
            double worst = Worst(weights[start..end], scale, rowArea, side);
            while (end < weights.Length)
            {
                double nextArea = rowArea + weights[end] * scale;
                double nextWorst = Worst(weights[start..(end + 1)], scale, nextArea, side);
                if (nextWorst > worst) break;
                worst = nextWorst;
                rowArea = nextArea;
                end++;
            }

            if (w >= h)
            {
                double stripW = h > 0 ? rowArea / h : 0;
                double yy = y;
                for (int i = start; i < end; i++)
                {
                    double ih = stripW > 0 ? weights[i] * scale / stripW : 0;
                    result[i] = new RectD(x, yy, stripW, ih);
                    yy += ih;
                }
                x += stripW;
                w -= stripW;
            }
            else
            {
                double stripH = w > 0 ? rowArea / w : 0;
                double xx = x;
                for (int i = start; i < end; i++)
                {
                    double iw = stripH > 0 ? weights[i] * scale / stripH : 0;
                    result[i] = new RectD(xx, y, iw, stripH);
                    xx += iw;
                }
                y += stripH;
                h -= stripH;
            }
            start = end;
        }
    }

    private static double Worst(ReadOnlySpan<double> row, double scale, double rowArea, double side)
    {
        double s2 = rowArea * rowArea, side2 = side * side, max = 0;
        foreach (var weight in row)
        {
            double a = weight * scale;
            if (a <= 0) continue;
            double ratio = Math.Max(side2 * a / s2, s2 / (side2 * a));
            if (ratio > max) max = ratio;
        }
        return max;
    }
}
