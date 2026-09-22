namespace JeekTokenPlanUsage;

/// Tracks threshold notifications independently for each provider window.
internal sealed class WindowThresholdState
{
    private static readonly int[] NotificationThresholds = { 80, 95 };
    public DateTimeOffset? LastSeenReset;
    public int LastNotifiedThreshold;

    public bool ShouldNotify(UsageMetric metric, bool enabled)
    {
        if (LastSeenReset != metric.ResetsAt)
        {
            LastSeenReset = metric.ResetsAt;
            LastNotifiedThreshold = 0;
        }

        // Exhausted windows need no further warning, including on startup or
        // when a provider changes its reported reset time while still at 100%.
        // Consume the thresholds to avoid a delayed alert if usage fluctuates.
        if (metric.Utilization >= 100)
        {
            LastNotifiedThreshold = NotificationThresholds[^1];
            return false;
        }

        if (!enabled)
            return false;

        int crossed = 0;
        foreach (int threshold in NotificationThresholds)
            if (metric.Utilization >= threshold)
                crossed = threshold;

        if (crossed <= LastNotifiedThreshold)
            return false;

        LastNotifiedThreshold = crossed;
        return true;
    }
}
