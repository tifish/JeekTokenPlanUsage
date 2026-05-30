using System.Diagnostics;
using System.Reflection;

namespace JeekTokenPlanUsage;

/// Tri-state outcome of an update check. Callers can distinguish a real "no
/// update" verdict from a check that didn't complete — avoiding the previous
/// bug where a network failure during a manual check was misreported as
/// "you're on the latest version".
public enum UpdateCheckOutcome
{
    /// Remote version is strictly greater than local — caller should apply.
    Available,
    /// Local & remote both read successfully and local >= remote.
    UpToDate,
    /// Check did not complete: network, parse, or local-version issue.
    /// See <see cref="AutoUpdate.FailureReason"/> for a short caller-facing
    /// description; the diagnostic log has the full detail.
    Failed,
}

/// Self-update flow. Version identity is the total git commit count on main —
/// a monotonic integer like 234 that's human-readable and zero-maintenance.
///
///   * Local count  — CI runs `dotnet publish /p:Version=&lt;count&gt;` which
///     stamps AssemblyVersion as &lt;count&gt;.0.0.0. Read via reflection.
///   * Remote count — a tiny `version.txt` artifact on the `latest_release`
///     release whose body is the same integer.
///
/// Update available iff remote &gt; local (strict). A force-push that rewinds
/// history would lower the remote count, but in that case we shouldn't
/// auto-update — the local build is "ahead" by design.
public static class AutoUpdate
{
    public const string ReleaseZipUrl =
        "https://github.com/tifish/JeekTokenPlanUsage/releases/download/latest_release/JeekTokenPlanUsage.zip";

    public const string VersionTxtUrl =
        "https://github.com/tifish/JeekTokenPlanUsage/releases/download/latest_release/version.txt";

    public const string RepoUrl = "https://github.com/tifish/JeekTokenPlanUsage";

    /// The local build identity (git commit count baked into AssemblyVersion.Major).
    /// 0 for a local dev build that didn't pass through CI. Shown in the About box.
    public static int LocalBuild => ReadLocalCommitCount();

    private const string UpdateScriptName = "AutoUpdate.ps1";

    public static string DownloadUrl { get; private set; } = "";
    public static int LocalCommitCount { get; private set; }
    public static int RemoteCommitCount { get; private set; }

    /// Short English description set whenever the most recent check returned
    /// <see cref="UpdateCheckOutcome.Failed"/>. Cleared on every check entry
    /// so callers reading it always see the current run's state. Detailed
    /// diagnostics still go to <see cref="DiagnosticLog"/>.
    public static string FailureReason { get; private set; } = "";

    public static async Task<UpdateCheckOutcome> HasUpdateAsync(bool disableMirror)
    {
        DownloadUrl = "";
        RemoteCommitCount = 0;
        FailureReason = "";
        LocalCommitCount = ReadLocalCommitCount();

        try
        {
            string versionUrl;
            if (disableMirror)
            {
                DownloadUrl = ReleaseZipUrl;
                versionUrl = VersionTxtUrl;
            }
            else
            {
                // Probe mirrors against the zip URL (the larger artifact); the
                // chosen mirror index is cached, so the second call below is
                // free and yields the version.txt URL on the same host.
                string zipMirror = await GitHubMirrors.GetFastestMirrorAsync(ReleaseZipUrl);
                if (string.IsNullOrEmpty(zipMirror))
                    return Fail("no reachable mirror");
                DownloadUrl = zipMirror;
                versionUrl = await GitHubMirrors.GetFastestMirrorAsync(VersionTxtUrl);
                if (string.IsNullOrEmpty(versionUrl))
                    versionUrl = VersionTxtUrl;
            }

            string? remote = await GitHubMirrors.DownloadTextAsync(versionUrl);
            if (string.IsNullOrWhiteSpace(remote))
                return Fail($"empty version.txt from {versionUrl}");

            if (!int.TryParse(remote.Trim(), out int remoteCount) || remoteCount <= 0)
                return Fail($"version.txt did not contain a positive integer: '{remote.Trim()}'");
            RemoteCommitCount = remoteCount;

            if (LocalCommitCount <= 0)
            {
                // Sentinel: csproj's <Version>0</Version> means a dev build that
                // didn't pass through CI's /p:Version=<count>. Refuse to overwrite
                // an unreleased working tree.
                return Fail("local version unavailable (dev build?)");
            }

            if (RemoteCommitCount > LocalCommitCount)
            {
                DiagnosticLog.Info(
                    $"AutoUpdate: local={LocalCommitCount} remote={RemoteCommitCount} — update available");
                return UpdateCheckOutcome.Available;
            }

            DiagnosticLog.Info(
                $"AutoUpdate: local={LocalCommitCount} remote={RemoteCommitCount} — up to date");
            return UpdateCheckOutcome.UpToDate;
        }
        catch (Exception ex)
        {
            return Fail($"exception: {ex.Message}");
        }
    }

    private static UpdateCheckOutcome Fail(string reason)
    {
        FailureReason = reason;
        DiagnosticLog.Warn($"AutoUpdate: {reason}");
        return UpdateCheckOutcome.Failed;
    }

    /// Launches AutoUpdate.ps1 with the chosen zip URL, then signals the
    /// app to exit so the script can replace the executable.
    public static bool LaunchUpdate()
    {
        try
        {
            if (string.IsNullOrEmpty(DownloadUrl))
                return false;

            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            string workDir = Path.GetDirectoryName(exePath)!;
            string scriptPath = Path.Combine(workDir, UpdateScriptName);
            if (!File.Exists(scriptPath))
            {
                DiagnosticLog.Error($"AutoUpdate: script not found at {scriptPath}");
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" \"{DownloadUrl}\"",
                WorkingDirectory = workDir,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
            });

            DiagnosticLog.Info("AutoUpdate: launched updater; exiting");
            Application.Exit();
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error($"AutoUpdate: failed to launch updater: {ex.Message}");
            return false;
        }
    }

    /// Reads the Major field of AssemblyVersion, which the CI workflow sets
    /// via `dotnet publish /p:Version=<git-commit-count>`. .NET expands the
    /// single integer into Major.0.0.0 so Version.Major is the count.
    /// Returns 0 for local dev builds (csproj's &lt;Version&gt;0&lt;/Version&gt;
    /// sentinel) so AutoUpdate refuses to overwrite a dev working tree.
    private static int ReadLocalCommitCount()
    {
        try
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            return v?.Major ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
