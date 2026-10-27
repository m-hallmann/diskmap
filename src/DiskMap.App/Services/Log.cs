using DiskMap.Core.Settings;

namespace DiskMap.Services;

internal static class Log
{
    private static readonly object s_lock = new();

    public static void Write(string message)
    {
        try
        {
            lock (s_lock)
            {
                Directory.CreateDirectory(AppPaths.DataFolder);
                var file = Path.Combine(AppPaths.DataFolder, "diskmap.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1 << 20) File.Delete(file);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
