using DiskMap.Core.Model;

namespace DiskMap.Core.Layout;

public enum BoxKind : byte
{
    /// <summary>A folder drawn with a title bar and its contents inside.</summary>
    Frame,
    /// <summary>A file, or a folder too small to open up.</summary>
    Tile,
    /// <summary>Free space on the drive (drive root only).</summary>
    Free,
    /// <summary>Many items too small to draw, merged into one box.</summary>
    Rest,
}

public sealed class MapBox
{
    public required DiskNode? Node { get; init; }
    public required RectD Rect { get; init; }
    public required BoxKind Kind { get; init; }
    public required int Depth { get; init; }
    public long Size { get; init; }
    public int RestCount { get; init; }
    public MapBox? Parent { get; init; }

    /// <summary>
    /// For a folder drawn together with the subfolder it (almost) entirely consists of ("Users / alex"):
    /// those folders from the outside in. <see cref="Node"/> is the innermost one.
    /// </summary>
    public IReadOnlyList<DiskNode>? Chain { get; init; }

    /// <summary>Position among its siblings: 0 for the largest, 1 for the smallest.</summary>
    public double Rank { get; init; }

    /// <summary>Stable identity across layouts, used to animate boxes between them.</summary>
    public long Key => Node?.Id ?? (Kind == BoxKind.Free ? -1 : -(2 + (Parent?.Node?.Id ?? 0)));

    /// <summary>True when this box shows <paramref name="node"/>, also as part of a merged title.</summary>
    public bool Shows(DiskNode node) => ReferenceEquals(Node, node) || (Chain?.Contains(node) ?? false);
}

public sealed class MapLayoutOptions
{
    /// <summary>Title bar height of a folder, by depth (the last value is used for deeper folders).</summary>
    public double[] HeaderHeights { get; init; } = [26, 24, 22, 20];

    /// <summary>Space between a folder's edge and its contents, by depth.</summary>
    public double[] Paddings { get; init; } = [5, 4, 3, 2];

    /// <summary>Gap between neighbouring boxes, by depth: wide between big folders, narrow deep inside them.</summary>
    public double[] Gaps { get; init; } = [6, 4, 3, 2];

    public double MinFrameWidth { get; init; } = 64;
    public double MinFrameHeight { get; init; } = 50;
    public double MinTileArea { get; init; } = 36;

    /// <summary>Boxes thinner than this (after the gap) read as hairlines and go into the "small items" box.</summary>
    public double MinTileSide { get; init; } = 6;

    /// <summary>A folder that is at least this share one subfolder gets one title with it ("Users / alex").</summary>
    public double ChainShare { get; init; } = 0.995;

    public MapTiling Tiling { get; init; } = MapTiling.Squarified;
    public int MaxDepth { get; init; } = 16;
    public int MaxBoxes { get; init; } = 40_000;

    public double HeaderHeight(int depth) => At(HeaderHeights, depth);
    public double Padding(int depth) => At(Paddings, depth);
    public double Gap(int depth) => At(Gaps, depth);

    private static double At(double[] values, int depth) => values[Math.Min(depth, values.Length - 1)];
}

/// <summary>
/// Builds the nested boxes for one folder: frames for folders that fit, tiles for the rest. Neighbouring boxes
/// are separated by gaps that shrink with depth, so the eye groups boxes by how close they are.
/// </summary>
public static class MapLayout
{
    public static List<MapBox> Build(DiskNode folder, RectD bounds, long freeBytes, MapLayoutOptions? options = null)
    {
        options ??= new MapLayoutOptions();
        var boxes = new List<MapBox>(1024);
        var items = new List<(DiskNode? Node, double Weight)>();
        foreach (var c in folder.Children ?? [])
        {
            if (c.Size > 0) items.Add((c, c.Size));
        }
        if (freeBytes > 0) items.Add((null, freeBytes));
        items.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
        LayoutLevel(items, bounds.Inflate(-options.Gap(0)), 0, null, options, boxes, 0, 0);
        return boxes;
    }

