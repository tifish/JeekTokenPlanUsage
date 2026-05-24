using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace JeekTokenPlanUsage;

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

    /// Base polling interval shared by all three providers (minutes). Allowed:
    /// 1, 5, 10, 30, 60. Claude's poll may hit the messages-API fallback which
    /// costs real quota — 1 minute is available but burns quota fast; default
    /// is 5. Codex / Cursor endpoints are free, so they just inherit this.
    public int PollMinutes { get; set; } = 5;

    /// Legacy field retained only for one-shot migration from older
    /// settings.json files. New writes leave it at 0; see Load().
    public int ClaudePollMinutes { get; set; }

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
