using System.Security.Cryptography;
using System.Text;

namespace JeekTokenPlanUsage;

/// Single source of truth for the MCP named-pipe names. The stdio adapter
/// (Tools\JeekTokenPlanUsageMcp) compiles this exact file via Compile Include,
/// so the app and the adapter can never disagree on a name. Keep this file free
/// of any other project dependency.
public static class McpPipeNames
{
    public const string ProductBase = "JeekTokenPlanUsage.Mcp";
    public const string DebugBase = "JeekTokenPlanUsage.Mcp.Debug";

    /// Stable 12-hex identity of an installation, hashed from its executable
    /// directory. The adapter sits beside the app, derives the same id from its
    /// own folder, and therefore only ever reaches the instance it shipped with.
    public static string InstanceId(string executableDirectory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Normalize(executableDirectory))))[..12].ToLowerInvariant();

    public static string Product(string? instanceId) => Compose(ProductBase, instanceId);

    public static string Debug(string? instanceId) => Compose(DebugBase, instanceId);

    public static string Resolve(string surface, string? instanceId) =>
        surface.Equals("debug", StringComparison.OrdinalIgnoreCase)
            ? Debug(instanceId)
            : Product(instanceId);

    /// Release is single-instance and registers the bare base name so client
    /// configs can hard-code it; Debug suffixes the folder hash so parallel
    /// worktrees never answer each other.
    private static string Compose(string baseName, string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) || instanceId == "release"
            ? baseName
            : $"{baseName}.{instanceId.Trim()}";

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
}