    /// <param name="inner">The area to fill. Boxes on its edge touch it; boxes inside are a gap apart.</param>
    /// <param name="extraWeight">Items that go into the "small items" box as well (what sits beside a merged subfolder).</param>
    private static void LayoutLevel(List<(DiskNode? Node, double Weight)> items, RectD inner, int depth, MapBox? parent,
        MapLayoutOptions o, List<MapBox> boxes, double extraWeight, int extraCount)
    {
        if (items.Count == 0 || inner.W < 1 || inner.H < 1) return;
        double half = o.Gap(depth) / 2;
        var bounds = inner.Inflate(half);

        // Merge everything that would end up smaller than MinTileArea into one "rest" box.
        double total = extraWeight;
        foreach (var it in items) total += it.Weight;
        double pxPerUnit = bounds.Area / total;
        int keep = items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Weight * pxPerUnit < o.MinTileArea)
            {
                keep = i;
                break;
            }
        }
        double restWeight = extraWeight;
        for (int i = keep; i < items.Count; i++) restWeight += items[i].Weight;
        int restCount = extraCount + items.Count - keep;
        if (restCount == 1 && extraCount == 0)
        {
            keep++;
            restWeight = 0;
            restCount = 0;
        }

        var rects = Arrange(items, keep, restCount > 0 ? restWeight : 0, bounds, o);

        // Small items can still come out as hairlines (at the end of a tall row, say). Fold the first such box and
        // everything smaller into the "small items" box, and lay out again.
        for (int pass = 0; pass < 4; pass++)
        {
            int thin = -1;
            for (int i = 1; i < keep; i++)
            {
                if (items[i].Node != null && Math.Min(rects[i].W, rects[i].H) - 2 * half < o.MinTileSide)
                {
                    thin = i;
                    break;
                }
            }
            if (thin < 0) break;
            for (int i = thin; i < keep; i++) restWeight += items[i].Weight;
            restCount += keep - thin;
            keep = thin;
            rects = Arrange(items, keep, restWeight, bounds, o);
        }
        int n = rects.Length;

        for (int i = 0; i < keep; i++)
        {
            if (boxes.Count >= o.MaxBoxes) return;
            var (node, weight) = items[i];
            var r = Snap(rects[i], half);
            if (r.W < 1 || r.H < 1) continue;
            double rank = n > 1 ? (double)i / (n - 1) : 0;
            if (node == null)
            {
                boxes.Add(new MapBox { Node = null, Rect = r, Kind = BoxKind.Free, Depth = depth, Size = (long)weight, Parent = parent, Rank = rank });
                continue;
            }

            // A folder that is (almost) all one subfolder shares one title with it. Whatever sits beside that
            // subfolder goes into the subfolder's "small items" box.
            var inside = node;
            List<DiskNode>? chain = null;
            double besides = 0;
            int besidesCount = 0;
            while (Largest(inside) is { IsDirectory: true, Children.Count: > 0 } big && big.Size >= inside.Size * o.ChainShare)
            {
                chain ??= [node];
                foreach (var c in inside.Children!)
                {
                    if (!ReferenceEquals(c, big) && c.Size > 0)
                    {
                        besides += c.Size;
                        besidesCount++;
                    }
                }
                inside = big;
                chain.Add(inside);
            }

            bool canOpen = inside.Children is { Count: > 0 } && depth < o.MaxDepth &&
                           r.W >= o.MinFrameWidth && r.H >= o.MinFrameHeight && HasVisibleChildren(inside);
            if (canOpen)
            {
                var frame = new MapBox
                {
                    Node = inside, Chain = chain, Rect = r, Kind = BoxKind.Frame, Depth = depth, Size = node.Size, Parent = parent, Rank = rank,
                };
                boxes.Add(frame);
                double pad = o.Padding(depth), head = o.HeaderHeight(depth);
                var content = new RectD(r.X + pad, r.Y + head, r.W - 2 * pad, r.H - head - pad);
                var kids = new List<(DiskNode?, double)>(inside.Children!.Count);
                foreach (var c in inside.Children!)
                {
                    if (c.Size > 0) kids.Add((c, c.Size));
                }
                LayoutLevel(kids, content, depth + 1, frame, o, boxes, besides, besidesCount);
            }
            else
            {
                boxes.Add(new MapBox { Node = node, Rect = r, Kind = BoxKind.Tile, Depth = depth, Size = node.Size, Parent = parent, Rank = rank });
            }
        }

        var restRect = restCount > 0 ? Snap(rects[keep], half) : default;
        if (restCount > 0 && restRect.Area >= 2 && boxes.Count < o.MaxBoxes)
        {
            boxes.Add(new MapBox
            {
                Node = null, Rect = restRect, Kind = BoxKind.Rest, Depth = depth,
                Size = (long)restWeight, RestCount = restCount, Parent = parent, Rank = 1,
            });
        }
    }

    /// <summary>Cells for the first <paramref name="keep"/> items, plus one for the "small items" box when it has weight.</summary>
    private static RectD[] Arrange(List<(DiskNode? Node, double Weight)> items, int keep, double restWeight, RectD bounds, MapLayoutOptions o)
    {
        int n = keep + (restWeight > 0 ? 1 : 0);
        var weights = new double[n];
        for (int i = 0; i < keep; i++) weights[i] = items[i].Weight;
        if (restWeight > 0) weights[keep] = restWeight;
        var rects = new RectD[n];
        if (o.Tiling == MapTiling.Rows) RowLayout.Layout(weights, bounds, rects);
        else Squarify.Layout(weights, bounds, rects);
        return rects;
    }

    /// <summary>Shrinks a cell by half a gap on each side and rounds its edges to whole pixels, so gaps stay even.</summary>
    private static RectD Snap(RectD r, double half)
    {
        double x0 = Math.Round(r.X + half), y0 = Math.Round(r.Y + half);
        double x1 = Math.Round(r.Right - half), y1 = Math.Round(r.Bottom - half);
        return new RectD(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>The biggest child (children are kept sorted largest first).</summary>
    internal static DiskNode? Largest(DiskNode node) => node.Children is { Count: > 0 } kids ? kids[0] : null;

    /// <summary>A folder whose only child is a single item gains nothing from a frame, unless that child is a folder.</summary>
    private static bool HasVisibleChildren(DiskNode node)
    {
        int count = 0;
        foreach (var c in node.Children!)
        {
            if (c.Size > 0 && ++count > 1) return true;
        }
        return count == 1 && node.Children!.Find(c => c.Size > 0)!.IsDirectory;
    }

    /// <summary>Topmost box under a point (children are drawn after, and above, their parents).</summary>
    public static MapBox? HitTest(IReadOnlyList<MapBox> boxes, double x, double y)
    {
        for (int i = boxes.Count - 1; i >= 0; i--)
        {
            if (boxes[i].Rect.Contains(x, y)) return boxes[i];
        }
        return null;
    }
}
