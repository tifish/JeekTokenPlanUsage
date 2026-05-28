using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace JeekTokenPlanUsage;

/// How many tray icons each enabled provider contributes.
///   None — no per-provider icons; only the anchor stays visible.
///   Single — one icon per provider (Claude/Codex show 5h, Cursor shows API);
///            the tooltip lists both windows on two lines.
///   Double — separate icons for each window (default).
public enum IconDisplayMode
{
    None = 0,
    Single = 1,
    Double = 2,
}

internal sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeekTokenPlanUsage",
        "settings.json"
    );

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyName = "JeekTokenPlanUsage";

    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    public bool ShowCursor { get; set; } = true;

    /// How many icons each enabled provider shows in the tray. Persisted as a
    /// string so the settings file stays human-readable.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IconDisplayMode IconMode { get; set; } = IconDisplayMode.Double;

    /// Base polling interval shared by all three providers (minutes). Allowed:
    /// 1, 5, 10, 30, 60. Claude's poll may hit the messages-API fallback which
    /// costs real quota — 1 minute is available but burns quota fast; default
    /// is 5. Codex / Cursor endpoints are free, so they just inherit this.
    public int PollMinutes { get; set; } = 5;

    /// UI language override. Empty / null = follow system UI language, with
    /// English (the neutral resource) used for any culture without a satellite.
    /// Values match a resource culture name, e.g. "en", "zh-CN".
    public string Language { get; set; } = "";

    /// Show a Windows toast when a usage window first crosses 80% or 95%.
    /// Each (window, threshold) only fires once per window cycle.
    public bool EnableThresholdNotifications { get; set; } = true;

    /// Show an embedded widget on the taskbar (progress bars + percentages),
    /// in addition to / instead of the tray icons. Off by default so existing
    /// users keep the tray-only behavior until they opt in.
    public bool ShowTaskbarWidget { get; set; } = false;

    /// Horizontal gap (device-independent px) between the widget's right edge
    /// and the tray notification area. Adjusted by dragging the widget.
    public int TaskbarWidgetOffset { get; set; } = 0;

    /// Legacy field retained only for one-shot migration from older
    /// settings.json files. New writes leave it at 0; see Load().
    public int ClaudePollMinutes { get; set; }

    /// Periodically check the GitHub `latest_release` artifact and, when newer
    /// than the local exe, silently relaunch into the updater script. Off-by-default
    /// auto-update would surprise users who installed manually, so the default
    /// is on — toggle from the tray menu to opt out.
    public bool AutoUpdate { get; set; } = true;

    /// Skip GitHub-mirror probing and download directly from github.com.
    /// Useful for users outside China where the mirrors are slower or
    /// occasionally unhealthy.
    public bool DisableMirrorDownload { get; set; } = false;

    [JsonIgnore]
    public bool RunAtStartup
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunKeyName) is string;
        }
        set
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;
            if (value)
                key.SetValue(RunKeyName, ExePath);
            else
                key.DeleteValue(RunKeyName, throwOnMissingValue: false);
        }
    }

    private static string ExePath => $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"";

    /// Side-effect-free read of just the Language field, used at startup before
    /// any WinForms code (and before any provider credential probing in Load).
    /// Returns "" when the file is missing or unreadable — caller treats empty
    /// as "follow system UI culture".
    public static string PeekLanguage()
    {
        if (!File.Exists(SettingsPath))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty("Language", out var el)
                && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    public static AppSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            try
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings loaded =
                    JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                // Migrate legacy ClaudePollMinutes -> PollMinutes (one-shot).
                if (loaded.PollMinutes == 0)
                {
                    loaded.PollMinutes =
                        loaded.ClaudePollMinutes > 0 ? loaded.ClaudePollMinutes : 5;
                    loaded.ClaudePollMinutes = 0;
                    loaded.Save();
                }

                return loaded;
            }
            catch
            {
                return new AppSettings();
            }
        }

        // First launch: enable each provider only when its local credentials are present,
        // then persist so subsequent launches use the saved choice instead of re-detecting.
        var fresh = new AppSettings
        {
            ShowClaude = ClaudeUsageProvider.HasLocalCredentials(),
            ShowCodex = CodexUsageProvider.HasLocalCredentials(),
            ShowCursor = CursorUsageProvider.HasLocalCredentials(),
        };
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })
            );
        }
        catch { }
    }
}
