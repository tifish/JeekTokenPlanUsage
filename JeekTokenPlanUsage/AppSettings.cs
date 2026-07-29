using System.Text.Json;
using JeekTools;
using Microsoft.Win32;
using System.Text.Json.Serialization;

namespace JeekTokenPlanUsage;

/// How many tray icons each enabled provider contributes.
///   None - no per-provider icons; only the anchor stays visible.
///   Single - one icon per provider (Claude/Codex/Grok show primary, Cursor shows API);
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
/// %LocalAppData%\JeekTokenPlanUsage\Config. Serialized names are part of the
/// on-disk settings format; maps to JeekTools.StorageLocation internally.
public enum SettingsStorageMode
{
    AppData = 0,
    Portable = 1,
    Custom = 2,
}

internal sealed class AppSettings
{
    private const string AppName = "JeekTokenPlanUsage";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeyName = "JeekTokenPlanUsage";

    /// JeekTools path scheme: machine settings always live under
    /// %LocalAppData%\<App>\Config\settings.json; roaming settings live in the
    /// active storage location's Config dir; a Config folder next to the exe
    /// forces portable mode regardless of the saved mode.
    private static readonly SettingsStorage Storage = new(AppName);

    /// Tick of the last save this process performed. The settings watcher uses
    /// it to ignore file events caused by our own writes.
    public static long LastWriteTick { get; private set; }

    private string _roamingConfigDirectory = "";
    private string _roamingSettingsPath = "";
    private SettingsStorageMode _savedStorageMode = SettingsStorageMode.AppData;
    private MachineSettingsFile _machineBaseline = new();
    private RoamingSettingsFile _roamingBaseline = new();

    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    public bool ShowCursor { get; set; } = true;
    public bool ShowGrok { get; set; } = true;

    /// Local runtime state: pausing one machine should not pause another
    /// machine when roaming settings are shared.
    public bool Paused { get; set; } = false;

    /// How many icons each enabled provider shows in the tray.
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IconDisplayMode IconMode { get; set; } = IconDisplayMode.Double;

    /// Base polling interval shared by all providers (minutes). Allowed:
    /// 1, 2, 3, 5, 10. Claude's poll may hit the messages-API fallback which
    /// costs real quota - 1 minute is available but burns quota fast; default
    /// is 5. Codex / Cursor / Grok endpoints are free, so they just inherit this.
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
    /// trailing Config segment; SettingsStorage.ResolveConfigRoot appends it.
    [JsonIgnore]
    public string CustomStorageRoot { get; private set; } = "";

    [JsonIgnore]
    public string RoamingConfigDirectory => _roamingConfigDirectory;

    [JsonIgnore]
    public string RoamingSettingsPath => _roamingSettingsPath;

    public static string PortableConfigPath => Storage.ProgramConfigDir;

    /// Machine settings used to live in local-settings.json before the move to
    /// the JeekTools SettingsStorage layout (settings.json in the same dir).
    private static string LegacyMachineSettingsPath =>
        Path.Combine(Storage.LocalConfigDir, "local-settings.json");

    /// Pre-split releases kept one flat settings.json directly under
    /// %AppData%\JeekTokenPlanUsage (no Config segment).
    private static string LegacySettingsPath =>
        Path.Combine(Storage.RoamingDir, "settings.json");

    private static StorageLocation ToLocation(SettingsStorageMode mode) => mode switch
    {
        SettingsStorageMode.Portable => StorageLocation.ProgramDirectory,
        SettingsStorageMode.Custom => StorageLocation.CustomDirectory,
        _ => StorageLocation.UserDirectory,
    };

    private static SettingsStorageMode FromLocation(StorageLocation location) => location switch
    {
        StorageLocation.ProgramDirectory => SettingsStorageMode.Portable,
        StorageLocation.CustomDirectory => SettingsStorageMode.Custom,
        _ => SettingsStorageMode.AppData,
    };

