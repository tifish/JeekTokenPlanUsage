using JeekTools;

namespace JeekTokenPlanUsage;

/// Thin app-side wrapper over JeekTools.AutoUpdater. Version identity is the
/// total git commit count on main — CI runs `dotnet build -c Release /p:Version=<count>`
/// which stamps AssemblyVersion.Major, and the `latest_release` release carries
/// a version.txt with the same integer. The updater compares the two, downloads
/// and stages the zip in-app (mirror racing, speed cutover, verification), then
/// AutoUpdate.ps1 swaps the files after the app exits.
public static class AutoUpdate
{
    public const string RepoUrl = "https://github.com/tifish/JeekTokenPlanUsage";

    public const string ReleaseZipUrl =
        "https://github.com/tifish/JeekTokenPlanUsage/releases/download/latest_release/JeekTokenPlanUsage.zip";

    public const string VersionTxtUrl =
        "https://github.com/tifish/JeekTokenPlanUsage/releases/download/latest_release/version.txt";

    private static readonly bool IsDebugBuild =
#if DEBUG
        true;
#else
        false;
#endif

    private static readonly AutoUpdater Updater = new(new AutoUpdaterOptions
    {
        AppExeName = "JeekTokenPlanUsage.exe",
        ReleaseZipUrl = ReleaseZipUrl,
        VersionTxtUrl = VersionTxtUrl,
        UpdateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JeekTokenPlanUsage", "Update"),
        UserAgent = "JeekTokenPlanUsage-Updater/1.0",
        // Debug builds never self-update. Release dev builds are additionally
        // protected by the version sentinel: csproj bakes 0.0.0.0 unless CI
        // overrides it, and 0 < MinimumValidLocalVersion.
        Disabled = IsDebugBuild,
        MinimumValidLocalVersion = 1,
    });

    /// The local build identity (git commit count baked into AssemblyVersion.Major).
    /// 0 for a local dev build that didn't pass through CI. Shown in the About box.
    public static int LocalBuild => Updater.GetLocalVersion();

    public static int LocalCommitCount => Updater.LocalVersion;
    public static int RemoteCommitCount => Updater.RemoteVersion;

    /// Short English description of why the most recent check or install step
    /// failed. Mirrors AutoUpdater.FailureReason.
    public static string FailureReason => Updater.FailureReason;

    /// Checks version.txt across mirrors and reports whether a newer release
    /// exists. Never throws; failures come back as UpdateCheckOutcome.Failed.
    public static Task<UpdateCheckOutcome> HasUpdateAsync() => Updater.HasUpdateAsync();

    public static string UpdateRoot => Updater.UpdateRoot;

    public static Task<string?> DownloadAsync(bool disableMirror) =>
        Updater.DownloadAndStageAsync(disableMirror ? [ReleaseZipUrl] : null);

    public static bool LaunchInstall(string package) => Updater.LaunchInstall(package);
}
