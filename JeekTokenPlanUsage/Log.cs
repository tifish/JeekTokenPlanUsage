using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekTokenPlanUsage;

/// App-wide logging facade over JeekTools.LogManager (ZLogger rolling files).
/// Preserves the Info/Warn/Error(string) surface the codebase already uses so
/// call sites stay terse while everything flows through the shared logger
/// configured in Program.Main.
internal static class Log
{
    private static readonly ILogger Logger = LogManager.CreateLogger("App");

    public static void Info(string message) => Logger.ZLogInformation($"{message}");

    public static void Warn(string message) => Logger.ZLogWarning($"{message}");

    public static void Error(string message) => Logger.ZLogError($"{message}");

    /// Path to the current log file, for the tray "open log" action. Prefers the
    /// stable alias; falls back to the timestamped rolling file before the alias
    /// hardlink has been created.
    public static string FilePath =>
        !string.IsNullOrEmpty(LogManager.CurrentLogFile)
            ? LogManager.CurrentLogFile
            : LogManager.CurrentRollingLogFile;
}
