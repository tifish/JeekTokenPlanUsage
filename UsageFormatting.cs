using JeekTokenPlanUsage.Resources;

namespace JeekTokenPlanUsage;

/// Formatting helpers shared by the tray tooltip and the details popup so both
/// surfaces show the same reset/remaining string for any given metric.
internal static class UsageFormatting
{
    public readonly record struct ResetDisplay(string Remaining, string Absolute);

    public static ResetDisplay? FormatResetParts(DateTimeOffset? reset, bool longDate)
    {
        if (reset is null)
            return null;
        DateTimeOffset local = reset.Value.ToLocalTime();
        string absolute = longDate ? local.ToString("MM-dd HH:mm") : local.ToString("HH:mm");
        return new ResetDisplay(FormatRemaining(reset.Value - DateTimeOffset.Now), absolute);
    }

    public static string FormatReset(DateTimeOffset? reset, bool longDate)
    {
        if (FormatResetParts(reset, longDate) is not { } display)
            return "?";
        return string.Format(
            Strings.Tray_ResetFormat,
            display.Remaining,
            display.Absolute);
    }

    public static string FormatResetAligned(DateTimeOffset? reset, bool longDate, int remainingWidth)
    {
        if (FormatResetParts(reset, longDate) is not { } display)
            return "?";
        return string.Format(
            Strings.Tray_ResetFormat,
            display.Remaining.PadRight(remainingWidth),
            display.Absolute);
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

    /// Like FormatRemaining but shows only the largest non-zero unit (e.g. "2d",
    /// "5h", "45m"), trading precision for width — used by the taskbar widget.
    public static string FormatRemainingShort(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "0m";
        if (remaining.Days > 0)
            return $"{remaining.Days}d";
        if (remaining.Hours > 0)
            return $"{remaining.Hours}h";
        return $"{Math.Max(1, remaining.Minutes)}m";
    }
}
