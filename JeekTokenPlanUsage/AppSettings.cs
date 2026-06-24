using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace JeekTokenPlanUsage;

/// How many tray icons each enabled provider contributes.
///   None - no per-provider icons; only the anchor stays visible.
///   Single - one icon per provider (Claude/Codex show 5h, Cursor shows API);
///            the tooltip lists both windows on two lines.
///   Double - separate icons for each window (default).
public enum IconDisplayMode
{
    None = 0,
    Single = 1,
    Double = 2,
}

/// How outbound HTTP traffic is routed.
///   System - follow the Windows system proxy (HttpClient.DefaultProxy); default.
///   Direct - ignore any proxy and connect straight out.
///   Custom - use the host/port/protocol configured below.
public enum ProxyMode
{
    System = 0,
    Direct = 1,
    Custom = 2,
}

/// Where roaming settings are stored. Machine-local settings always stay under
/// %LocalAppData%\JeekTokenPlanUsage\Config.
public enum SettingsStorageMode
{
    AppData = 0,
    Portable = 1,
    Custom = 2,
}

internal sealed class AppSettings
{
    private const string AppName = "JeekTokenPlanUsage";
    private const string ConfigDirectoryName = "Config";
    private const string SettingsFileName = "settings.json";
    private const string LocalSettingsFileName = "local-settings.json";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyName = "JeekTokenPlanUsage";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private string _roamingConfigDirectory = "";
    private string _roamingSettingsPath = "";
    private SettingsStorageMode _savedStorageMode = SettingsStorageMode.AppData;

    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    public bool ShowCursor { get; set; } = true;

    /// Local runtime state: pausing one machine should not pause another
    /// machine when roaming settings are shared.
    public bool Paused { get; set; } = false;

    /// How many icons each enabled provider shows in the tray.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IconDisplayMode IconMode { get; set; } = IconDisplayMode.Double;

    /// Base polling interval shared by all three providers (minutes). Allowed:
    /// 1, 5, 10, 30, 60. Claude's poll may hit the messages-API fallback which
    /// costs real quota - 1 minute is available but burns quota fast; default
    /// is 5. Codex / Cursor endpoints are free, so they just inherit this.
    public int PollMinutes { get; set; } = 5;

    /// UI language override. Empty / null = follow system UI language, with
    /// English (the neutral resource) used for any culture without a satellite.
    /// Values match a resource culture name, e.g. "en", "zh-CN".
    public string Language { get; set; } = "";

    /// Show a Windows toast when a usage window first crosses 80% or 95%.
    /// Each (window, threshold) only fires once per window cycle.
    public bool EnableThresholdNotifications { get; set; } = true;

    /// Local taskbar setting. Taskbar geometry and user layout are machine-bound.
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
    /// is on - toggle from the tray menu to opt out.
    public bool AutoUpdate { get; set; } = true;

    /// Skip GitHub-mirror probing and download directly from github.com.
    /// Useful for users outside China where the mirrors are slower or
    /// occasionally unhealthy.
    public bool DisableMirrorDownload { get; set; } = false;

    /// How outbound HTTP requests are routed. Defaults to following the Windows
    /// system proxy so behavior matches a browser out of the box. See AppProxy,
    /// which reads these fields live so a tray-menu change applies immediately.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    /// Custom-proxy protocol. "socks5" (default) or "http" (also covers the
    /// https CONNECT proxy). Only consulted when ProxyMode == Custom.
    public string ProxyProtocol { get; set; } = "socks5";

    /// Custom-proxy host. Defaults to localhost, the common case for a local
    /// Clash/V2Ray-style proxy. Only consulted when ProxyMode == Custom.
    public string ProxyHost { get; set; } = "127.0.0.1";

    /// Custom-proxy port. Only consulted when ProxyMode == Custom.
    public int ProxyPort { get; set; } = 7890;

    [JsonIgnore]
    public SettingsStorageMode StorageMode { get; private set; } = SettingsStorageMode.AppData;

    /// Custom storage root selected by the user. This path does not include the
    /// trailing Config segment; ResolveRoamingConfigDirectory appends it.
    [JsonIgnore]
    public string CustomStorageRoot { get; private set; } = "";

    [JsonIgnore]
    public string RoamingConfigDirectory => _roamingConfigDirectory;

    [JsonIgnore]
    public string RoamingSettingsPath => _roamingSettingsPath;

