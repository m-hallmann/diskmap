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

    /// <summary>Stable identity across layouts, used to animate boxes between them.</summary>
    public long Key => Node?.Id ?? (Kind == BoxKind.Free ? -1 : -(2 + (Parent?.Node?.Id ?? 0)));
}

public sealed class MapLayoutOptions
{
    public double HeaderHeight { get; init; } = 18;
    public double Padding { get; init; } = 2;
    public double MinFrameWidth { get; init; } = 52;
    public double MinFrameHeight { get; init; } = 40;
    public double MinTileArea { get; init; } = 9;
    public int MaxDepth { get; init; } = 16;
    public int MaxBoxes { get; init; } = 40_000;
}

/// <summary>Builds the nested boxes for one folder: frames for folders that fit, tiles for the rest.</summary>
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
        LayoutLevel(items, bounds, 0, null, options, boxes);
        return boxes;
    }

    private static void LayoutLevel(List<(DiskNode? Node, double Weight)> items, RectD bounds, int depth, MapBox? parent,
        MapLayoutOptions o, List<MapBox> boxes)
    {
        if (items.Count == 0 || bounds.W < 1 || bounds.H < 1) return;

        // Merge everything that would end up smaller than MinTileArea into one "rest" box.
        double total = 0;
        foreach (var it in items) total += it.Weight;
        double pxPerUnit = bounds.Area / total;
        int keep = items.Count;
        double restWeight = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Weight * pxPerUnit < o.MinTileArea)
            {
                keep = i;
                break;
            }
        }
        for (int i = keep; i < items.Count; i++) restWeight += items[i].Weight;
        int restCount = items.Count - keep;
        if (restCount == 1)
        {
            keep++;
            restWeight = 0;
            restCount = 0;
        }

        int n = keep + (restCount > 0 ? 1 : 0);
        var weights = new double[n];
        for (int i = 0; i < keep; i++) weights[i] = items[i].Weight;
        if (restCount > 0) weights[keep] = restWeight;
        var rects = new RectD[n];
        Squarify.Layout(weights, bounds, rects);

        for (int i = 0; i < keep; i++)
        {
            if (boxes.Count >= o.MaxBoxes) return;
            var (node, weight) = items[i];
            var r = rects[i];
            if (node == null)
            {
                boxes.Add(new MapBox { Node = null, Rect = r, Kind = BoxKind.Free, Depth = depth, Size = (long)weight, Parent = parent });
                continue;
            }

            bool canOpen = node.Children is { Count: > 0 } && depth < o.MaxDepth &&
                           r.W >= o.MinFrameWidth && r.H >= o.MinFrameHeight && HasVisibleChildren(node);
            if (canOpen)
            {
                var frame = new MapBox { Node = node, Rect = r, Kind = BoxKind.Frame, Depth = depth, Size = node.Size, Parent = parent };
                boxes.Add(frame);
                var inner = new RectD(r.X + o.Padding, r.Y + o.HeaderHeight, r.W - 2 * o.Padding, r.H - o.HeaderHeight - o.Padding);
                var kids = new List<(DiskNode?, double)>(node.Children!.Count);
                foreach (var c in node.Children!)
                {
                    if (c.Size > 0) kids.Add((c, c.Size));
                }
                LayoutLevel(kids, inner, depth + 1, frame, o, boxes);
            }
            else
            {
                boxes.Add(new MapBox { Node = node, Rect = r, Kind = BoxKind.Tile, Depth = depth, Size = node.Size, Parent = parent });
            }
        }

        if (restCount > 0 && rects[keep].Area >= 2 && boxes.Count < o.MaxBoxes)
        {
            boxes.Add(new MapBox
            {
                Node = null, Rect = rects[keep], Kind = BoxKind.Rest, Depth = depth,
                Size = (long)restWeight, RestCount = restCount, Parent = parent,
            });
        }
    }

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
