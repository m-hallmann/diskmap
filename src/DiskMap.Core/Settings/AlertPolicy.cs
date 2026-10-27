namespace DiskMap.Core.Settings;

/// <summary>Decides when the tray helper should show a low-space notification.</summary>
public static class AlertPolicy
{
    private const long FiveGb = 5L << 30;
    private const long TenGb = 10L << 30;

    public static bool ShouldAlert(DriveAlert alert, DriveAlertState state, long free, long total, RemindPolicy remind,
        DateTime now, DateTime? snoozedUntil)
    {
        bool below = free < alert.ThresholdBytes(total);
        bool wasBelow = state.Below;
        state.Below = below;
        if (!alert.Enabled || !below) return false;
        if (snoozedUntil is { } until && now < until) return false;
        if (!wasBelow || state.LastAlertAt == null) return true;

        long lostSinceAlert = state.LastAlertFree - free;
        var sinceAlert = now - state.LastAlertAt.Value;
        return remind switch
        {
            RemindPolicy.AfterFiveGbOrDaily => lostSinceAlert >= FiveGb || sinceAlert >= TimeSpan.FromDays(1),
            RemindPolicy.AfterTenGb => lostSinceAlert >= TenGb,
            RemindPolicy.Daily => sinceAlert >= TimeSpan.FromDays(1),
            _ => false,
        };
    }

    public static void MarkAlerted(DriveAlertState state, long free, DateTime now)
    {
        state.LastAlertFree = free;
        state.LastAlertAt = now;
    }
}
