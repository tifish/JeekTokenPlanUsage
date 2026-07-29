namespace JeekTokenPlanUsage;

/// Build / instance identity for the MCP endpoints. Debug builds suffix the
/// pipe names with a hash of the executable directory so parallel worktree
/// instances stay isolated; Release registers the bare names.
internal static class AppInstance
{
    public static bool IsDebugBuild { get; } =
#if DEBUG
        true;
#else
        false;
#endif

    public static string DebugMcpPipeName { get; } =
        McpPipeNames.Debug(IsDebugBuild ? McpPipeNames.InstanceId(AppContext.BaseDirectory) : null);

    public static string ProductMcpPipeName { get; } =
        McpPipeNames.Product(IsDebugBuild ? McpPipeNames.InstanceId(AppContext.BaseDirectory) : null);
}