    /// Where the roaming Config directory would live under the given mode.
    /// Used by the storage menu to show the target path before switching.
    public string PreviewConfigRoot(SettingsStorageMode mode, string? customRoot = null) =>
        Storage.ResolveConfigRoot(
            ToLocation(mode),
            string.IsNullOrWhiteSpace(customRoot) ? CustomStorageRoot : customRoot.Trim());

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
        string settingsPath = Storage.ResolveSettingsPath(
            ResolveEffectiveLocation(machine), machine.CustomStorageRoot);
        return PeekLanguageFromPath(settingsPath)
            ?? PeekLanguageFromPath(LegacySettingsPath)
            ?? "";
    }

    public static AppSettings Load()
    {
        MachineSettingsFile machine = LoadMachineSettings();
        StorageLocation effectiveLocation = ResolveEffectiveLocation(machine);
        string roamingSettingsPath = Storage.ResolveSettingsPath(effectiveLocation, machine.CustomStorageRoot);
        string roamingConfigDirectory = Path.GetDirectoryName(roamingSettingsPath)!;

        bool machineFileMissing = !File.Exists(Storage.MachineSettingsPath);
        bool shouldSave = machineFileMissing;

        bool loadedRoaming = JsonSettingsFile.TryLoad(roamingSettingsPath, out RoamingSettingsFile roaming);
        bool loadedFromLegacy = false;
        if (!loadedRoaming)
        {
            loadedFromLegacy = JsonSettingsFile.TryLoad(LegacySettingsPath, out RoamingSettingsFile legacyRoaming)
                && File.Exists(LegacySettingsPath);
            if (loadedFromLegacy)
                roaming = legacyRoaming;
            shouldSave = true;
        }

        if (!loadedRoaming && !loadedFromLegacy)
        {
            roaming = new RoamingSettingsFile
            {
                ShowClaude = ClaudeUsageProvider.HasLocalCredentials(),
                ShowCodex = CodexUsageProvider.HasLocalCredentials(),
                ShowCursor = CursorUsageProvider.HasLocalCredentials(),
                ShowGrok = GrokUsageProvider.HasLocalCredentials(),
            };
        }

        // The legacy flat file carried machine-bound fields too; adopt them the
        // first time the split machine file is created.
        if (machineFileMissing && loadedFromLegacy
            && !File.Exists(LegacyMachineSettingsPath)
            && JsonSettingsFile.TryLoad(LegacySettingsPath, out MachineSettingsFile legacyMachine))
        {
            legacyMachine.StorageMode = machine.StorageMode;
            legacyMachine.CustomStorageRoot = machine.CustomStorageRoot;
            machine = legacyMachine;
        }

        var settings = new AppSettings();
        settings.ApplyRoamingSettings(roaming);
        settings.ApplyMachineSettings(machine);
        settings.SetStorage(machine, FromLocation(effectiveLocation), roamingConfigDirectory, roamingSettingsPath);
        settings._machineBaseline = JsonSettingsFile.Clone(machine);
        settings._roamingBaseline = JsonSettingsFile.Clone(roaming);
        shouldSave |= settings.NormalizeLegacyFields();
        if (shouldSave)
            settings.Save();

        // Once the new-layout machine file exists the legacy one is dead weight.
        if (machineFileMissing && File.Exists(Storage.MachineSettingsPath))
            try { File.Delete(LegacyMachineSettingsPath); } catch { }

        return settings;
    }

    public void ReloadFromDisk()
    {
        AppSettings loaded = Load();
        CopyFrom(loaded);
    }

    /// Switches the roaming storage mode. When moveFiles is true the whole
    /// Config directory is moved (same-volume rename, cross-volume copy
    /// fallback) via SettingsStorage.MoveConfigRoot; leaving portable mode
    /// always moves, because a Config dir next to the exe would force portable
    /// mode again on next start. Throws on failure - callers surface the error.
    public void SwitchStorageMode(SettingsStorageMode mode, string? customRoot = null, bool moveFiles = true)
    {
        if (mode == SettingsStorageMode.Custom)
        {
            string root = string.IsNullOrWhiteSpace(customRoot) ? CustomStorageRoot : customRoot;
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("A custom settings folder is required.");
            customRoot = root.Trim();
        }

        string oldRoamingConfigDirectory = _roamingConfigDirectory;
        bool leavingPortable = StorageMode == SettingsStorageMode.Portable
            && mode != SettingsStorageMode.Portable;

        string effectiveCustomRoot = mode == SettingsStorageMode.Custom ? customRoot! : CustomStorageRoot;
        string newRoamingConfigDirectory = Storage.ResolveConfigRoot(ToLocation(mode), effectiveCustomRoot);
        string newRoamingSettingsPath = Storage.ResolveSettingsPath(ToLocation(mode), effectiveCustomRoot);

        if (!SamePath(oldRoamingConfigDirectory, newRoamingConfigDirectory)
            && (moveFiles || leavingPortable))
        {
            SettingsStorage.MoveConfigRoot(oldRoamingConfigDirectory, newRoamingConfigDirectory);
        }
        else
        {
            // Portable mode is detected by the directory's existence, so it must
            // exist even when the user chose to leave old files behind.
            Directory.CreateDirectory(newRoamingConfigDirectory);
        }

        _savedStorageMode = mode;
        StorageMode = mode;
        if (mode == SettingsStorageMode.Custom)
            CustomStorageRoot = customRoot!;
        _roamingConfigDirectory = newRoamingConfigDirectory;
        _roamingSettingsPath = newRoamingSettingsPath;
        Save();
    }

    public void Save()
    {
        MachineSettingsFile machine = CaptureMachineSettings();
        if (JsonSettingsFile.TryMergeAndWrite(
                Storage.MachineSettingsPath, _machineBaseline, machine,
                static _ => { }, forceAllLocal: false, out MachineSettingsFile mergedMachine))
            _machineBaseline = mergedMachine;

        RoamingSettingsFile roaming = RoamingSettingsFile.From(this);
        bool forceAllLocal = !File.Exists(_roamingSettingsPath);
        if (JsonSettingsFile.TryMergeAndWrite(
                _roamingSettingsPath, _roamingBaseline, roaming,
                static _ => { }, forceAllLocal, out RoamingSettingsFile mergedRoaming))
            _roamingBaseline = mergedRoaming;

        LastWriteTick = Environment.TickCount64;
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

    private static MachineSettingsFile LoadMachineSettings()
    {
        if (!JsonSettingsFile.TryLoad(Storage.MachineSettingsPath, out MachineSettingsFile machine)
            && JsonSettingsFile.TryLoad(LegacyMachineSettingsPath, out MachineSettingsFile legacy))
            machine = legacy;

        if (machine.StorageMode == SettingsStorageMode.Custom
            && string.IsNullOrWhiteSpace(machine.CustomStorageRoot))
            machine.StorageMode = SettingsStorageMode.AppData;
        return machine;
    }

    private static StorageLocation ResolveEffectiveLocation(MachineSettingsFile machine) =>
        Storage.ResolveEffectiveLocation(ToLocation(machine.StorageMode));

    private void ApplyRoamingSettings(RoamingSettingsFile roaming)
    {
        ShowClaude = roaming.ShowClaude;
        ShowCodex = roaming.ShowCodex;
        ShowCursor = roaming.ShowCursor;
        ShowGrok = roaming.ShowGrok;
        IconMode = roaming.IconMode;
        PollMinutes = roaming.PollMinutes;
        Language = roaming.Language;
        EnableThresholdNotifications = roaming.EnableThresholdNotifications;
        ClaudePollMinutes = roaming.ClaudePollMinutes;
        AutoUpdate = roaming.AutoUpdate;
        DisableMirrorDownload = roaming.DisableMirrorDownload;
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
        ShowGrok = other.ShowGrok;
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
        _machineBaseline = other._machineBaseline;
        _roamingBaseline = other._roamingBaseline;
    }

    private static bool SamePath(string left, string right)
    {
        string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
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
    }

    private sealed class RoamingSettingsFile
    {
        public bool ShowClaude { get; set; } = true;
        public bool ShowCodex { get; set; } = true;
        public bool ShowCursor { get; set; } = true;
        public bool ShowGrok { get; set; } = true;

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
            ShowGrok = settings.ShowGrok,
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
