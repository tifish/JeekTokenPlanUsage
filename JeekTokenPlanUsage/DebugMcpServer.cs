using JeekTools;

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
