using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DiskMap.Core.Native;

namespace DiskMap.Core.Actions;

public sealed record DeleteFailure(string Path, string Reason, IReadOnlyList<string> LockedBy);

public sealed class DeleteReport
{
    public List<string> Removed { get; } = new();
    public List<string> Partial { get; } = new();
    public ConcurrentBag<DeleteFailure> Failures { get; } = new();
    public bool Succeeded => Failures.IsEmpty;
}

public readonly record struct DeleteProgress(int ItemsDone, int ItemsTotal, string? Current);

/// <summary>Deletes files and folders permanently (fast, parallel) or to the Recycle Bin.</summary>
public static class DeleteService
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    public static Task<DeleteReport> DeleteAsync(IReadOnlyList<string> paths, bool permanently, IProgress<DeleteProgress>? progress, CancellationToken ct) =>
        Task.Run(() => permanently ? DeletePermanently(paths, progress, ct) : Recycle(paths, progress), ct);

    private static DeleteReport DeletePermanently(IReadOnlyList<string> paths, IProgress<DeleteProgress>? progress, CancellationToken ct)
    {
        var report = new DeleteReport();
        for (int i = 0; i < paths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = paths[i];
            progress?.Report(new DeleteProgress(i, paths.Count, path));
            int before = report.Failures.Count;
            try
            {
                var attrs = File.GetAttributes(path);
                if ((attrs & FileAttributes.Directory) != 0 && (attrs & FileAttributes.ReparsePoint) == 0) DeleteTree(path, report, ct);
                else DeleteFile(path, attrs, report);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Failures.Add(Fail(path, ex));
            }

            if (report.Failures.Count == before) report.Removed.Add(path);
            else if (Exists(path)) report.Partial.Add(path);
            else report.Removed.Add(path);
        }
        progress?.Report(new DeleteProgress(paths.Count, paths.Count, null));
        return report;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void DeleteTree(string root, DeleteReport report, CancellationToken ct)
    {
        // Collect folders without following junctions or symlinks, then delete files in parallel
        // and folders deepest first.
        var dirs = new List<string> { root };
        var files = new List<(string Path, FileAttributes Attrs)>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        for (int i = 0; i < dirs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in new DirectoryInfo(dirs[i]).EnumerateFileSystemInfos("*", options))
                {
                    var a = entry.Attributes;
                    if ((a & FileAttributes.Directory) != 0 && (a & FileAttributes.ReparsePoint) == 0) dirs.Add(entry.FullName);
                    else files.Add((entry.FullName, a));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Failures.Add(Fail(dirs[i], ex));
            }
        }

        Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Min(16, Environment.ProcessorCount * 2) },
            f => DeleteFile(f.Path, f.Attrs, report));

        for (int i = dirs.Count - 1; i >= 0; i--)
        {
            try
            {
                var d = new DirectoryInfo(dirs[i]);
                if ((d.Attributes & FileAttributes.ReadOnly) != 0) d.Attributes &= ~FileAttributes.ReadOnly;
                d.Delete(false);
            }
            catch (DirectoryNotFoundException) { }
            catch (IOException) when (Directory.EnumerateFileSystemEntries(dirs[i]).Any())
            {
                // Something inside could not be deleted; that failure is already reported.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report.Failures.Add(Fail(dirs[i], ex));
            }
        }
    }

    private static void DeleteFile(string path, FileAttributes attrs, DeleteReport report)
    {
        try
        {
            if ((attrs & FileAttributes.Directory) != 0)
            {
                Directory.Delete(path, false); // a junction or directory symlink: removes the link only
                return;
            }
            if ((attrs & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            File.Delete(path);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Failures.Add(Fail(path, ex));
        }
    }

    private static DeleteFailure Fail(string path, Exception ex)
    {
        bool locked = ex.HResult is SharingViolation or LockViolation;
        IReadOnlyList<string> lockers = locked ? RestartManager.WhoIsLocking(path) : [];
        string reason = locked ? "In use" : ex is UnauthorizedAccessException ? "Access denied" : ex.Message;
        return new DeleteFailure(path, reason, lockers);
    }

    /// <summary>Schedules leftover files and folders for deletion at the next restart (needs admin).</summary>
    public static int DeleteAtRestart(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    if (Kernel32.MoveFileEx(f, null, Kernel32.MOVEFILE_DELAY_UNTIL_REBOOT)) n++;
                }
                foreach (var d in Directory.EnumerateDirectories(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                             .OrderByDescending(d => d.Length))
                {
                    Kernel32.MoveFileEx(d, null, Kernel32.MOVEFILE_DELAY_UNTIL_REBOOT);
                }
            }
            if (Kernel32.MoveFileEx(p, null, Kernel32.MOVEFILE_DELAY_UNTIL_REBOOT)) n++;
        }
        return n;
    }

    private static DeleteReport Recycle(IReadOnlyList<string> paths, IProgress<DeleteProgress>? progress)
    {
        var report = new DeleteReport();
        progress?.Report(new DeleteProgress(0, paths.Count, null));
        var op = new Shell32.SHFILEOPSTRUCT
        {
            wFunc = Shell32.FO_DELETE,
            pFrom = string.Join('\0', paths) + "\0\0",
            fFlags = Shell32.FOF_ALLOWUNDO | Shell32.FOF_NOCONFIRMATION | Shell32.FOF_NOERRORUI | Shell32.FOF_SILENT,
        };
        int rc = Shell32.SHFileOperation(ref op);
        foreach (var p in paths)
        {
            if (Exists(p)) report.Failures.Add(new DeleteFailure(p, rc == 0 ? "Could not be recycled" : $"Error {rc}", []));
            else report.Removed.Add(p);
        }
        progress?.Report(new DeleteProgress(paths.Count, paths.Count, null));
        return report;
    }
}

internal static class Shell32
{
    public const uint FO_DELETE = 3;
    public const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
    public const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(nint hwnd, string? root, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
}

/// <summary>Asks the Restart Manager which programs have a file open.</summary>
internal static class RestartManager
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint handle, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint handle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint handle, uint files, string[] names, uint apps, nint procs, uint services, nint serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? info, ref uint reasons);

    public static IReadOnlyList<string> WhoIsLocking(string path)
    {
        try
        {
            if (RmStartSession(out var h, 0, Guid.NewGuid().ToString()) != 0) return [];
            try
            {
                if (RmRegisterResources(h, 1, [path], 0, 0, 0, 0) != 0) return [];
                uint count = 0, reasons = 0;
                int rc = RmGetList(h, out var needed, ref count, null, ref reasons);
                if (rc != 234 /* ERROR_MORE_DATA */ || needed == 0) return [];
                var info = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(h, out _, ref count, info, ref reasons) != 0) return [];
                return info.Take((int)count).Select(i => i.strAppName).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            }
            finally
            {
                RmEndSession(h);
            }
        }
        catch (DllNotFoundException)
        {
            return [];
        }
    }
}
