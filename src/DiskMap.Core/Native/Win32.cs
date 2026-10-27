using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DiskMap.Core.Native;

internal static partial class Kernel32
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;
    public const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
    public const uint INVALID_FILE_SIZE = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuf, int inSize, void* outBuf, int outSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string dir, out ulong freeToCaller, out ulong total, out ulong totalFree);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetCompressedFileSize(string path, out uint high);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MoveFileEx(string existing, string? target, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetVolumeInformation(string root, char* volumeName, int volumeNameSize, out uint serial, out uint maxComponent, out uint fsFlags, char* fsName, int fsNameSize);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NtfsVolumeData
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}

public static class Volume
{
    public static bool TryGetFreeSpace(string root, out long free, out long total)
    {
        if (Kernel32.GetDiskFreeSpaceEx(root, out _, out var t, out var f))
        {
            free = (long)f;
            total = (long)t;
            return true;
        }
        free = total = 0;
        return false;
    }

    public static long ClusterSize(string root)
    {
        return Kernel32.GetDiskFreeSpace(root, out var spc, out var bps, out _, out _) ? (long)spc * bps : 4096;
    }

    public static unsafe string FileSystem(string root)
    {
        char* name = stackalloc char[64];
        return Kernel32.GetVolumeInformation(root, null, 0, out _, out _, out _, name, 64)
            ? new string(name)
            : "";
    }

    /// <summary>Allocated size of one file, honouring compression, sparseness and cloud placeholders.</summary>
    public static long CompressedSize(string path, long fallback)
    {
        uint low = Kernel32.GetCompressedFileSize(path, out var high);
        if (low == Kernel32.INVALID_FILE_SIZE && Marshal.GetLastPInvokeError() != 0) return fallback;
        return ((long)high << 32) | low;
    }
}

public static class Elevation
{
    private static readonly Lazy<bool> s_isElevated = new(() =>
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public static bool IsElevated => s_isElevated.Value;
}
