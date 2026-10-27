namespace DiskMap.Core.Model;

[Flags]
public enum NodeFlags : ushort
{
    None = 0,
    Directory = 1 << 0,
    /// <summary>Managed by Windows: never deleted directly, only through a cleanup action.</summary>
    Protected = 1 << 1,
    /// <summary>Junction, symlink, app alias or similar. Never followed, counts 0 bytes.</summary>
    ReparsePoint = 1 << 2,
    /// <summary>File has more than one name; this node is where its bytes are counted.</summary>
    Hardlinked = 1 << 3,
    /// <summary>Synthetic grouping node (NTFS metadata, unreadable space, ...).</summary>
    Synthetic = 1 << 4,
    /// <summary>Can be selected but not deleted from the map (Recycle Bin, grouped items).</summary>
    NoDelete = 1 << 5,
    /// <summary>Folder could not be read completely.</summary>
    Unreadable = 1 << 6,
    /// <summary>Root folder of an installed game.</summary>
    Game = 1 << 7,
}

/// <summary>
/// One file or folder in a scan. Folder sizes are aggregated over the whole subtree.
/// </summary>
public sealed class DiskNode
{
    private static int s_nextId;

    public DiskNode(string name, NodeFlags flags)
    {
        Name = name;
        Flags = flags;
        Id = Interlocked.Increment(ref s_nextId);
        if ((flags & NodeFlags.Directory) != 0) Children = new List<DiskNode>();
    }

    public int Id { get; }
    public string Name { get; internal set; }
    public NodeFlags Flags { get; internal set; }
    public DiskNode? Parent { get; internal set; }
    public List<DiskNode>? Children { get; }

    /// <summary>Bytes on disk (allocated), aggregated for folders.</summary>
    public long Size { get; internal set; }

    /// <summary>Logical file length, aggregated for folders.</summary>
    public long Logical { get; internal set; }

    /// <summary>Number of files, aggregated for folders.</summary>
    public long FileCount { get; internal set; }

    /// <summary>Bytes of files that also have a name inside this folder but are counted elsewhere.</summary>
    public long LinkedElsewhere { get; internal set; }

    public DateTime Modified { get; internal set; }

    /// <summary>Own category for files, dominant category for folders.</summary>
    public Category Category { get; internal set; }

    /// <summary>Per-category byte totals (folders only).</summary>
    public long[]? CategorySizes { get; internal set; }

    /// <summary>Friendlier display name, e.g. a game title for a Steam folder.</summary>
    public string? Label { get; internal set; }

    /// <summary>Extra information attached during post-processing (e.g. <see cref="Games.GameInfo"/>).</summary>
    public object? Tag { get; internal set; }

    public bool IsDirectory => (Flags & NodeFlags.Directory) != 0;
    public bool IsProtected => (Flags & NodeFlags.Protected) != 0;
    public bool IsSynthetic => (Flags & NodeFlags.Synthetic) != 0;
    public bool CanDelete => (Flags & (NodeFlags.Protected | NodeFlags.NoDelete | NodeFlags.Synthetic)) == 0 && Parent != null;
    public string DisplayName => Label ?? Name;

    public int Depth
    {
        get
        {
            int d = 0;
            for (var p = Parent; p != null; p = p.Parent) d++;
            return d;
        }
    }

    /// <summary>Full path on disk. Synthetic nodes return their parent's path.</summary>
    public string FullPath
    {
        get
        {
            if (Parent == null) return Name;
            var parts = new List<string>();
            for (var n = this; n.Parent != null; n = n.Parent)
            {
                if (!n.IsSynthetic) parts.Add(n.Name);
            }
            parts.Reverse();
            var root = Root.Name;
            return Path.Combine(root, Path.Combine(parts.ToArray()));
        }
    }

    public DiskNode Root
    {
        get
        {
            var n = this;
            while (n.Parent != null) n = n.Parent;
            return n;
        }
    }

    public bool IsAncestorOf(DiskNode other)
    {
        for (var p = other.Parent; p != null; p = p.Parent)
        {
            if (ReferenceEquals(p, this)) return true;
        }
        return false;
    }

    public IEnumerable<DiskNode> Ancestors()
    {
        for (var p = Parent; p != null; p = p.Parent) yield return p;
    }

    public DiskNode? Child(string name)
    {
        if (Children == null) return null;
        foreach (var c in Children)
        {
            if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
        }
        return null;
    }

    public DiskNode? Find(string relativePath)
    {
        var n = this;
        foreach (var part in relativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            n = n.Child(part);
            if (n == null) return null;
        }
        return n;
    }

    internal void AddChild(DiskNode child)
    {
        child.Parent = this;
        Children!.Add(child);
    }

    public override string ToString() => $"{Name} ({Size:N0} bytes)";
}
