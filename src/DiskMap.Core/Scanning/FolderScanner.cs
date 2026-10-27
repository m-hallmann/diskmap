using System.Diagnostics;
using System.IO.Enumeration;
using DiskMap.Core.Model;
using DiskMap.Core.Native;

namespace DiskMap.Core.Scanning;

/// <summary>
/// Fallback scanner that walks folders in parallel with the normal file APIs.
/// Works on any file system and without admin rights, but cannot see hardlinks.
/// </summary>
public sealed class FolderScanner
{
    private const FileAttributes SpecialStorage =
        FileAttributes.Compressed | FileAttributes.SparseFile | FileAttributes.Offline |
        (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    private readonly record struct Entry(string Name, bool IsDirectory, long Length, DateTime Modified, FileAttributes Attributes);

    public ScanResult Scan(string driveRoot, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var root = new DiskNode(driveRoot, NodeFlags.Directory);
        long items = ScanInto(root, driveRoot, Volume.ClusterSize(driveRoot), progress, ct, out int unreadable);
        Volume.TryGetFreeSpace(driveRoot, out long free, out long total);
        ScanPostProcessor.Run(root, total, free, ct);
        progress?.Report(new ScanProgress(items, root.Size, null, 1));
        return new ScanResult
        {
            Root = root,
            DriveRoot = driveRoot,
            Method = ScanMethod.FolderWalk,
            TotalBytes = total,
            FreeBytes = free,
            Duration = watch.Elapsed,
            ScannedAt = DateTime.Now,
            ItemCount = items,
            UnreadableFolders = unreadable,
        };
    }

    /// <summary>Rescans one folder (after a partial delete) and returns a fresh, aggregated node.</summary>
    public static DiskNode Rescan(string path, string name, long clusterSize)
    {
        var node = new DiskNode(name, NodeFlags.Directory);
        ScanInto(node, path, clusterSize, null, CancellationToken.None, out _);
        ScanPostProcessor.AggregateSubtree(node);
        return node;
    }

    private static long ScanInto(DiskNode root, string rootPath, long cluster, IProgress<ScanProgress>? progress,
        CancellationToken ct, out int unreadable)
    {
        var queue = new System.Collections.Concurrent.BlockingCollection<(DiskNode Node, string Path)>();
        long items = 0, bytes = 0;
        int pending = 1, unreadableCount = 0;
        queue.Add((root, rootPath));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };
        long lastReport = 0;

        void Work()
        {
            foreach (var (dir, path) in queue.GetConsumingEnumerable(ct))
            {
                try
                {
                    var enumerable = new FileSystemEnumerable<Entry>(path,
                        (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length,
                            e.LastWriteTimeUtc.LocalDateTime, e.Attributes), options);
                    foreach (var e in enumerable)
                    {
                        bool reparse = (e.Attributes & FileAttributes.ReparsePoint) != 0;
                        if (e.IsDirectory)
                        {
                            var child = new DiskNode(e.Name, NodeFlags.Directory | (reparse ? NodeFlags.ReparsePoint : 0))
                            {
                                Modified = e.Modified,
                            };
                            dir.AddChild(child);
                            Interlocked.Increment(ref items);
                            if (!reparse)
                            {
                                Interlocked.Increment(ref pending);
                                queue.Add((child, Path.Join(path, e.Name)));
                            }
                        }
                        else
                        {
                            long alloc;
                            if (reparse) alloc = 0;
                            else if ((e.Attributes & SpecialStorage) != 0) alloc = Volume.CompressedSize(Path.Join(path, e.Name), e.Length);
                            else alloc = (e.Length + cluster - 1) / cluster * cluster;
                            var file = new DiskNode(e.Name, reparse ? NodeFlags.ReparsePoint : NodeFlags.None)
                            {
                                Size = alloc,
                                Logical = reparse ? 0 : e.Length,
                                FileCount = 1,
                                Modified = e.Modified,
                            };
                            dir.AddChild(file);
                            Interlocked.Increment(ref items);
                            Interlocked.Add(ref bytes, alloc);
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    dir.Flags |= NodeFlags.Unreadable;
                    Interlocked.Increment(ref unreadableCount);
                }

                long now = Environment.TickCount64;
                if (progress != null && now - Interlocked.Read(ref lastReport) > 100)
                {
                    Interlocked.Exchange(ref lastReport, now);
                    progress.Report(new ScanProgress(Interlocked.Read(ref items), Interlocked.Read(ref bytes), path, -1));
                }
                if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding();
            }
        }

        var threads = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 2, 16))
            .Select(_ => new Thread(() =>
            {
                try { Work(); }
                catch (OperationCanceledException) { }
            }) { IsBackground = true, Name = "diskmap scan" })
            .ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        ct.ThrowIfCancellationRequested();
        unreadable = unreadableCount;
        return items;
    }
}
