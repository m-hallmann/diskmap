namespace DiskMap.Core.Model;

/// <summary>Keeps folder totals right when parts of a scanned tree change.</summary>
public static class TreeOps
{
    /// <summary>Removes a node and subtracts its totals from every ancestor.</summary>
    public static void Remove(DiskNode node)
    {
        var parent = node.Parent;
        if (parent == null) return;
        parent.Children!.Remove(node);
        Subtract(parent, node.Size, node.Logical, node.FileCount, node.CategorySizes, node.Category);
        node.Parent = null;
    }

    /// <summary>Swaps in a freshly scanned node (e.g. after a partial delete) and fixes totals.</summary>
    public static void Replace(DiskNode old, DiskNode fresh)
    {
        var parent = old.Parent;
        if (parent == null) return;
        int index = parent.Children!.IndexOf(old);
        Subtract(parent, old.Size, old.Logical, old.FileCount, old.CategorySizes, old.Category);
        fresh.Flags |= old.Flags & (NodeFlags.Game | NodeFlags.Protected | NodeFlags.NoDelete);
        fresh.Label = old.Label;
        fresh.Tag = old.Tag;
        if (index >= 0) parent.Children[index] = fresh;
        else parent.Children.Add(fresh);
        fresh.Parent = parent;
        old.Parent = null;
        if (fresh.Size == 0)
        {
            parent.Children.Remove(fresh);
            return;
        }
        for (var p = parent; p != null; p = p.Parent)
        {
            p.Size += fresh.Size;
            p.Logical += fresh.Logical;
            p.FileCount += fresh.FileCount;
            if (p.CategorySizes != null && fresh.CategorySizes != null)
            {
                for (int i = 0; i < p.CategorySizes.Length; i++) p.CategorySizes[i] += fresh.CategorySizes[i];
                p.Category = Scanning.ScanPostProcessor.Dominant(p.CategorySizes, p.Category);
            }
            p.Children!.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        }
    }

    private static void Subtract(DiskNode from, long size, long logical, long files, long[]? cats, Category cat)
    {
        for (var p = from; p != null; p = p.Parent)
        {
            p.Size -= size;
            p.Logical -= logical;
            p.FileCount -= files;
            if (p.CategorySizes != null)
            {
                if (cats != null)
                {
                    for (int i = 0; i < cats.Length; i++) p.CategorySizes[i] -= cats[i];
                }
                else
                {
                    p.CategorySizes[(int)cat] -= size;
                }
                p.Category = Scanning.ScanPostProcessor.Dominant(p.CategorySizes, p.Category);
            }
            p.Children!.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        }
    }

    /// <summary>Per-category totals of a node (works for files too).</summary>
    public static long[] CategoryTotals(DiskNode node)
    {
        if (node.CategorySizes != null) return node.CategorySizes;
        var cats = new long[Categories.Count];
        cats[(int)node.Category] = node.Size;
        return cats;
    }

    /// <summary>Drops nodes whose ancestor is also in the list.</summary>
    public static List<DiskNode> TopLevel(IEnumerable<DiskNode> nodes)
    {
        var set = new HashSet<DiskNode>(nodes, ReferenceEqualityComparer.Instance);
        return set.Where(n => !n.Ancestors().Any(set.Contains)).OrderByDescending(n => n.Size).ToList();
    }
}
