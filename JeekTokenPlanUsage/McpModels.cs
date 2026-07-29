namespace JeekTokenPlanUsage;

/// App-side contract the MCP servers call into. TrayApplicationContext
/// implements it and marshals to the UI thread internally, so tool handlers
/// may call these from any thread.
internal interface IMcpUsageSource
{
    Task<McpUsageState> GetUsageAsync(string? provider, bool refresh, CancellationToken ct);
    Task<McpUiState> GetUiStateAsync(CancellationToken ct);
    Task<McpUiActionResult> InvokeUiActionAsync(McpUiActionRequest request, CancellationToken ct);
}

internal sealed record McpUsageState(
    DateTimeOffset GeneratedAt,
    bool Paused,
    IReadOnlyList<McpProviderState> Providers);

internal sealed record McpProviderState(
    string Id,
    string Name,
    bool Enabled,
    DateTimeOffset? LastPollAt,
    DateTimeOffset? Timestamp,
    string? Error,
    string? ErrorKind,
    IReadOnlyList<McpUsageWindow> Windows);

internal sealed record McpUsageWindow(
    string Id,
    string Label,
    double? Utilization,
    DateTimeOffset? ResetsAt);

internal sealed record McpUiState(
    DateTimeOffset GeneratedAt,
    bool DetailsVisible,
    bool AnchorVisible,
    string LogPath,
    McpUiSettings Settings,
    McpUiAllowedValues AllowedValues);

internal sealed record McpUiSettings(
    bool Paused,
    bool RunAtStartup,
    bool ShowClaude,
    bool ShowCodex,
    bool ShowCursor,
    bool ShowGrok,
    string IconMode,
    int PollMinutes,
    string Language,
    bool EnableThresholdNotifications,
    bool ShowTaskbarWidget,
    int TaskbarWidgetOffset,
    bool AutoUpdate,
    bool DisableMirrorDownload,
    string ProxyMode,
    string ProxyProtocol,
    string ProxyHost,
    int ProxyPort,
    string StorageMode,
    string CustomStorageRoot,
    string RoamingConfigDirectory,
    string RoamingSettingsPath);

internal sealed record McpUiAllowedValues(
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> IconModes,
    IReadOnlyList<int> PollMinutes,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> ProxyModes,
    IReadOnlyList<string> ProxyProtocols,
    IReadOnlyList<string> StorageModes,
    IReadOnlyList<string> Actions);

internal sealed record McpUiActionRequest(
    string Action,
    string? Provider,
    bool? Paused,
    bool? Enabled,
    bool? Visible,
    string? Mode,
    int? Minutes,
    string? Language,
    int? Offset,
    string? Protocol,
    string? Host,
    int? Port,
    string? CustomRoot,
    bool? AllowUpdateLaunch);

internal sealed record McpUiActionResult(
    DateTimeOffset GeneratedAt,
    string Action,
    bool Succeeded,
    string Message,
    McpUiState State);
