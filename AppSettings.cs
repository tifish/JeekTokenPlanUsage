using Microsoft.Win32;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeekTokenPlanUsage;

internal sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeekTokenPlanUsage", "settings.json");

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyName = "JeekTokenPlanUsage";

    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;

    /// Base polling interval for Claude (minutes). Allowed: 5, 10, 30, 60.
    /// Each Claude poll may hit the messages-API fallback, which costs real
    /// quota, so the minimum is intentionally 5 minutes (no 1-minute option).
    public int ClaudePollMinutes { get; set; } = 5;

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
            if (key is null) return;
            if (value)
                key.SetValue(RunKeyName, ExePath);
            else
                key.DeleteValue(RunKeyName, throwOnMissingValue: false);
        }
    }

    private static string ExePath =>
        $"\"{Environment.ProcessPath ?? Application.ExecutablePath}\"";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
