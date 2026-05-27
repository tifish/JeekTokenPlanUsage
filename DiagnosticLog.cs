namespace JeekTokenPlanUsage;

internal static class DiagnosticLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    /// Absolute path of the current log file. Exposed so menu actions can open
    /// it; logging itself never throws if the file is missing.
    public static string FilePath { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JeekTokenPlanUsage.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                RotateIfNeeded();
                string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(FilePath, line);
            }
        }
        catch
        {
            // Diagnostics must never affect polling.
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
            {
                string oldPath = FilePath + ".old";
                if (File.Exists(oldPath))
                    File.Delete(oldPath);
                File.Move(FilePath, oldPath);
            }
        }
        catch
        {
            // Best effort rotation.
        }
    }
}
