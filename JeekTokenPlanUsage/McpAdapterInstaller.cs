namespace JeekTokenPlanUsage;

internal static class McpAdapterInstaller
{
    internal const string AdapterName = "JeekTokenPlanUsageMcp.exe";
    internal static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekTokenPlanUsage", "Mcp");

    public static void Start() => new Thread(() =>
    {
        try
        {
            // Serialize installations from concurrent worktrees, on this background thread only.
            using var mutex = new Mutex(false, @"Local\JeekTokenPlanUsage.Mcp.Install");
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                return;
            try { Install(AppContext.BaseDirectory, InstallDirectory); }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex) { Log.Warn($"MCP adapter installation failed: {ex.Message}"); }
    }) { IsBackground = true, Name = "MCP adapter installer" }.Start();

    internal static void Install(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (string old in Directory.EnumerateFiles(targetDirectory, AdapterName + ".old.*"))
        {
            try { File.Delete(old); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var source = new FileInfo(Path.Combine(sourceDirectory, AdapterName));
        var target = new FileInfo(Path.Combine(targetDirectory, AdapterName));
        if (!source.Exists)
            return;
        if (target.Exists && source.Length == target.Length
            && source.LastWriteTimeUtc == target.LastWriteTimeUtc)
            return;

        // Prepare the complete replacement before moving the live entry point.
        string pending = target.FullName + ".new." + Guid.NewGuid().ToString("N");
        string? backup = null;
        try
        {
            source.CopyTo(pending);
            File.SetLastWriteTimeUtc(pending, source.LastWriteTimeUtc);
            if (target.Exists)
            {
                backup = target.FullName + ".old." + Guid.NewGuid().ToString("N");
                File.Move(target.FullName, backup);
            }
            File.Move(pending, target.FullName);
        }
        catch
        {
            if (backup is not null && !File.Exists(target.FullName))
                File.Move(backup, target.FullName);
            throw;
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
        }
    }
}
