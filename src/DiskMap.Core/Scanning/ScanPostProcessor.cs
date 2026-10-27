using DiskMap.Core.Games;
using DiskMap.Core.Model;

namespace DiskMap.Core.Scanning;

/// <summary>Classifies, protects, labels and aggregates a freshly scanned tree.</summary>
public static class ScanPostProcessor
{
    private enum Zone : byte { None, System, Games, Caches, Apps, Downloads }

    private readonly record struct Context(Zone Zone, bool Protected, bool AppZone, bool Downloads);

    private static readonly HashSet<string> s_systemRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "System Volume Information", "Recovery", "$WinREAgent", "$SysReset", "$Windows.~BT",
        "$Windows.~WS", "Config.Msi", "Boot", "EFI",
    };

    private static readonly HashSet<string> s_systemRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log", "DumpStack.log.tmp", "bootmgr", "BOOTNXT",
    };

    private static readonly HashSet<string> s_cacheFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Caches", "Code Cache", "GPUCache", "ShaderCache", "DXCache", "GLCache", "D3DSCache", "shadercache",
        "INetCache", "CacheStorage", "npm-cache", "node_modules", "__pycache__", ".cache", "CrashDumps", "WER",
        "downloading", "Temp", "tmp", "Squirrel.old", "LocalCache", "cache2", "GrShaderCache", "DawnCache",
    };

    public static void Run(DiskNode root, long totalBytes, long freeBytes, CancellationToken ct, GameCatalog? catalog = null)
    {
        var games = catalog ?? GameCatalog.Load();
        Classify(root, root.Name.TrimEnd('\\'), new Context(Zone.None, false, false, false), games, 0, ct);
        AggregateSubtree(root);
        AddUnaccounted(root, totalBytes - freeBytes);
    }

    private static void Classify(DiskNode dir, string path, Context ctx, GameCatalog games, int depth, CancellationToken ct)
    {
        if ((depth & 3) == 0) ct.ThrowIfCancellationRequested();
        if (ctx.Protected || dir.IsSynthetic && dir.IsProtected) dir.Flags |= NodeFlags.Protected;
        dir.Category = ZoneCategory(ctx.Zone, Category.Other);

        foreach (var child in dir.Children!)
        {
            if (child.IsDirectory)
            {
                string childPath = child.IsSynthetic ? path : path + "\\" + child.Name;
                var childCtx = ChildContext(dir, child, childPath, ctx, depth, games);
                Classify(child, childPath, childCtx, games, depth + 1, ct);
            }
            else
            {
                ClassifyFile(child, ctx, depth);
            }
        }
    }

    /// <summary>Folders inside a user profile that must stay, even though their contents can go.</summary>
    private static readonly HashSet<string> s_profileFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music", "OneDrive", "Favorites",
        "Contacts", "Links", "Saved Games", "Searches", "3D Objects",
    };

    private static Context ChildContext(DiskNode parent, DiskNode child, string childPath, Context ctx, int depth, GameCatalog games)
    {
        string name = child.Name;
        if (child.IsSynthetic) return ctx with { Zone = Zone.System, Protected = true };

        // Structural folders can be opened and their contents deleted, but never deleted as a whole.
        bool underUsers = string.Equals(parent.Name, "Users", StringComparison.OrdinalIgnoreCase) && depth == 1;
        bool inProfile = depth == 2 && string.Equals(parent.Parent?.Name, "Users", StringComparison.OrdinalIgnoreCase);
        bool inAppData = depth == 3 && string.Equals(parent.Name, "AppData", StringComparison.OrdinalIgnoreCase);
        if ((depth == 0 && (name.Equals("Users", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase))) ||
            underUsers || (inProfile && s_profileFolders.Contains(name)) || inAppData)
        {
            child.Flags |= NodeFlags.NoDelete;
        }
        if (depth == 1 && name.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) &&
            parent.Name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase))
        {
            return ctx with { Zone = Zone.System, Protected = true };
        }

        if (depth == 0)
        {
            if (s_systemRootFolders.Contains(name)) return ctx with { Zone = Zone.System, Protected = true };
            if (name.Equals("Windows.old", StringComparison.OrdinalIgnoreCase))
            {
                child.Flags |= NodeFlags.NoDelete;
                return ctx with { Zone = Zone.System, Protected = true };
            }
            if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
            {
                child.Flags |= NodeFlags.NoDelete;
                return ctx with { Zone = Zone.Caches, Protected = false };
            }
            if (name.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ProgramData", StringComparison.OrdinalIgnoreCase))
            {
                return ctx with { AppZone = true };
            }
            if (name.Equals("XboxGames", StringComparison.OrdinalIgnoreCase) || name.Equals("Riot Games", StringComparison.OrdinalIgnoreCase))
            {
                return ctx with { Zone = Zone.Games };
            }
        }

        if (ctx.Zone == Zone.System) return ctx;

        if (games.TryGet(childPath, out var game))
        {
            child.Flags |= NodeFlags.Game;
            child.Label = game.Title;
            child.Tag = game;
            return ctx with { Zone = Zone.Games };
        }

        if (parent.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Equals("common", StringComparison.OrdinalIgnoreCase) || name.Equals("workshop", StringComparison.OrdinalIgnoreCase))
                return ctx with { Zone = Zone.Games };
            if (name.Equals("shadercache", StringComparison.OrdinalIgnoreCase) || name.Equals("downloading", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("temp", StringComparison.OrdinalIgnoreCase))
                return ctx with { Zone = Zone.Caches };
        }

        if (name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) && depth == 1)
            return ctx with { Zone = Zone.Apps, Protected = true };

        if (depth == 2 && name.Equals("Downloads", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parent.Parent?.Name, "Users", StringComparison.OrdinalIgnoreCase))
            return ctx with { Downloads = true };

        if (name.Equals("Programs", StringComparison.OrdinalIgnoreCase) && parent.Name.Equals("Local", StringComparison.OrdinalIgnoreCase))
            return ctx with { AppZone = true };

        if (ctx.Zone != Zone.Games && s_cacheFolders.Contains(name))
        {
            // "Temp" only counts as a cache inside AppData; elsewhere it may hold real work.
            if ((name.Equals("Temp", StringComparison.OrdinalIgnoreCase) || name.Equals("tmp", StringComparison.OrdinalIgnoreCase)) &&
                !childPath.Contains("\\AppData\\", StringComparison.OrdinalIgnoreCase))
            {
                return ctx;
            }
            if (name.Equals("downloading", StringComparison.OrdinalIgnoreCase) && !parent.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return ctx;
            }
            return ctx with { Zone = Zone.Caches };
        }
        return ctx;
    }

    private static void ClassifyFile(DiskNode file, Context ctx, int depth)
    {
        if (depth == 0 && s_systemRootFiles.Contains(file.Name))
        {
            file.Flags |= NodeFlags.Protected;
            file.Category = Category.System;
            return;
        }
        if (ctx.Protected) file.Flags |= NodeFlags.Protected;
        var byExt = Categories.FromExtension(file.Name);
        file.Category = ctx.Zone switch
        {
            Zone.System => Category.System,
            Zone.Games => Category.Games,
            Zone.Caches => Category.Caches,
            _ => byExt switch
            {
                Category.Other or Category.Apps when ctx.AppZone => Category.Apps,
                Category.Apps when ctx.Downloads && Categories.IsExecutableOrInstaller(file.Name) => Category.Archives,
                _ => byExt,
            },
        };
    }

    private static Category ZoneCategory(Zone zone, Category fallback) => zone switch
    {
        Zone.System => Category.System,
        Zone.Games => Category.Games,
        Zone.Caches => Category.Caches,
        Zone.Apps => Category.Apps,
        _ => fallback,
    };

    /// <summary>Sums sizes, counts and categories bottom-up and sorts children largest first.</summary>
    public static void AggregateSubtree(DiskNode node)
    {
        if (!node.IsDirectory) return;
        long size = node.Size, logical = 0, files = 0, linked = node.LinkedElsewhere;
        var cats = new long[Categories.Count];
        cats[(int)node.Category] += node.Size;
        DateTime newest = node.Modified;
        foreach (var child in node.Children!)
        {
            AggregateSubtree(child);
            size += child.Size;
            logical += child.Logical;
            files += child.FileCount;
            linked += child.LinkedElsewhere;
            if (child.CategorySizes != null)
            {
                for (int i = 0; i < cats.Length; i++) cats[i] += child.CategorySizes[i];
            }
            else
            {
                cats[(int)child.Category] += child.Size;
            }
            if (child.Modified > newest) newest = child.Modified;
        }
        node.Size = size;
        node.Logical = logical;
        node.FileCount = files;
        node.LinkedElsewhere = linked;
        node.CategorySizes = cats;
        node.Modified = newest;
        node.Category = Dominant(cats, node.Category);
        node.Children!.Sort(static (a, b) => b.Size.CompareTo(a.Size));
    }

    internal static Category Dominant(long[] cats, Category fallback)
    {
        int best = -1;
        long bestSize = 0;
        for (int i = 0; i < cats.Length; i++)
        {
            if (cats[i] > bestSize)
            {
                bestSize = cats[i];
                best = i;
            }
        }
        return best < 0 ? fallback : (Category)best;
    }

    private static void AddUnaccounted(DiskNode root, long usedBytes)
    {
        long gap = usedBytes - root.Size;
        if (gap <= Math.Max(512L << 20, usedBytes / 100)) return;
        var node = new DiskNode("Hidden from the scan", NodeFlags.Synthetic | NodeFlags.Protected)
        {
            Size = gap,
            Category = Category.System,
        };
        root.AddChild(node);
        root.Size += gap;
        root.CategorySizes![(int)Category.System] += gap;
        root.Children!.Sort(static (a, b) => b.Size.CompareTo(a.Size));
    }
}
