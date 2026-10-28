using System.Globalization;

namespace DiskMap.Core;

public static class Format
{
    private const double KB = 1024, MB = KB * 1024, GB = MB * 1024, TB = GB * 1024;

    /// <summary>Formats a byte count the way Explorer does (binary units, "GB").</summary>
    public static string Bytes(long bytes)
    {
        double b = Math.Abs((double)bytes);
        string s = b switch
        {
            >= TB => (b / TB).ToString(b / TB >= 100 ? "0" : "0.00", CultureInfo.CurrentCulture) + " TB",
            >= GB => (b / GB).ToString(b / GB >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " GB",
            >= MB => (b / MB).ToString(b / MB >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture) + " MB",
            >= KB => (b / KB).ToString("0", CultureInfo.CurrentCulture) + " KB",
            _ => b.ToString("0", CultureInfo.CurrentCulture) + " bytes",
        };
        return bytes < 0 ? "−" + s : s;
    }

    /// <summary>Signed change, e.g. "+18.6 GB" or "−5.5 GB".</summary>
    public static string Delta(long bytes) => (bytes >= 0 ? "+" : "−") + Bytes(Math.Abs(bytes));

    public static string Percent(double fraction) =>
        (fraction * 100).ToString(fraction < 0.1 ? "0.0" : "0", CultureInfo.CurrentCulture) + "%";

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Date(DateTime local)
    {
        if (local.Kind == DateTimeKind.Utc) local = local.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return "Today, " + local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1)) return "Yesterday, " + local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Year == today.Year) return local.ToString("MMM d", CultureInfo.CurrentCulture);
        return local.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    public static string Ago(TimeSpan span) => span.TotalDays switch
    {
        >= 2 => $"{(int)span.TotalDays} days ago",
        >= 1 => "1 day ago",
        _ when span.TotalHours >= 2 => $"{(int)span.TotalHours} hours ago",
        _ when span.TotalHours >= 1 => "1 hour ago",
        _ => "minutes ago",
    };
}