    private static string AppDataConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppName,
        ConfigDirectoryName
    );

    private static string LocalConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppName,
        ConfigDirectoryName
    );

    private static string PortableConfigDirectory => Path.Combine(
        AppContext.BaseDirectory,
        ConfigDirectoryName
    );

    private static string LocalSettingsPath => Path.Combine(LocalConfigDirectory, LocalSettingsFileName);

    private static string LegacySettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppName,
        "settings.json"
    );

    public static string PortableConfigPath => PortableConfigDirectory;

    /// Build the proxy URI from the custom fields, or null when they don't form
    /// a usable proxy (empty host / out-of-range port) so the caller falls back
    /// to a direct connection rather than throwing.
    public Uri? BuildCustomProxyUri()
    {
        if (string.IsNullOrWhiteSpace(ProxyHost) || ProxyPort is <= 0 or > 65535)
            return null;
        string scheme = ProxyProtocol?.Trim().ToLowerInvariant() switch
        {
            "socks5" or "socks" => "socks5",
            _ => "http",
        };
        return Uri.TryCreate($"{scheme}://{ProxyHost.Trim()}:{ProxyPort}", UriKind.Absolute, out Uri? uri)
            ? uri
            : null;
    }

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
    /// Returns "" when the file is missing or unreadable - caller treats empty
    /// as "follow system UI culture".
    public static string PeekLanguage()
    {
        MachineSettingsFile machine = LoadMachineSettings();
        string settingsPath = ResolveRoamingSettingsPath(machine);
        return PeekLanguageFromPath(settingsPath)
            ?? PeekLanguageFromPath(LegacySettingsPath)
            ?? "";
    }

    public static AppSettings Load()
    {
        bool machineFileExists = File.Exists(LocalSettingsPath);
        MachineSettingsFile machine = LoadMachineSettings();
        SettingsStorageMode effectiveMode = ResolveEffectiveStorageMode(machine);
        string roamingSettingsPath = ResolveRoamingSettingsPath(machine, effectiveMode);
        string roamingConfigDirectory = Path.GetDirectoryName(roamingSettingsPath)!;

        bool shouldSave = !machineFileExists;
        AppSettings? loaded = TryReadSettings(roamingSettingsPath);
        bool loadedFromLegacy = false;
        if (loaded is null)
        {
            loaded = TryReadSettings(LegacySettingsPath);
            loadedFromLegacy = loaded is not null;
            shouldSave = true;
        }

        if (loaded is null)
        {
            shouldSave = true;
            loaded = new AppSettings
            {
                ShowClaude = ClaudeUsageProvider.HasLocalCredentials(),
                ShowCodex = CodexUsageProvider.HasLocalCredentials(),
                ShowCursor = CursorUsageProvider.HasLocalCredentials(),
            };
        }

        if (!machineFileExists && loadedFromLegacy)
            machine = MachineSettingsFile.FromLegacy(loaded, machine);

        loaded.ApplyMachineSettings(machine);
        loaded.SetStorage(machine, effectiveMode, roamingConfigDirectory, roamingSettingsPath);
        shouldSave |= loaded.NormalizeLegacyFields();
        if (shouldSave)
            loaded.Save();
        return loaded;
    }

    public void ReloadFromDisk()
    {
        AppSettings loaded = Load();
        CopyFrom(loaded);
    }

    public void SwitchStorageMode(SettingsStorageMode mode, string? customRoot = null)
    {
        if (mode == SettingsStorageMode.Custom)
        {
            string root = string.IsNullOrWhiteSpace(customRoot) ? CustomStorageRoot : customRoot;
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("A custom settings folder is required.");
            customRoot = root.Trim();
        }

        string oldRoamingConfigDirectory = _roamingConfigDirectory;
        SettingsStorageMode oldMode = StorageMode;
        string oldPortableConfigDirectory = PortableConfigDirectory;

        MachineSettingsFile machine = CaptureMachineSettings();
        machine.StorageMode = mode;
        if (customRoot is not null)
            machine.CustomStorageRoot = customRoot;

        string newRoamingConfigDirectory = ResolveRoamingConfigDirectory(machine, forcePortableWhenPresent: false);
        string newRoamingSettingsPath = Path.Combine(newRoamingConfigDirectory, SettingsFileName);

        if (!SamePath(oldRoamingConfigDirectory, newRoamingConfigDirectory)
            && Directory.Exists(oldRoamingConfigDirectory))
        {
            CopyDirectoryContents(oldRoamingConfigDirectory, newRoamingConfigDirectory);
        }

        SetStorage(machine, mode, newRoamingConfigDirectory, newRoamingSettingsPath);
        Save();

        if (oldMode == SettingsStorageMode.Portable
            && mode != SettingsStorageMode.Portable
            && Directory.Exists(oldPortableConfigDirectory)
            && !SamePath(oldPortableConfigDirectory, newRoamingConfigDirectory))
        {
            Directory.Delete(oldPortableConfigDirectory, recursive: true);
        }
    }

    public void Save()
    {
        SaveRoamingSettings();
        SaveMachineSettings(CaptureMachineSettings());
    }

    private void SaveRoamingSettings()
    {
        try
        {
            Directory.CreateDirectory(_roamingConfigDirectory);
            File.WriteAllText(
                _roamingSettingsPath,
                JsonSerializer.Serialize(RoamingSettingsFile.From(this), JsonOptions));
        }
        catch { }
    }

    private static void SaveMachineSettings(MachineSettingsFile machine)
    {
        try
        {
            Directory.CreateDirectory(LocalConfigDirectory);
            File.WriteAllText(LocalSettingsPath, JsonSerializer.Serialize(machine, JsonOptions));
        }
        catch { }
    }

    private static string? PeekLanguageFromPath(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(nameof(Language), out var el)
                && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static AppSettings? TryReadSettings(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static MachineSettingsFile LoadMachineSettings()
    {
        MachineSettingsFile settings = TryReadMachineSettings() ?? new MachineSettingsFile();
        if (settings.StorageMode == SettingsStorageMode.Custom
            && string.IsNullOrWhiteSpace(settings.CustomStorageRoot))
            settings.StorageMode = SettingsStorageMode.AppData;
        return settings;
    }

    private static MachineSettingsFile? TryReadMachineSettings()
    {
        if (!File.Exists(LocalSettingsPath))
            return null;
        try
        {
            return JsonSerializer.Deserialize<MachineSettingsFile>(
                File.ReadAllText(LocalSettingsPath),
                JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static SettingsStorageMode ResolveEffectiveStorageMode(MachineSettingsFile machine) =>
        Directory.Exists(PortableConfigDirectory)
            ? SettingsStorageMode.Portable
            : machine.StorageMode;

    private static string ResolveRoamingSettingsPath(
        MachineSettingsFile machine,
        SettingsStorageMode? effectiveMode = null) =>
        Path.Combine(
            ResolveRoamingConfigDirectory(machine, effectiveMode: effectiveMode),
            SettingsFileName);

    private static string ResolveRoamingConfigDirectory(
        MachineSettingsFile machine,
        bool forcePortableWhenPresent = true,
        SettingsStorageMode? effectiveMode = null)
    {
        SettingsStorageMode mode = effectiveMode
            ?? (forcePortableWhenPresent && Directory.Exists(PortableConfigDirectory)
            ? SettingsStorageMode.Portable
            : machine.StorageMode);

        return mode switch
        {
            SettingsStorageMode.Portable => PortableConfigDirectory,
            SettingsStorageMode.Custom when !string.IsNullOrWhiteSpace(machine.CustomStorageRoot) =>
                Path.Combine(machine.CustomStorageRoot, ConfigDirectoryName),
            _ => AppDataConfigDirectory,
        };
    }

    private void ApplyMachineSettings(MachineSettingsFile machine)
    {
        Paused = machine.Paused;
        ShowTaskbarWidget = machine.ShowTaskbarWidget;
        TaskbarWidgetOffset = machine.TaskbarWidgetOffset;
        ProxyMode = machine.ProxyMode;
        ProxyProtocol = string.IsNullOrWhiteSpace(machine.ProxyProtocol) ? "socks5" : machine.ProxyProtocol;
        ProxyHost = string.IsNullOrWhiteSpace(machine.ProxyHost) ? "127.0.0.1" : machine.ProxyHost;
        ProxyPort = machine.ProxyPort is > 0 and <= 65535 ? machine.ProxyPort : 7890;
    }

    private MachineSettingsFile CaptureMachineSettings() => new()
    {
        StorageMode = _savedStorageMode,
        CustomStorageRoot = CustomStorageRoot,
        Paused = Paused,
        ShowTaskbarWidget = ShowTaskbarWidget,
        TaskbarWidgetOffset = TaskbarWidgetOffset,
        ProxyMode = ProxyMode,
        ProxyProtocol = ProxyProtocol,
        ProxyHost = ProxyHost,
        ProxyPort = ProxyPort,
    };

    private void SetStorage(
        MachineSettingsFile machine,
        SettingsStorageMode effectiveMode,
        string roamingConfigDirectory,
        string roamingSettingsPath)
    {
        _savedStorageMode = machine.StorageMode;
        StorageMode = effectiveMode;
        CustomStorageRoot = machine.CustomStorageRoot ?? "";
        _roamingConfigDirectory = roamingConfigDirectory;
        _roamingSettingsPath = roamingSettingsPath;
    }

    private bool NormalizeLegacyFields()
    {
        bool changed = false;
        if (PollMinutes == 0)
        {
            PollMinutes = ClaudePollMinutes > 0 ? ClaudePollMinutes : 5;
            changed = true;
        }
        if (ClaudePollMinutes != 0)
        {
            ClaudePollMinutes = 0;
            changed = true;
        }
        return changed;
    }

    private void CopyFrom(AppSettings other)
    {
        ShowClaude = other.ShowClaude;
        ShowCodex = other.ShowCodex;
        ShowCursor = other.ShowCursor;
        Paused = other.Paused;
        IconMode = other.IconMode;
        PollMinutes = other.PollMinutes;
        Language = other.Language;
        EnableThresholdNotifications = other.EnableThresholdNotifications;
        ShowTaskbarWidget = other.ShowTaskbarWidget;
        TaskbarWidgetOffset = other.TaskbarWidgetOffset;
        ClaudePollMinutes = other.ClaudePollMinutes;
        AutoUpdate = other.AutoUpdate;
        DisableMirrorDownload = other.DisableMirrorDownload;
        ProxyMode = other.ProxyMode;
        ProxyProtocol = other.ProxyProtocol;
        ProxyHost = other.ProxyHost;
        ProxyPort = other.ProxyPort;
        StorageMode = other.StorageMode;
        _savedStorageMode = other._savedStorageMode;
        CustomStorageRoot = other.CustomStorageRoot;
        _roamingConfigDirectory = other._roamingConfigDirectory;
        _roamingSettingsPath = other._roamingSettingsPath;
    }

    private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }

        foreach (string sourceSubdirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            if (IsSameOrChildPath(destinationDirectory, sourceSubdirectory))
                continue;
            string destinationSubdirectory = Path.Combine(
                destinationDirectory,
                Path.GetFileName(sourceSubdirectory));
            CopyDirectoryContents(sourceSubdirectory, destinationSubdirectory);
        }
    }

    private static bool SamePath(string left, string right)
    {
        string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrChildPath(string child, string parent)
    {
        string normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalizedChild = Path.GetFullPath(child)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MachineSettingsFile
    {
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public SettingsStorageMode StorageMode { get; set; } = SettingsStorageMode.AppData;
        public string CustomStorageRoot { get; set; } = "";
        public bool Paused { get; set; } = false;
        public bool ShowTaskbarWidget { get; set; } = false;
        public int TaskbarWidgetOffset { get; set; } = 0;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ProxyMode ProxyMode { get; set; } = ProxyMode.System;
        public string ProxyProtocol { get; set; } = "socks5";
        public string ProxyHost { get; set; } = "127.0.0.1";
        public int ProxyPort { get; set; } = 7890;

        public static MachineSettingsFile FromLegacy(AppSettings legacy, MachineSettingsFile current) => new()
        {
            StorageMode = current.StorageMode,
            CustomStorageRoot = current.CustomStorageRoot,
            Paused = legacy.Paused,
            ShowTaskbarWidget = legacy.ShowTaskbarWidget,
            TaskbarWidgetOffset = legacy.TaskbarWidgetOffset,
            ProxyMode = legacy.ProxyMode,
            ProxyProtocol = legacy.ProxyProtocol,
            ProxyHost = legacy.ProxyHost,
            ProxyPort = legacy.ProxyPort,
        };
    }

    private sealed class RoamingSettingsFile
    {
        public bool ShowClaude { get; set; } = true;
        public bool ShowCodex { get; set; } = true;
        public bool ShowCursor { get; set; } = true;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public IconDisplayMode IconMode { get; set; } = IconDisplayMode.Double;
        public int PollMinutes { get; set; } = 5;
        public string Language { get; set; } = "";
        public bool EnableThresholdNotifications { get; set; } = true;
        public int ClaudePollMinutes { get; set; }
        public bool AutoUpdate { get; set; } = true;
        public bool DisableMirrorDownload { get; set; } = false;

        public static RoamingSettingsFile From(AppSettings settings) => new()
        {
            ShowClaude = settings.ShowClaude,
            ShowCodex = settings.ShowCodex,
            ShowCursor = settings.ShowCursor,
            IconMode = settings.IconMode,
            PollMinutes = settings.PollMinutes,
            Language = settings.Language,
            EnableThresholdNotifications = settings.EnableThresholdNotifications,
            ClaudePollMinutes = 0,
            AutoUpdate = settings.AutoUpdate,
            DisableMirrorDownload = settings.DisableMirrorDownload,
        };
    }
}
