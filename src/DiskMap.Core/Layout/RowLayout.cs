namespace DiskMap.Core.Layout;

public enum MapTiling : byte
{
    /// <summary>Squarified: strips along the shorter side, so boxes stay close to square.</summary>
    Squarified,
    /// <summary>Full-width rows from top to bottom that get shorter as the items get smaller.</summary>
    Rows,
}

/// <summary>
/// Lays items out in full-width rows, top to bottom. Every box in a row has the row's height and a width that
/// follows its weight. A row takes the next item while that keeps its boxes closer to square on average, so with
/// weights sorted largest first the rows get shorter towards the bottom.
/// </summary>
public static class RowLayout
{
    /// <summary>
    /// Lays out <paramref name="weights"/> (sorted largest first) inside <paramref name="bounds"/>. A row also keeps
    /// taking items when what is left would only make a row thinner than <paramref name="minRow"/> (about one
    /// label tall), so the small items at the end don't turn into thin, unlabelled strips.
    /// </summary>
    public static void Layout(ReadOnlySpan<double> weights, RectD bounds, Span<RectD> result, double minRow = 24)
    {
        double total = 0;
        foreach (var weight in weights) total += weight;
        if (total <= 0 || bounds.W <= 0 || bounds.H <= 0)
        {
            result.Clear();
            return;
        }

        double scale = bounds.W * bounds.H / total;
        double left = bounds.W * bounds.H, y = bounds.Y;
        int start = 0;
        while (start < weights.Length)
        {
            int end = start + 1;
            double rowArea = weights[start] * scale;
            double average = AverageAspect(weights[start..end], scale, rowArea, bounds.W);
            while (end < weights.Length)
            {
                double nextArea = rowArea + weights[end] * scale;
                double next = AverageAspect(weights[start..(end + 1)], scale, nextArea, bounds.W);
                if (next > average && (left - rowArea) / bounds.W >= minRow) break;
                average = next;
                rowArea = nextArea;
                end++;
            }

            double h = rowArea / bounds.W, x = bounds.X;
            for (int i = start; i < end; i++)
            {
                double w = h > 0 ? weights[i] * scale / h : 0;
                result[i] = new RectD(x, y, w, h);
                x += w;
            }
            y += h;
            left -= rowArea;
            start = end;
        }
    }

    private static double AverageAspect(ReadOnlySpan<double> row, double scale, double rowArea, double width)
    {
        double h = rowArea / width, sum = 0;
        int count = 0;
        foreach (var weight in row)
        {
            double w = weight * scale / h;
            if (w <= 0 || h <= 0) continue;
            sum += Math.Max(w / h, h / w);
            count++;
        }
        return count > 0 ? sum / count : double.MaxValue;
    }
}
