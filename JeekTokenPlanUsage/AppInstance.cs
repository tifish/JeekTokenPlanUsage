using System.Security.Cryptography;
using System.Text;

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

    public static string MutexName { get; } = @"Global\JeekTokenPlanUsage.SingleInstance"
        + (IsDebugBuild ? "." + McpPipeNames.InstanceId(AppContext.BaseDirectory) : "");

    // Debug instances must not take over the installed app's Shell icon GUIDs.
    public static Guid TrayGuid(Guid releaseGuid) => IsDebugBuild
        ? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(
            releaseGuid.ToString("D") + McpPipeNames.InstanceId(AppContext.BaseDirectory))).AsSpan(0, 16))
        : releaseGuid;

    public static string DebugMcpPipeName { get; } =
        McpPipeNames.Debug(IsDebugBuild ? McpPipeNames.InstanceId(AppContext.BaseDirectory) : null);

    public static string ProductMcpPipeName { get; } =
        McpPipeNames.Product(IsDebugBuild ? McpPipeNames.InstanceId(AppContext.BaseDirectory) : null);
}
