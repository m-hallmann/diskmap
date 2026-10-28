using DiskMap.Core.Model;

namespace DiskMap.Core.Layout;

/// <summary>
/// Finds the few big folders a folder is organised into, so the map can give each one a colour that everything
/// inside it shares. Starts from the subfolders and keeps opening the biggest one while it holds more than
/// <see cref="Share"/> of the total, so the groups end up at similar sizes rather than at one fixed depth.
/// </summary>
public static class FolderGroups
{
    public const double Share = 0.22;
    public const int MaxGroups = 8;

    /// <summary>The groups, largest first (at most <paramref name="max"/>).</summary>
    public static List<DiskNode> Find(DiskNode folder, int max = MaxGroups, double chainShare = 0.995)
    {
        double limit = folder.Size * Share;
        var front = Subfolders(folder, chainShare);
        var kept = new HashSet<DiskNode>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < 64; i++)
        {
            var big = front.Where(n => n.Size > limit && !kept.Contains(n)).MaxBy(n => n.Size);
            if (big == null) break;
            // A folder that is mostly files (not subfolders) stays one group.
            var inside = Subfolders(big, chainShare);
            if (inside.Sum(n => n.Size) < big.Size / 2)
            {
                kept.Add(big);
                continue;
            }
            front.Remove(big);
            front.AddRange(inside);
        }
        front.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        if (front.Count > max) front.RemoveRange(max, front.Count - max);
        return front;
    }

    /// <summary>Subfolders with contents, each followed down while it is (almost) all one subfolder, as the map draws them.</summary>
    private static List<DiskNode> Subfolders(DiskNode folder, double chainShare)
    {
        var list = new List<DiskNode>();
        foreach (var c in folder.Children ?? [])
        {
            if (!c.IsDirectory || c.Size <= 0 || c.Children is not { Count: > 0 }) continue;
            var n = c;
            while (MapLayout.Largest(n) is { IsDirectory: true, Children.Count: > 0 } big && big.Size >= n.Size * chainShare) n = big;
            list.Add(n);
        }
        return list;
    }
}
