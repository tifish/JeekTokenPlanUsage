using JeekTokenPlanUsage.Resources;

namespace JeekTokenPlanUsage;

/// Formatting helpers shared by the tray tooltip and the details popup so both
/// surfaces show the same reset/remaining string for any given metric.
internal static class UsageFormatting
{
    public static string FormatReset(DateTimeOffset? reset, bool longDate)
    {
        if (reset is null)
            return "?";
        DateTimeOffset local = reset.Value.ToLocalTime();
        string absolute = longDate ? local.ToString("MM-dd HH:mm") : local.ToString("HH:mm");
        return string.Format(
            Strings.Tray_ResetFormat,
            FormatRemaining(reset.Value - DateTimeOffset.Now),
            absolute);
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "0m";
        int days = remaining.Days;
        int hours = remaining.Hours;
        int minutes = remaining.Minutes;
        if (days > 0)
            return $"{days}d {hours}h";
        if (hours > 0)
            return $"{hours}h {minutes}m";
        return $"{Math.Max(1, minutes)}m";
    }
}
