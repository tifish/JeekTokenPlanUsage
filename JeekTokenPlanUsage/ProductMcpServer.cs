using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using JeekTools;

namespace JeekTokenPlanUsage;

/// Product MCP surface: exposes the app's features (usage snapshots and
/// tray-menu-equivalent actions) to a user's agent over a named pipe. Listens
/// in every build. Kept strictly separate from the debug surface - it carries
/// no object graph, so nothing here can reach process internals.
internal static class ProductMcpServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static McpHost? _host;
    private static IMcpUsageSource? _usageSource;

    public static void Start(IMcpUsageSource usageSource)
    {
        if (_host is not null)
            return;

        _usageSource = usageSource;
        var host = new McpHost(new McpHostOptions
        {
            ServerName = "jeek-token-plan-usage",
            ServerTitle = "JeekTokenPlanUsage",
            Graph = new ObjectGraph(new ObjectGraphOptions
            {
                // No object roots on the product surface; the contract never
                // advertises get_value / set_value / invoke either.
                ResolveRoot = _ => throw new InvalidOperationException(
                    "This surface exposes app features only; internals live on the debug surface."),
                RootNamesHelp = "(none)",
            }),
            GetVersion = () => $"{AutoUpdate.LocalBuild}",
            PipeName = AppInstance.ProductMcpPipeName,
            DefaultPort = 0,
            Describe = () =>
                "JeekTokenPlanUsage product MCP: token plan usage snapshots for Claude, "
                + "Codex, Cursor, and Grok, plus tray-menu-equivalent UI actions.",
            ToolListProvider = ProductMcpContract.BuildToolList,
        });

        host.AddTool("get_usage", args => BuildUsageToolResultAsync(args, refreshDefault: false));
        host.AddTool("refresh_usage", args => BuildUsageToolResultAsync(args, refreshDefault: true));
        host.AddTool("get_ui_state", _ => BuildUiStateToolResultAsync());
        host.AddTool("ui_action", BuildUiActionToolResultAsync);

        host.Start();
        _host = host;
        Log.Info($@"Product MCP listening on \\.\pipe\{host.PipeName}");
    }

    public static void Stop()
    {
        _host?.Stop();
        _host = null;
        _usageSource = null;
    }

    private static IMcpUsageSource Source =>
        _usageSource ?? throw new InvalidOperationException("The app is shutting down.");

    private static async Task<JsonObject> BuildUsageToolResultAsync(JsonObject args, bool refreshDefault)
    {
        string? provider = ReadString(args, "provider");
        bool refresh = refreshDefault || (ReadBool(args, "refresh") ?? false);
        McpUsageState state = await Source.GetUsageAsync(provider, refresh, CancellationToken.None);
        return StructuredResult(FormatUsageText(state), state);
    }

    private static async Task<JsonObject> BuildUiStateToolResultAsync()
    {
        McpUiState state = await Source.GetUiStateAsync(CancellationToken.None);
        return StructuredResult(FormatUiStateText(state), state);
    }

    private static async Task<JsonObject> BuildUiActionToolResultAsync(JsonObject args)
    {
        var request = new McpUiActionRequest(
            Action: ReadString(args, "action") ?? throw new ArgumentException("Missing action."),
            Provider: ReadString(args, "provider"),
            Paused: ReadBool(args, "paused"),
            Enabled: ReadBool(args, "enabled"),
            Visible: ReadBool(args, "visible"),
            Mode: ReadString(args, "mode"),
            Minutes: ReadInt(args, "minutes"),
            Language: ReadString(args, "language"),
            Offset: ReadInt(args, "offset"),
            Protocol: ReadString(args, "protocol"),
            Host: ReadString(args, "host"),
            Port: ReadInt(args, "port"),
            CustomRoot: ReadString(args, "customRoot"),
            AllowUpdateLaunch: ReadBool(args, "allowUpdateLaunch"));

        McpUiActionResult result = await Source.InvokeUiActionAsync(request, CancellationToken.None);
        JsonObject response = StructuredResult(FormatUiActionText(result), result);
        if (!result.Succeeded)
            response["isError"] = true;
        return response;
    }

    private static JsonObject StructuredResult<T>(string text, T structured) => new()
    {
        ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = text,
        }),
        ["structuredContent"] = JsonSerializer.SerializeToNode(structured, JsonOptions)!,
    };

    private static string FormatUsageText(McpUsageState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"generatedAt: {state.GeneratedAt:O}");
        sb.AppendLine($"paused: {state.Paused.ToString().ToLowerInvariant()}");
        foreach (McpProviderState provider in state.Providers)
        {
            string status = provider.Error is null ? "ok" : $"error: {provider.Error}";
            sb.AppendLine(
                $"{provider.Name} ({provider.Id}, enabled={provider.Enabled.ToString().ToLowerInvariant()}): {status}");
            foreach (McpUsageWindow window in provider.Windows)
            {
                string usage = window.Utilization is null ? "n/a" : $"{window.Utilization:0.#}%";
                string reset = window.ResetsAt is null ? "n/a" : window.ResetsAt.Value.ToString("O");
                sb.AppendLine($"  - {window.Label}: {usage}, resetsAt={reset}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatUiStateText(McpUiState state)
    {
        McpUiSettings s = state.Settings;
        return string.Join(Environment.NewLine, new[]
        {
            $"generatedAt: {state.GeneratedAt:O}",
            $"paused: {s.Paused.ToString().ToLowerInvariant()}",
            $"detailsVisible: {state.DetailsVisible.ToString().ToLowerInvariant()}",
            $"providers: claude={s.ShowClaude.ToString().ToLowerInvariant()}, codex={s.ShowCodex.ToString().ToLowerInvariant()}, cursor={s.ShowCursor.ToString().ToLowerInvariant()}, grok={s.ShowGrok.ToString().ToLowerInvariant()}",
            $"iconMode: {s.IconMode}",
            $"pollMinutes: {s.PollMinutes}",
            $"language: {(string.IsNullOrEmpty(s.Language) ? "system" : s.Language)}",
            $"notifications: {s.EnableThresholdNotifications.ToString().ToLowerInvariant()}",
            $"taskbarWidget: {s.ShowTaskbarWidget.ToString().ToLowerInvariant()}, offset={s.TaskbarWidgetOffset}",
            $"startup: {s.RunAtStartup.ToString().ToLowerInvariant()}",
            $"autoUpdate: {s.AutoUpdate.ToString().ToLowerInvariant()}",
            $"proxy: {s.ProxyMode} {s.ProxyProtocol}://{s.ProxyHost}:{s.ProxyPort}",
            $"storage: {s.StorageMode}, config={s.RoamingConfigDirectory}",
            $"logPath: {state.LogPath}",
        });
    }

    private static string FormatUiActionText(McpUiActionResult result) =>
        $"{result.Action}: {(result.Succeeded ? "ok" : "failed")} - {result.Message}";

    private static string? ReadString(JsonObject args, string property) =>
        args.TryGetPropertyValue(property, out JsonNode? node) && node is not null
            ? node.GetValue<string>()
            : null;

    private static bool? ReadBool(JsonObject args, string property) =>
        args.TryGetPropertyValue(property, out JsonNode? node) && node is not null
            ? node.GetValue<bool>()
            : null;

    private static int? ReadInt(JsonObject args, string property) =>
        args.TryGetPropertyValue(property, out JsonNode? node) && node is not null
            ? node.GetValue<int>()
            : null;
}
