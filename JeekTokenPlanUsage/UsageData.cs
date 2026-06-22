namespace JeekTokenPlanUsage;

/// A single usage window (e.g. 5-hour or weekly).
public sealed record UsageMetric(double Utilization, DateTimeOffset? ResetsAt);

public enum UsageErrorKind
{
    Auth,
}

/// One provider's full snapshot: a 5-hour window and a weekly window.
public sealed class UsageSnapshot
{
    public UsageMetric? FiveHour { get; init; }
    public UsageMetric? Weekly { get; init; }

    /// Non-null when the latest refresh failed; describes why.
    public string? Error { get; init; }
    public UsageErrorKind? ErrorKind { get; init; }

    /// When this snapshot's underlying data was produced (UTC).
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public static UsageSnapshot FromError(string message, UsageErrorKind? kind = null) => new() { Error = message, ErrorKind = kind };
}

public interface IUsageProvider
{
    string Name { get; }
    Task<UsageSnapshot> GetUsageAsync(CancellationToken ct);
}
