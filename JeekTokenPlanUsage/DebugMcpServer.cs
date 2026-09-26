using JeekTools;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace JeekTokenPlanUsage;

/// Debug MCP surface: the McpHost standard tools over a live object graph so an
/// agent can read state, tweak settings, and call methods while debugging. The
/// code compiles in every configuration, but only Debug builds actually listen
/// (runtime gate rather than #if around the file, so Release builds can't rot).
/// Never merged with the product surface: `invoke` can call anything in the
/// process, which must stay unreachable from a user's agent.
internal static class DebugMcpServer
{
    private static readonly bool ListeningEnabled =
#if DEBUG
        true;
#else
        false;
#endif

    private static McpHost? _host;
    private static SynchronizationContext? _uiContext;

    public static void Start(TrayApplicationContext context, SynchronizationContext? uiContext)
    {
        if (_host is not null)
            return;

        _uiContext = uiContext;
        var graph = new ObjectGraph(new ObjectGraphOptions
        {
            ResolveRoot = name => name switch
            {
                "Context" => context,
                _ => throw new InvalidOperationException($"Unknown root '{name}'. Try: Context."),
            },
            RootNamesHelp = "Context",
            FindNamedChild = FindNamedChild,
        });

        var host = new McpHost(new McpHostOptions
        {
            ServerName = "jeek-token-plan-usage-debug",
            ServerTitle = "JeekTokenPlanUsage Debug Server",
            Graph = graph,
            GetVersion = () => $"{AutoUpdate.LocalBuild}",
            Enabled = ListeningEnabled,
            PipeName = AppInstance.DebugMcpPipeName,
            DefaultPort = 0,
            UiInvoker = InvokeOnUiAsync,
            Describe = () =>
                "JeekTokenPlanUsage debug server. Object-graph root: Context "
                + "(TrayApplicationContext; settings at Context._settings). "
                + $@"Pipe: \\.\pipe\{AppInstance.DebugMcpPipeName}.",
            ToolListProvider = DebugMcpContract.BuildToolList,
        });

        host.AddTool("probe_threshold_notifications", args =>
            Task.FromResult(ProbeThresholdNotifications(args)));
        host.AddTool("probe_dependencies", _ => Task.FromResult(ProbeDependencies()));
        host.AddTool("probe_adapter_installation", _ => Task.FromResult(ProbeAdapterInstallation()));

        host.Start();
        _host = host;
        if (ListeningEnabled)
            Log.Info($@"Debug MCP listening on \\.\pipe\{host.PipeName}");
    }

    public static void Stop()
    {
        _host?.Stop();
        _host = null;
        _uiContext = null;
    }

    private static JsonObject ProbeAdapterInstallation()
    {
        string root = Path.Combine(Path.GetTempPath(), "JeekTokenPlanUsageProbe-" + Guid.NewGuid().ToString("N"));
        try
        {
            string source = Path.Combine(root, "Source");
            string target = Path.Combine(root, "Target");
            Directory.CreateDirectory(source);
            string src = Path.Combine(source, McpAdapterInstaller.AdapterName);
            string dst = Path.Combine(target, McpAdapterInstaller.AdapterName);
            File.WriteAllText(src, "first");
            McpAdapterInstaller.Install(source, target);
            bool first = File.ReadAllText(dst) == "first";
            // Simulate an agent holding the old image open with rename sharing.
            using (var held = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                File.WriteAllText(src, "replacement");
                McpAdapterInstaller.Install(source, target);
            }
            bool replaced = File.ReadAllText(dst) == "replacement";
            File.WriteAllText(dst + ".old.stale1", "old");
            File.WriteAllText(dst + ".old.stale2", "old");
            McpAdapterInstaller.Install(source, target);
            bool cleaned = !Directory.EnumerateFiles(target, "*.old.*").Any();
            bool timestamp = File.GetLastWriteTimeUtc(src) == File.GetLastWriteTimeUtc(dst);
            var data = new JsonObject { ["firstInstall"] = first, ["lockedReplacement"] = replaced,
                ["cleanupWithoutUpdate"] = cleaned, ["timestampPreserved"] = timestamp };
            return new JsonObject { ["structuredContent"] = data,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = data.ToJsonString() }),
                ["isError"] = !(first && replaced && cleaned && timestamp) };
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Exercise the deployed managed/native SQLite pair with synthetic data.
    // Never open a provider's database or read credentials for this probe.
    private static JsonObject ProbeDependencies()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO ItemTable (key, value) VALUES ($key, $value)";
        command.Parameters.AddWithValue("$key", "debug-probe");
        command.Parameters.AddWithValue("$value", "SQLite 测试");
        command.ExecuteNonQuery();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = $key";
        bool roundTrip = Equals(command.ExecuteScalar(), "SQLite 测试");
        command.Parameters.Clear();
        command.CommandText = "SELECT sqlite_version()";
        string? sqliteVersion = command.ExecuteScalar()?.ToString();
        Log.Info($"Dependency probe: SQLite {sqliteVersion}, roundTrip={roundTrip}");
        var data = new JsonObject
        {
            ["sqliteVersion"] = sqliteVersion,
            ["managedSqliteVersion"] = typeof(SqliteConnection).Assembly.GetName().Version?.ToString(),
            ["roundTrip"] = roundTrip,
        };
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = data.ToJsonString(),
            }),
            ["structuredContent"] = data,
            ["isError"] = !roundTrip,
        };
    }

    // Runs the same decision code as the tray, but never touches live state,
    // settings, credentials, or the shell notification API.
    private static JsonObject ProbeThresholdNotifications(JsonObject args)
    {
        var samples = args["samples"] as JsonArray
            ?? throw new ArgumentException("Missing samples array.");
        var state = new WindowThresholdState();
        var results = new JsonArray();
        foreach (JsonNode? sample in samples)
        {
            double utilization = sample?["utilization"]?.GetValue<double>()
                ?? throw new ArgumentException("Missing utilization.");
            DateTimeOffset? resetsAt = sample?["resetsAt"]?.GetValue<DateTimeOffset>();
            bool enabled = sample?["enabled"]?.GetValue<bool>() ?? true;
            bool shouldNotify = state.ShouldNotify(new UsageMetric(utilization, resetsAt), enabled);
            results.Add(new JsonObject
            {
                ["shouldNotify"] = shouldNotify,
                ["lastNotifiedThreshold"] = state.LastNotifiedThreshold,
                ["cycleReset"] = state.LastSeenReset?.ToString("O"),
            });
        }
        var data = new JsonObject { ["results"] = results };
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = data.ToJsonString(),
            }),
            ["structuredContent"] = data,
        };
    }

    /// Tool work that touches UI state runs on the UI thread, with a timeout so
    /// a blocked message loop turns into a readable error instead of a hang.
    private static Task<object?> InvokeOnUiAsync(Func<object?> func)
    {
        SynchronizationContext? ui = _uiContext;
        if (ui is null)
            return Task.FromResult(func());

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// `#Name` path segments: depth-first search through WinForms child controls.
    private static object? FindNamedChild(object parent, string name)
    {
        if (parent is not Control control)
            return null;

        foreach (Control child in control.Controls)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                return child;
            object? found = FindNamedChild(child, name);
            if (found is not null)
                return found;
        }

        return null;
    }
}
