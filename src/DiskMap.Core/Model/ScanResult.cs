namespace DiskMap.Core.Model;

public enum ScanMethod
{
    /// <summary>Read the NTFS master file table directly (needs admin rights).</summary>
    MasterFileTable,
    /// <summary>Walked the folders with the normal file APIs.</summary>
    FolderWalk,
}

public sealed class ScanResult
{
    public required DiskNode Root { get; init; }
    public required string DriveRoot { get; init; }
    public required ScanMethod Method { get; init; }
    public required long TotalBytes { get; init; }
    public long FreeBytes { get; set; }
    public required TimeSpan Duration { get; init; }
    public required DateTime ScannedAt { get; init; }
    public long ItemCount { get; init; }
    public int UnreadableFolders { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public long UsedBytes => TotalBytes - FreeBytes;

    public void RefreshFreeSpace()
    {
        if (Native.Volume.TryGetFreeSpace(DriveRoot, out var free, out _)) FreeBytes = free;
    }
}

public readonly record struct ScanProgress(long Items, long Bytes, string? CurrentPath, double Fraction);
