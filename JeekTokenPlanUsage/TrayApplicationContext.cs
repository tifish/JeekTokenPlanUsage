using System.Globalization;
using System.Runtime.InteropServices;
using JeekTokenPlanUsage.Resources;
using Microsoft.Win32;
using WinTimer = System.Windows.Forms.Timer;

namespace JeekTokenPlanUsage;

public sealed class TrayApplicationContext : ApplicationContext, IMcpUsageSource
{
    // Allowed base polling intervals (minutes), shared across all providers.
    // Claude's fallback path may consume quota at the shorter end; 1 minute is
    // offered for users who accept that cost in exchange for fresher data. The
    // upper bound stays low: idle pause already covers the away case, so a
    // stale number adds nothing useful during active use.
    private static readonly int[] AllowedPollMinutes = { 1, 2, 3, 5, 10 };

    // After a successful Claude poll whose returned reset time is already in
    // the past, poll again at this cadence until the API rolls over to a new
    // window. Claude-only; Codex / Cursor don't need this.
    private static readonly TimeSpan ClaudeFastPollAfterReset = TimeSpan.FromSeconds(10);

    // While Claude usage is actively increasing, keep a short burst of faster
    // polls so the tray catches up without permanently raising fallback cost.
    private static readonly TimeSpan ClaudeActivePollInterval = TimeSpan.FromMinutes(2);
    private const int ClaudeActivePollFollowUps = 2;

    // Timer-driven Claude/Codex polls pause while the user is away; manual
    // refresh still bypasses this so the menu stays responsive.
    private static readonly TimeSpan IdlePause = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(30);

    // Poll just after an upcoming reset instead of waiting a full base interval.
    private static readonly TimeSpan ClaudeResetPollBuffer = TimeSpan.FromSeconds(5);

    // Cap on exponential backoff during sustained errors. Shared by Claude and
    // Codex so a flapping network/API can't push the next attempt out forever.
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly ClaudeUsageProvider _claude = new();
    private readonly CodexUsageProvider _codex = new();
    private readonly CursorUsageProvider _cursor = new();
    private readonly GrokUsageProvider _grok = new();

    // Stable GUIDs for every tray icon. Required so Windows 11's drag-to-reorder
    // can tell our icons apart — without NIF_GUID the shell uses (exe path +
    // position) as its persistence key, which is ambiguous when multiple icons
    // come from the same process. See TrayIcon.cs for details.
    private static readonly Guid AnchorGuid = new("3d71bbf6-953d-4a9e-adf4-4f0b3aa9913d");
    private static readonly Guid ClaudePrimaryGuid = new("d2ced3b6-8934-4dd8-84c7-df5a2c577a7a");
    private static readonly Guid ClaudeSecondaryGuid = new("bf0f5b5e-8163-41fa-ac2c-a7f6b6170148");
    private static readonly Guid CodexPrimaryGuid = new("3de345d0-48fd-4539-be26-d355a6c02b21");
    private static readonly Guid CodexSecondaryGuid = new("1d7bd7f9-da62-4c65-8127-a198d80fbe96");
    private static readonly Guid CursorPrimaryGuid = new("34c0a3e5-d659-4612-bfb0-8c9470d65304");
    private static readonly Guid CursorSecondaryGuid = new("39ce63d9-34ad-4ccc-85ee-4a97070cc2aa");
    private static readonly Guid GrokPrimaryGuid = new("a8e4f2c1-7b3d-4e9a-9c1f-2d6b8a5e0f41");
    private static readonly Guid GrokSecondaryGuid = new("b9f5a3d2-8c4e-5f0b-ad2e-3e7c9b6f1052");
    private static readonly Color AnchorFrameColor = Color.FromArgb(100, 100, 100);

    private readonly ProviderIcons _claudeIcons;
    private readonly ProviderIcons _codexIcons;
    private readonly ProviderIcons _cursorIcons;
    private readonly ProviderIcons _grokIcons;

    private readonly WinTimer _claudeTimer;
    private readonly WinTimer _codexTimer;
    private readonly WinTimer _cursorTimer;
    private readonly WinTimer _grokTimer;

    private readonly AppSettings _settings;
    private readonly TrayIcon _anchor;
    private Icon? _anchorIcon;
    private readonly DetailsForm _detailsForm;
    private readonly TaskbarWidget _taskbarWidget;
    private readonly SystemChangeListener _systemChangeListener;
    private readonly McpHttpServer _mcpServer;
    private readonly SynchronizationContext? _uiContext;
    private AboutForm? _aboutForm;

    // Track when each provider was last polled, so a wake/unlock event can skip
    // providers whose data is still fresh (within one base poll interval). This
    // keeps rapid lock/unlock cycles from firing a request per event.
    private DateTimeOffset _claudeLastPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset _codexLastPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset _cursorLastPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset _grokLastPollAt = DateTimeOffset.MinValue;

    private UsageSnapshot? _claudeSnap;
    private UsageSnapshot? _codexSnap;
    private UsageSnapshot? _cursorSnap;
    private UsageSnapshot? _grokSnap;

    private int _baseIntervalMs;
    private int _claudeRetryCount;
    private int _claudeFastPollsRemaining;
    private string _claudeAuthCredentialSignature = string.Empty;
    private string _codexAuthCredentialSignature = string.Empty;
    private string _grokAuthCredentialSignature = string.Empty;
    private UsageSnapshot? _claudeLastSuccessfulSnap;

    private bool _claudeBusy;
    private bool _claudeAuthPaused;
    private bool _claudeAuthNotified;
    private bool _claudeIdlePaused;
    private bool _codexBusy;
    private bool _codexAuthPaused;
    private bool _codexAuthNotified;
    private bool _codexIdlePaused;
    private int _codexRetryCount;
    private bool _cursorBusy;
    private bool _grokBusy;
    private bool _grokAuthPaused;
    private bool _grokAuthNotified;
    private bool _grokIdlePaused;
    private int _grokRetryCount;
    private bool _disposed;
    private bool _paused;

    // Menu items kept as fields so they can be re-localized in place when the
    // user switches language without rebuilding (and re-wiring) the menu.
    // Not readonly: BuildMenu assigns them, which the compiler can't see as
    // a constructor-only init even though it's only called from the ctor.
    private ToolStripMenuItem _pauseItem = null!;
    private ToolStripMenuItem _refreshItem = null!;
    private ToolStripMenuItem _startupItem = null!;
    private ToolStripMenuItem _showClaudeItem = null!;
    private ToolStripMenuItem _showCodexItem = null!;
    private ToolStripMenuItem _showCursorItem = null!;
    private ToolStripMenuItem _showGrokItem = null!;
    private ToolStripMenuItem _iconDisplayParent = null!;
    private ToolStripMenuItem _intervalParent = null!;
    private ToolStripMenuItem _languageItem = null!;
    private ToolStripMenuItem _languageAutoItem = null!;
    private ToolStripMenuItem _notifyItem = null!;
    private ToolStripMenuItem _widgetItem = null!;
    private ToolStripMenuItem _storageParent = null!;
    private ToolStripMenuItem _storageAppDataItem = null!;
    private ToolStripMenuItem _storagePortableItem = null!;
    private ToolStripMenuItem _storageCustomItem = null!;
    private ToolStripMenuItem _storageChooseCustomItem = null!;
    private ToolStripMenuItem _openLogItem = null!;
    private ToolStripMenuItem _checkUpdateItem = null!;
    private ToolStripMenuItem _autoUpdateItem = null!;
    private ToolStripMenuItem _proxyParent = null!;
    private ToolStripMenuItem _proxyCustomSettingsItem = null!;
    private ToolStripMenuItem _aboutItem = null!;
    private ToolStripMenuItem _exitItem = null!;

    private readonly WinTimer _updateTimer;
    private readonly WinTimer _settingsReloadTimer;
    private FileSystemWatcher? _settingsWatcher;
    private bool _updateInProgress;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan UpdateInitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettingsReloadDelay = TimeSpan.FromSeconds(10);

    public TrayApplicationContext()
    {
        _settings = AppSettings.Load();
        _paused = _settings.Paused;
        AppProxy.Configure(_settings);
        _baseIntervalMs = ClampPollMinutes(_settings.PollMinutes) * 60_000;

        ContextMenuStrip menu = BuildMenu();

        // Frame colors echo the brand icons: Claude = orange, Codex = blue, Cursor = monochrome dark;
        // the shorter window uses the brighter shade, the longer one the deeper shade.
        Func<bool> notifyEnabled = () => _settings.EnableThresholdNotifications;
        Action onLeftClick = HandleLeftClick;

        _claudeIcons = new ProviderIcons(
            "Claude",
            new WindowSpec(Color.FromArgb(255, 146, 48), "5h", LongDate: false),
            new WindowSpec(Color.FromArgb(198, 93, 20), Strings.Tray_WeeklyLabel, LongDate: true),
            SingleModeWindow.Primary,
            ClaudePrimaryGuid,
            ClaudeSecondaryGuid,
            menu,
            notifyEnabled,
            onLeftClick
        );
        _codexIcons = new ProviderIcons(
            "Codex",
            new WindowSpec(Color.FromArgb(64, 140, 255), "5h", LongDate: false),
            new WindowSpec(Color.FromArgb(30, 85, 200), Strings.Tray_WeeklyLabel, LongDate: true),
            SingleModeWindow.Primary,
            CodexPrimaryGuid,
            CodexSecondaryGuid,
            menu,
            notifyEnabled,
            onLeftClick
        );
        // Cursor's brand mark is monochrome with a slight cool cast; we use a light
        // and a deep cool slate. Avoid going near pure black — it disappears on
        // dark taskbars. Both pools reset on the monthly billing cycle, so both
        // use the long (MM-dd) date format. In single-icon mode we surface API
        // (the paid pool) since Auto rarely runs out for most plans.
        _cursorIcons = new ProviderIcons(
            "Cursor",
            new WindowSpec(Color.FromArgb(170, 175, 190), "Auto", LongDate: true),
            new WindowSpec(Color.FromArgb(85, 95, 120), "API", LongDate: true),
            SingleModeWindow.Secondary,
            CursorPrimaryGuid,
            CursorSecondaryGuid,
            menu,
            notifyEnabled,
            onLeftClick
        );
        // Grok / xAI brand leans black-and-white; we use a violet pair so it stays
        // distinct from Claude (orange), Codex (blue), and Cursor (slate). Weekly
        // SuperGrok pool + monthly included credits both use long date format.
        // In single-icon mode surface the weekly pool (the SuperGrok shared quota).
        _grokIcons = new ProviderIcons(
            "Grok",
            new WindowSpec(Color.FromArgb(160, 120, 255), "7d", LongDate: true),
            new WindowSpec(Color.FromArgb(100, 70, 180), "Mo", LongDate: true),
            SingleModeWindow.Primary,
            GrokPrimaryGuid,
            GrokSecondaryGuid,
            menu,
            notifyEnabled,
            onLeftClick
        );

        _anchorIcon = IconRenderer.Render(AnchorFrameColor, null, isError: true, placeholder: "T");
        _anchor = new TrayIcon(AnchorGuid, menu)
        {
            Icon = _anchorIcon,
            Text = "JeekTokenPlanUsage",
            LeftClick = onLeftClick,
        };

        // The form is reused across opens; UpdateRows rebuilds the rows each
        // time so the panel can render only the providers the user has enabled.
        _detailsForm = new DetailsForm();

        _taskbarWidget = new TaskbarWidget(
            menu, onLeftClick, OnWidgetOffsetChanged, _settings.TaskbarWidgetOffset);

        // Repaint theme/display-aware surfaces as soon as Windows broadcasts changes.
        _systemChangeListener = new SystemChangeListener(OnSystemThemeChanged, OnDisplayMetricsChanged);

        // Force a refresh on resume / unlock. The poll timer pauses while the user
        // is away, and a WinForms Timer doesn't fire while the system sleeps — so
        // when the user comes back overnight, the tray could otherwise show data
        // hours stale until the next 30s idle-check tick happens to land. These
        // events bypass the idle gate so the icon is current as soon as the user
        // is back at the machine.
        _uiContext = SynchronizationContext.Current;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        _claudeIcons.ApplyMode(EffectiveMode(_settings.ShowClaude));
        _codexIcons.ApplyMode(EffectiveMode(_settings.ShowCodex));
        _cursorIcons.ApplyMode(EffectiveMode(_settings.ShowCursor));
        _grokIcons.ApplyMode(EffectiveMode(_settings.ShowGrok));
        UpdateAnchor();
        _taskbarWidget.Visible = _settings.ShowTaskbarWidget;

        _claudeTimer = new WinTimer { Interval = _baseIntervalMs };
        _claudeTimer.Tick += async (_, _) => await RefreshClaudeAsync();

        _codexTimer = new WinTimer { Interval = _baseIntervalMs };
        _codexTimer.Tick += async (_, _) => await RefreshCodexAsync();

        _cursorTimer = new WinTimer { Interval = _baseIntervalMs };
        _cursorTimer.Tick += async (_, _) => await RefreshCursorAsync();

        _grokTimer = new WinTimer { Interval = _baseIntervalMs };
        _grokTimer.Tick += async (_, _) => await RefreshGrokAsync();

        WireIconDisplayMenu();
        WireIntervalMenu();
        WireLanguageMenu();
        WireStorageMenu();
        WireProxyMenu();
        _settingsReloadTimer = new WinTimer { Interval = (int)SettingsReloadDelay.TotalMilliseconds };
        _settingsReloadTimer.Tick += (_, _) =>
        {
            _settingsReloadTimer.Stop();
            ReloadSettingsFromDisk();
        };
        StartSettingsWatcher();
        ApplyTimers();

        _pauseItem.Checked = _paused;
        _pauseItem.Click += (_, _) => SetPaused(!_paused);
        _refreshItem.Enabled = !_paused;
        _checkUpdateItem.Enabled = !_paused;
        ApplyPausedVisuals();

        if (!_paused)
        {
            if (_settings.ShowClaude)
                _ = RefreshClaudeAsync(force: true);
            if (_settings.ShowCodex)
                _ = RefreshCodexAsync(force: true);
            if (_settings.ShowCursor)
                _ = RefreshCursorAsync();
            if (_settings.ShowGrok)
                _ = RefreshGrokAsync(force: true);
        }

        _startupItem.Checked = _settings.RunAtStartup;
        _startupItem.Click += (_, _) =>
        {
            bool next = !_startupItem.Checked;
            _settings.RunAtStartup = next;
            _startupItem.Checked = next;
        };

        _showClaudeItem.Checked = _settings.ShowClaude;
        _showClaudeItem.Click += async (_, _) =>
        {
            bool next = !_showClaudeItem.Checked;
            _settings.ShowClaude = next;
            _settings.Save();
            _showClaudeItem.Checked = next;
            _claudeIcons.ApplyMode(EffectiveMode(next));
            if (!next)
                ResetClaudeAuthState();
            ApplyTimers();
            UpdateAnchor();
            UpdateWidget();
            if (next)
                await RefreshClaudeAsync(force: true);
        };

        _showCodexItem.Checked = _settings.ShowCodex;
        _showCodexItem.Click += async (_, _) =>
        {
            bool next = !_showCodexItem.Checked;
            _settings.ShowCodex = next;
            _settings.Save();
            _showCodexItem.Checked = next;
            _codexIcons.ApplyMode(EffectiveMode(next));
            if (!next)
                ResetCodexAuthState();
            ApplyTimers();
            UpdateAnchor();
            UpdateWidget();
            if (next)
                await RefreshCodexAsync(force: true);
        };

        _showCursorItem.Checked = _settings.ShowCursor;
        _showCursorItem.Click += async (_, _) =>
        {
            bool next = !_showCursorItem.Checked;
            _settings.ShowCursor = next;
            _settings.Save();
            _showCursorItem.Checked = next;
            _cursorIcons.ApplyMode(EffectiveMode(next));
            ApplyTimers();
            UpdateAnchor();
            UpdateWidget();
            if (next)
                await RefreshCursorAsync();
        };

        _showGrokItem.Checked = _settings.ShowGrok;
        _showGrokItem.Click += async (_, _) =>
        {
            bool next = !_showGrokItem.Checked;
            _settings.ShowGrok = next;
            _settings.Save();
            _showGrokItem.Checked = next;
            _grokIcons.ApplyMode(EffectiveMode(next));
            if (!next)
                ResetGrokAuthState();
            ApplyTimers();
            UpdateAnchor();
            UpdateWidget();
            if (next)
                await RefreshGrokAsync(force: true);
        };

        _notifyItem.Checked = _settings.EnableThresholdNotifications;
        _notifyItem.Click += (_, _) =>
        {
            bool next = !_notifyItem.Checked;
            _settings.EnableThresholdNotifications = next;
            _settings.Save();
            _notifyItem.Checked = next;
        };

        _widgetItem.Checked = _settings.ShowTaskbarWidget;
        _widgetItem.Click += (_, _) =>
        {
            bool next = !_widgetItem.Checked;
            _settings.ShowTaskbarWidget = next;
            _settings.Save();
            _widgetItem.Checked = next;
            _taskbarWidget.Visible = next;
            if (next)
                UpdateWidget();
        };

        _updateTimer = new WinTimer { Interval = (int)UpdateCheckInterval.TotalMilliseconds };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(manual: false);
        if (_settings.AutoUpdate && !_paused)
            _updateTimer.Start();

        _autoUpdateItem.Checked = _settings.AutoUpdate;
        _autoUpdateItem.Click += (_, _) =>
        {
            bool next = !_autoUpdateItem.Checked;
            _settings.AutoUpdate = next;
            _settings.Save();
            _autoUpdateItem.Checked = next;
            if (next)
                _updateTimer.Start();
            else
                _updateTimer.Stop();
            // The next hourly tick (or app launch) will pick up any update;
            // we don't trigger an immediate check on opt-in.
        };

        // Defer the initial check briefly so we don't compete with the first
        // poll for network attention, and so a transient startup outage doesn't
        // immediately log a false negative.
        var initialDelayTimer = new WinTimer { Interval = (int)UpdateInitialDelay.TotalMilliseconds };
        initialDelayTimer.Tick += async (s, _) =>
        {
            ((WinTimer)s!).Stop();
            ((WinTimer)s!).Dispose();
            if (!_disposed && _settings.AutoUpdate && !_paused)
                await CheckForUpdatesAsync(manual: false);
        };
        initialDelayTimer.Start();

        _mcpServer = new McpHttpServer(this);
        _mcpServer.Start();
    }

    private static void ShowAbout()
    {
        using var about = new AboutForm();
        about.ShowDialog();
    }

    private static void OpenLogFile()
    {
        // Touch the file first so the shell-launched editor opens an existing
        // file rather than prompting "create new?". Any failure here just falls
        // through to ShellExecute, which will surface the real reason.
        string path = Log.FilePath;
        try
        {
            if (!File.Exists(path))
                Log.Info("Log file opened from tray menu");
        }
        catch { }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "JeekTokenPlanUsage",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnWidgetOffsetChanged(int offset)
    {
        _settings.TaskbarWidgetOffset = offset;
        _settings.Save();
    }

    private void StartSettingsWatcher()
    {
        _settingsWatcher?.Dispose();
        _settingsWatcher = null;

        try
        {
            Directory.CreateDirectory(_settings.RoamingConfigDirectory);
            var watcher = new FileSystemWatcher(_settings.RoamingConfigDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnSettingsDirectoryChanged;
            watcher.Created += OnSettingsDirectoryChanged;
            watcher.Deleted += OnSettingsDirectoryChanged;
            watcher.Renamed += OnSettingsDirectoryChanged;
            _settingsWatcher = watcher;
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings watcher could not start: {ex.Message}");
        }
    }

    private void OnSettingsDirectoryChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed)
            return;

        _uiContext?.Post(_ =>
        {
            if (_disposed)
                return;
            _settingsReloadTimer.Stop();
            _settingsReloadTimer.Start();
        }, null);
    }

    private void ReloadSettingsFromDisk()
    {
        if (_disposed)
            return;

        string previousLanguage = _settings.Language;
        string previousConfigDirectory = _settings.RoamingConfigDirectory;
        try
        {
            _settings.ReloadFromDisk();
            AppProxy.Configure(_settings);
            ApplySettingsFromDisk(previousLanguage);
            if (!string.Equals(
                previousConfigDirectory,
                _settings.RoamingConfigDirectory,
                StringComparison.OrdinalIgnoreCase))
                StartSettingsWatcher();
            Log.Info($"Settings reloaded from {_settings.RoamingSettingsPath}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings reload failed: {ex.Message}");
        }
    }

    private void ApplySettingsFromDisk(string previousLanguage)
    {
        _paused = _settings.Paused;
        _baseIntervalMs = ClampPollMinutes(_settings.PollMinutes) * 60_000;
        _claudeRetryCount = 0;
        _claudeFastPollsRemaining = 0;
        _codexRetryCount = 0;
        _grokRetryCount = 0;
        _claudeTimer.Interval = _baseIntervalMs;
        _codexTimer.Interval = _baseIntervalMs;
        _cursorTimer.Interval = _baseIntervalMs;
        _grokTimer.Interval = _baseIntervalMs;

        _pauseItem.Checked = _paused;
        _refreshItem.Enabled = !_paused;
        _checkUpdateItem.Enabled = !_paused;
        _startupItem.Checked = _settings.RunAtStartup;
        _showClaudeItem.Checked = _settings.ShowClaude;
        _showCodexItem.Checked = _settings.ShowCodex;
        _showCursorItem.Checked = _settings.ShowCursor;
        _showGrokItem.Checked = _settings.ShowGrok;
        _notifyItem.Checked = _settings.EnableThresholdNotifications;
        _widgetItem.Checked = _settings.ShowTaskbarWidget;
        _autoUpdateItem.Checked = _settings.AutoUpdate;
        _taskbarWidget.Visible = _settings.ShowTaskbarWidget;

        UpdateMenuChecks();
        if (!string.Equals(previousLanguage, _settings.Language, StringComparison.Ordinal))
        {
            ApplyUiCulture(_settings.Language);
            RelocalizeMenu();
        }

        _claudeIcons.ApplyMode(EffectiveMode(_settings.ShowClaude));
        _codexIcons.ApplyMode(EffectiveMode(_settings.ShowCodex));
        _cursorIcons.ApplyMode(EffectiveMode(_settings.ShowCursor));
        _grokIcons.ApplyMode(EffectiveMode(_settings.ShowGrok));
        ApplyPausedVisuals();
        ApplyTimers();
        if (_settings.AutoUpdate && !_paused)
            _updateTimer.Start();
        else
            _updateTimer.Stop();
        UpdateAnchor();
        UpdateWidget();

        if (!_paused)
            _ = RefreshAllAsync(forceClaude: true, forceCodex: true, forceGrok: true);
    }

    private void UpdateMenuChecks()
    {
        foreach (ToolStripItem raw in _iconDisplayParent.DropDownItems)
            if (raw is ToolStripMenuItem item && item.Tag is IconDisplayMode mode)
                item.Checked = mode == _settings.IconMode;

        foreach (ToolStripItem raw in _intervalParent.DropDownItems)
            if (raw is ToolStripMenuItem item && item.Tag is int minutes)
                item.Checked = minutes == _settings.PollMinutes;

        foreach (ToolStripItem raw in _languageItem.DropDownItems)
            if (raw is ToolStripMenuItem item && item.Tag is string code)
                item.Checked = code == _settings.Language;

        foreach (ToolStripItem raw in _proxyParent.DropDownItems)
            if (raw is ToolStripMenuItem item && item.Tag is ProxyMode mode)
                item.Checked = mode == _settings.ProxyMode;

        UpdateStorageMenuChecks();
    }

    // Fired (on the UI thread) when Windows broadcasts a light/dark switch. Repaint
    // the theme-aware surfaces at once; the tray icons are re-rendered on the next
    // poll and the context menu re-themes when next shown.
    private void OnSystemThemeChanged()
    {
        if (_disposed)
            return;
        // Re-applying System color mode re-maps SystemColors (Window/ControlText/…)
        // to the new theme — they're otherwise frozen at their startup values — and
        // lets WinForms re-theme its controls/menus. Then repaint our surfaces.
        try { Application.SetColorMode(SystemColorMode.System); } catch { }
        _detailsForm.NotifyThemeChanged();
        _taskbarWidget.NotifyThemeChanged();
    }

    // SystemEvents fires on a worker thread; marshal back to the UI thread before
    // touching timers, snapshots, or HttpClient state.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            PostForceRefresh("system resume");
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock
            || e.Reason == SessionSwitchReason.ConsoleConnect
            || e.Reason == SessionSwitchReason.RemoteConnect)
            PostForceRefresh($"session {e.Reason}");
    }

    private void PostForceRefresh(string reason)
    {
        if (_disposed || _paused)
            return;
        _uiContext?.Post(_ =>
        {
            if (_disposed)
                return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan window = TimeSpan.FromMilliseconds(_baseIntervalMs);

            bool refreshClaude = _settings.ShowClaude && now - _claudeLastPollAt >= window;
            bool refreshCodex = _settings.ShowCodex && now - _codexLastPollAt >= window;
            bool refreshCursor = _settings.ShowCursor && now - _cursorLastPollAt >= window;
            bool refreshGrok = _settings.ShowGrok && now - _grokLastPollAt >= window;

            if (!refreshClaude && !refreshCodex && !refreshCursor && !refreshGrok)
            {
                Log.Info($"Skipping refresh on {reason}; all providers polled within base interval");
                return;
            }

            Log.Info(
                $"Forcing refresh on {reason} (claude={refreshClaude}, codex={refreshCodex}, cursor={refreshCursor}, grok={refreshGrok})");
            if (refreshClaude)
                _ = RefreshClaudeAsync(force: true);
            if (refreshCodex)
                _ = RefreshCodexAsync(force: true);
            if (refreshCursor)
                _ = RefreshCursorAsync();
            if (refreshGrok)
                _ = RefreshGrokAsync(force: true);
        }, null);
    }

    private void OnDisplayMetricsChanged()
    {
        if (_disposed)
            return;
        _taskbarWidget.NotifyDisplayChanged();
    }

    private void UpdateAnchor()
    {
        bool anyIcon = _settings.IconMode != IconDisplayMode.None
            && (_settings.ShowClaude || _settings.ShowCodex || _settings.ShowCursor || _settings.ShowGrok);
        _anchor.Visible = !anyIcon;
    }

    private IconDisplayMode EffectiveMode(bool providerEnabled) =>
        providerEnabled ? _settings.IconMode : IconDisplayMode.None;

    private void ApplyTimers()
    {
        // While paused the app does nothing — keep every poll timer stopped
        // regardless of which providers are enabled. Resuming calls back in here.
        if (_paused)
        {
            _claudeTimer.Stop();
            _codexTimer.Stop();
            _cursorTimer.Stop();
            _grokTimer.Stop();
            return;
        }

        if (_settings.ShowClaude)
            _claudeTimer.Start();
        else
            _claudeTimer.Stop();

        if (_settings.ShowCodex)
            _codexTimer.Start();
        else
            _codexTimer.Stop();

        if (_settings.ShowCursor)
            _cursorTimer.Start();
        else
            _cursorTimer.Stop();

        if (_settings.ShowGrok)
            _grokTimer.Start();
        else
            _grokTimer.Stop();
    }

    // Toggle the global pause. While paused the app performs no work: every poll
    // timer and the update timer are stopped, and all refresh entry points
    // (manual, left-click, resume/unlock) short-circuit. Resuming restarts the
    // timers and forces an immediate refresh so the tray catches up at once.
    private void SetPaused(bool paused)
    {
        if (_paused == paused)
            return;
        _paused = paused;
        _settings.Paused = paused;
        _settings.Save();

        _pauseItem.Checked = paused;
        _refreshItem.Enabled = !paused;
        _checkUpdateItem.Enabled = !paused;
        ApplyPausedVisuals();

        ApplyTimers(); // paused-aware: stops every timer while paused, restarts otherwise
        if (paused)
        {
            _updateTimer.Stop();
            Log.Info("Monitoring paused by user");
        }
        else
        {
            if (_settings.AutoUpdate)
                _updateTimer.Start();
            Log.Info("Monitoring resumed by user");
            _ = RefreshAllAsync(forceClaude: true, forceCodex: true, forceGrok: true);
        }
    }

    // Reflect the paused state on the tray surfaces, so a frozen icon reads as
    // intentionally paused rather than merely stale.
    private void ApplyPausedVisuals()
    {
        _claudeIcons.SetPaused(_paused);
        _codexIcons.SetPaused(_paused);
        _cursorIcons.SetPaused(_paused);
        _grokIcons.SetPaused(_paused);
        Icon rendered = IconRenderer.Render(
            AnchorFrameColor,
            null,
            isError: true,
            placeholder: "T",
            isPaused: _paused);
        _anchor.SetIconAndText(
            rendered,
            _paused
                ? "JeekTokenPlanUsage" + Strings.Tray_PausedSuffix
                : "JeekTokenPlanUsage");
        _anchorIcon?.Dispose();
        _anchorIcon = rendered;
    }

    private void WireIconDisplayMenu()
    {
        foreach (ToolStripItem raw in _iconDisplayParent.DropDownItems)
        {
            if (raw is not ToolStripMenuItem item || item.Tag is not IconDisplayMode mode)
                continue;
            item.Checked = (mode == _settings.IconMode);
            item.Click += (_, _) =>
            {
                _settings.IconMode = mode;
                _settings.Save();
                foreach (ToolStripItem sibling in _iconDisplayParent.DropDownItems)
                    if (sibling is ToolStripMenuItem mi)
                        mi.Checked = ReferenceEquals(mi, item);
                _claudeIcons.ApplyMode(EffectiveMode(_settings.ShowClaude));
                _codexIcons.ApplyMode(EffectiveMode(_settings.ShowCodex));
                _cursorIcons.ApplyMode(EffectiveMode(_settings.ShowCursor));
                _grokIcons.ApplyMode(EffectiveMode(_settings.ShowGrok));
                UpdateAnchor();
            };
        }
    }

    private void WireIntervalMenu()
    {
        foreach (ToolStripItem raw in _intervalParent.DropDownItems)
        {
            if (raw is not ToolStripMenuItem item || item.Tag is not int minutes)
                continue;
            item.Checked = (minutes == _settings.PollMinutes);
            item.Click += (_, _) =>
            {
                _settings.PollMinutes = minutes;
                _settings.Save();
                _baseIntervalMs = minutes * 60_000;
                _claudeRetryCount = 0;
                _claudeFastPollsRemaining = 0;
                _codexRetryCount = 0;
                _grokRetryCount = 0;
                _claudeTimer.Interval = _baseIntervalMs;
                _codexTimer.Interval = _baseIntervalMs;
                _cursorTimer.Interval = _baseIntervalMs;
                _grokTimer.Interval = _baseIntervalMs;
                foreach (ToolStripItem sibling in _intervalParent.DropDownItems)
                    if (sibling is ToolStripMenuItem mi)
                        mi.Checked = ReferenceEquals(mi, item);
            };
        }
    }

    private void WireLanguageMenu()
    {
        foreach (ToolStripItem raw in _languageItem.DropDownItems)
        {
            if (raw is not ToolStripMenuItem item || item.Tag is not string code)
                continue;
            item.Checked = (code == _settings.Language);
            item.Click += (_, _) => SetLanguage(code);
        }
    }

    private void SetLanguage(string code)
    {
        if (code == _settings.Language)
            return;
        _settings.Language = code;
        _settings.Save();

        ApplyUiCulture(code);

        RelocalizeMenu();
        // Tray tooltip text is rebuilt from Strings.* on next refresh tick;
        // trigger one now so the change is immediately visible.
        _ = RefreshAllAsync();
    }

    private static void ApplyUiCulture(string code)
    {
        try
        {
            var culture = string.IsNullOrEmpty(code)
                ? Program.SystemUiCulture
                : CultureInfo.GetCultureInfo(code);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException) { }
    }

    private void WireStorageMenu()
    {
        _storageAppDataItem.Click += (_, _) => SwitchStorageMode(SettingsStorageMode.AppData);
        _storagePortableItem.Click += (_, _) => SwitchStorageMode(SettingsStorageMode.Portable);
        _storageCustomItem.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_settings.CustomStorageRoot))
                ChooseCustomStorageFolder();
            else
                SwitchStorageMode(SettingsStorageMode.Custom, _settings.CustomStorageRoot);
        };
        _storageChooseCustomItem.Click += (_, _) => ChooseCustomStorageFolder();
        UpdateStorageMenuChecks();
    }

    private void ChooseCustomStorageFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Strings.Storage_SelectCustomFolder,
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_settings.CustomStorageRoot)
                ? _settings.CustomStorageRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };

        if (dialog.ShowDialog() == DialogResult.OK)
            SwitchStorageMode(SettingsStorageMode.Custom, dialog.SelectedPath);
    }

    private void SwitchStorageMode(SettingsStorageMode mode, string? customRoot = null, bool showErrorDialog = true)
    {
        try
        {
            _settings.SwitchStorageMode(mode, customRoot);
            StartSettingsWatcher();
            UpdateStorageMenuChecks();
            Log.Info($"Settings storage switched to {_settings.StorageMode}: {_settings.RoamingConfigDirectory}");
        }
        catch (Exception ex)
        {
            if (!showErrorDialog)
                throw;

            MessageBox.Show(
                string.Format(Strings.Storage_SwitchFailedFormat, ex.Message),
                "JeekTokenPlanUsage",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void WireProxyMenu()
    {
        foreach (ToolStripItem raw in _proxyParent.DropDownItems)
        {
            if (raw is not ToolStripMenuItem item || item.Tag is not ProxyMode mode)
                continue;
            item.Checked = (mode == _settings.ProxyMode);
            item.Click += async (_, _) =>
            {
                _settings.ProxyMode = mode;
                _settings.Save();
                foreach (ToolStripItem sibling in _proxyParent.DropDownItems)
                    if (sibling is ToolStripMenuItem mi && mi.Tag is ProxyMode)
                        mi.Checked = ReferenceEquals(mi, item);
                // The next request reads the new mode through AppProxy, but poke
                // a refresh so the change is visible without waiting for a tick.
                await RefreshAllAsync(forceClaude: true, forceCodex: true, forceGrok: true);
            };
        }

        _proxyCustomSettingsItem.Click += async (_, _) =>
        {
            using var dialog = new ProxyForm(
                _settings.ProxyProtocol, _settings.ProxyHost, _settings.ProxyPort);
            if (dialog.ShowDialog() != DialogResult.OK)
                return;
            _settings.ProxyProtocol = dialog.Protocol;
            _settings.ProxyHost = dialog.Host;
            _settings.ProxyPort = dialog.Port;
            // Editing the custom fields implies the user wants custom routing.
            _settings.ProxyMode = ProxyMode.Custom;
            _settings.Save();
            foreach (ToolStripItem sibling in _proxyParent.DropDownItems)
                if (sibling is ToolStripMenuItem mi && mi.Tag is ProxyMode m)
                    mi.Checked = (m == ProxyMode.Custom);
            await RefreshAllAsync(forceClaude: true, forceCodex: true, forceGrok: true);
        };
    }

    private static string ProxyModeLabel(ProxyMode mode) => mode switch
    {
        ProxyMode.System => Strings.Menu_ProxySystem,
        ProxyMode.Direct => Strings.Menu_ProxyDirect,
        ProxyMode.Custom => Strings.Menu_ProxyCustom,
        _ => mode.ToString(),
    };

    private static string StorageModeLabel(SettingsStorageMode mode) => mode switch
    {
        SettingsStorageMode.AppData => Strings.Menu_StorageAppData,
        SettingsStorageMode.Portable => Strings.Menu_StoragePortable,
        SettingsStorageMode.Custom => Strings.Menu_StorageCustom,
        _ => mode.ToString(),
    };

    private void UpdateStorageMenuChecks()
    {
        _storageAppDataItem.Checked = _settings.StorageMode == SettingsStorageMode.AppData;
        _storagePortableItem.Checked = _settings.StorageMode == SettingsStorageMode.Portable;
        _storageCustomItem.Checked = _settings.StorageMode == SettingsStorageMode.Custom;
        _storageChooseCustomItem.ToolTipText = string.IsNullOrWhiteSpace(_settings.CustomStorageRoot)
            ? ""
            : _settings.CustomStorageRoot;
    }

    private void RelocalizeMenu()
    {
        _pauseItem.Text = Strings.Menu_Pause;
        _refreshItem.Text = Strings.Menu_RefreshNow;
        _startupItem.Text = Strings.Menu_RunAtStartup;
        _showClaudeItem.Text = Strings.Menu_ShowClaude;
        _showCodexItem.Text = Strings.Menu_ShowCodex;
        _showCursorItem.Text = Strings.Menu_ShowCursor;
        _showGrokItem.Text = Strings.Menu_ShowGrok;
        _iconDisplayParent.Text = Strings.Menu_IconDisplay;
        _intervalParent.Text = Strings.Menu_RefreshInterval;
        _languageItem.Text = Strings.Menu_Language;
        _languageAutoItem.Text = Strings.Menu_LanguageAuto;
        _notifyItem.Text = Strings.Menu_EnableNotifications;
        _widgetItem.Text = Strings.Menu_ShowTaskbarWidget;
        _storageParent.Text = Strings.Menu_StorageMode;
        _storageAppDataItem.Text = StorageModeLabel(SettingsStorageMode.AppData);
        _storagePortableItem.Text = StorageModeLabel(SettingsStorageMode.Portable);
        _storageCustomItem.Text = StorageModeLabel(SettingsStorageMode.Custom);
        _storageChooseCustomItem.Text = Strings.Menu_StorageChooseCustom;
        _proxyParent.Text = Strings.Menu_Proxy;
        _proxyCustomSettingsItem.Text = Strings.Menu_ProxyCustomSettings;
        foreach (ToolStripItem raw in _proxyParent.DropDownItems)
            if (raw is ToolStripMenuItem mi && mi.Tag is ProxyMode mode)
                mi.Text = ProxyModeLabel(mode);
        _openLogItem.Text = Strings.Menu_OpenLog;
        _checkUpdateItem.Text = Strings.Menu_CheckForUpdates;
        _autoUpdateItem.Text = Strings.Menu_AutoUpdate;
        _exitItem.Text = Strings.Menu_Exit;
        foreach (ToolStripItem raw in _iconDisplayParent.DropDownItems)
            if (raw is ToolStripMenuItem mi && mi.Tag is IconDisplayMode mode)
                mi.Text = IconDisplayLabel(mode);
        foreach (ToolStripItem raw in _intervalParent.DropDownItems)
            if (raw is ToolStripMenuItem mi && mi.Tag is int minutes)
                mi.Text = FormatMinutes(minutes);

        // Language radio state may have changed; update the checkmarks.
        foreach (ToolStripItem raw in _languageItem.DropDownItems)
            if (raw is ToolStripMenuItem mi && mi.Tag is string code)
                mi.Checked = (code == _settings.Language);
        UpdateStorageMenuChecks();

        // Re-render the paused marker in the new language. When not paused this
        // is a no-op suffix; the metric text itself is refreshed by the caller.
        ApplyPausedVisuals();
    }

    private static string IconDisplayLabel(IconDisplayMode mode) => mode switch
    {
        IconDisplayMode.None => Strings.Menu_IconDisplayNone,
        IconDisplayMode.Single => Strings.Menu_IconDisplaySingle,
        IconDisplayMode.Double => Strings.Menu_IconDisplayDouble,
        _ => mode.ToString(),
    };

    private ContextMenuStrip BuildMenu()
    {
        // Drive menu colors from the live system theme (covers the menu and all its
        // submenus) so a runtime light/dark switch is reflected on the next open.
        ToolStripManager.Renderer = new ThemedMenuRenderer();

        var menu = new ContextMenuStrip();

        _pauseItem = new ToolStripMenuItem(Strings.Menu_Pause);

        _refreshItem = new ToolStripMenuItem(Strings.Menu_RefreshNow);
        _refreshItem.Click += async (_, _) => await RefreshAllAsync(forceClaude: true, forceCodex: true, forceGrok: true);

        _startupItem = new ToolStripMenuItem(Strings.Menu_RunAtStartup);
        _showClaudeItem = new ToolStripMenuItem(Strings.Menu_ShowClaude);
        _showCodexItem = new ToolStripMenuItem(Strings.Menu_ShowCodex);
        _showCursorItem = new ToolStripMenuItem(Strings.Menu_ShowCursor);
        _showGrokItem = new ToolStripMenuItem(Strings.Menu_ShowGrok);

        _iconDisplayParent = new ToolStripMenuItem(Strings.Menu_IconDisplay);
        foreach (IconDisplayMode mode in new[] { IconDisplayMode.None, IconDisplayMode.Single, IconDisplayMode.Double })
            _iconDisplayParent.DropDownItems.Add(
                new ToolStripMenuItem(IconDisplayLabel(mode)) { Tag = mode }
            );

        _intervalParent = new ToolStripMenuItem(Strings.Menu_RefreshInterval);
        foreach (int min in AllowedPollMinutes)
            _intervalParent.DropDownItems.Add(
                new ToolStripMenuItem(FormatMinutes(min)) { Tag = min }
            );

        // Language names stay in their native script regardless of the current
        // UI culture — that's the standard convention so users can find their
        // language even when the app is in a script they can't read. The
        // "follow system" item is the only one that gets localized.
        _languageItem = new ToolStripMenuItem(Strings.Menu_Language);
        _languageAutoItem = new ToolStripMenuItem(Strings.Menu_LanguageAuto) { Tag = "" };
        _languageItem.DropDownItems.Add(_languageAutoItem);
        _languageItem.DropDownItems.Add(new ToolStripMenuItem("简体中文") { Tag = "zh-CN" });
        _languageItem.DropDownItems.Add(new ToolStripMenuItem("English") { Tag = "en" });

        _notifyItem = new ToolStripMenuItem(Strings.Menu_EnableNotifications);
        _widgetItem = new ToolStripMenuItem(Strings.Menu_ShowTaskbarWidget);
        _autoUpdateItem = new ToolStripMenuItem(Strings.Menu_AutoUpdate);

        _storageParent = new ToolStripMenuItem(Strings.Menu_StorageMode);
        _storageAppDataItem = new ToolStripMenuItem(StorageModeLabel(SettingsStorageMode.AppData));
        _storagePortableItem = new ToolStripMenuItem(StorageModeLabel(SettingsStorageMode.Portable));
        _storageCustomItem = new ToolStripMenuItem(StorageModeLabel(SettingsStorageMode.Custom));
        _storageChooseCustomItem = new ToolStripMenuItem(Strings.Menu_StorageChooseCustom);
        _storageParent.DropDownItems.Add(_storageAppDataItem);
        _storageParent.DropDownItems.Add(_storagePortableItem);
        _storageParent.DropDownItems.Add(_storageCustomItem);
        _storageParent.DropDownItems.Add(new ToolStripSeparator());
        _storageParent.DropDownItems.Add(_storageChooseCustomItem);

        // Proxy mode radios + a dialog for the custom host/port/protocol. The
        // mode items carry their ProxyMode in Tag, mirroring the interval menu.
        _proxyParent = new ToolStripMenuItem(Strings.Menu_Proxy);
        foreach (ProxyMode mode in new[] { ProxyMode.Direct, ProxyMode.System, ProxyMode.Custom })
            _proxyParent.DropDownItems.Add(
                new ToolStripMenuItem(ProxyModeLabel(mode)) { Tag = mode }
            );
        _proxyParent.DropDownItems.Add(new ToolStripSeparator());
        _proxyCustomSettingsItem = new ToolStripMenuItem(Strings.Menu_ProxyCustomSettings);
        _proxyParent.DropDownItems.Add(_proxyCustomSettingsItem);

        _openLogItem = new ToolStripMenuItem(Strings.Menu_OpenLog);
        _openLogItem.Click += (_, _) => OpenLogFile();

        _checkUpdateItem = new ToolStripMenuItem(Strings.Menu_CheckForUpdates);
        _checkUpdateItem.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);

        _aboutItem = new ToolStripMenuItem(Strings.Menu_About);
        _aboutItem.Click += (_, _) => ShowAbout();

        _exitItem = new ToolStripMenuItem(Strings.Menu_Exit);
        _exitItem.Click += (_, _) => ExitThread();

        menu.Items.Add(_pauseItem);
        menu.Items.Add(_refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(_notifyItem);
        menu.Items.Add(_widgetItem);
        menu.Items.Add(_autoUpdateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_showClaudeItem);
        menu.Items.Add(_showCodexItem);
        menu.Items.Add(_showCursorItem);
        menu.Items.Add(_showGrokItem);
        menu.Items.Add(_iconDisplayParent);
        menu.Items.Add(_intervalParent);
        menu.Items.Add(_languageItem);
        menu.Items.Add(_storageParent);
        menu.Items.Add(_proxyParent);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_checkUpdateItem);
        menu.Items.Add(_openLogItem);
        menu.Items.Add(_aboutItem);
        menu.Items.Add(_exitItem);

        return menu;
    }

    private async Task RefreshAllAsync(bool forceClaude = false, bool forceCodex = false, bool forceGrok = false)
    {
        var tasks = new List<Task>();
        if (_settings.ShowClaude)
            tasks.Add(RefreshClaudeAsync(forceClaude));
        if (_settings.ShowCodex)
            tasks.Add(RefreshCodexAsync(forceCodex));
        if (_settings.ShowCursor)
            tasks.Add(RefreshCursorAsync());
        if (_settings.ShowGrok)
            tasks.Add(RefreshGrokAsync(forceGrok));
        await Task.WhenAll(tasks);
    }

    Task<McpUsageState> IMcpUsageSource.GetUsageAsync(string? provider, bool refresh, CancellationToken ct) =>
        InvokeOnUiThreadAsync(() => GetMcpUsageAsync(provider, refresh), ct);

    Task<McpUiState> IMcpUsageSource.GetUiStateAsync(CancellationToken ct) =>
        InvokeOnUiThreadAsync(() => Task.FromResult(BuildMcpUiState()), ct);

    Task<McpUiActionResult> IMcpUsageSource.InvokeUiActionAsync(McpUiActionRequest request, CancellationToken ct) =>
        InvokeOnUiThreadAsync(() => InvokeMcpUiActionAsync(request), ct);

    private async Task<McpUsageState> GetMcpUsageAsync(string? provider, bool refresh)
    {
        string? normalizedProvider = NormalizeMcpProvider(provider);
        if (refresh)
            await RefreshForMcpAsync(normalizedProvider);
        return BuildMcpUsageState(normalizedProvider);
    }

    private async Task RefreshForMcpAsync(string? provider)
    {
        switch (provider)
        {
            case null:
                await Task.WhenAll(
                    RefreshClaudeAsync(force: true),
                    RefreshCodexAsync(force: true),
                    RefreshCursorAsync(),
                    RefreshGrokAsync(force: true));
                break;

            case "claude":
                await RefreshClaudeAsync(force: true);
                break;

            case "codex":
                await RefreshCodexAsync(force: true);
                break;

            case "cursor":
                await RefreshCursorAsync();
                break;

            case "grok":
                await RefreshGrokAsync(force: true);
                break;
        }
    }

    private McpUsageState BuildMcpUsageState(string? provider)
    {
        var providers = new List<McpProviderState>(4);
        AddProvider("claude", "Claude", _settings.ShowClaude, _claudeLastPollAt, _claudeSnap);
        AddProvider("codex", "Codex", _settings.ShowCodex, _codexLastPollAt, _codexSnap);
        AddProvider("cursor", "Cursor", _settings.ShowCursor, _cursorLastPollAt, _cursorSnap);
        AddProvider("grok", "Grok", _settings.ShowGrok, _grokLastPollAt, _grokSnap);
        return new McpUsageState(DateTimeOffset.UtcNow, _paused, providers);

        void AddProvider(
            string id,
            string name,
            bool enabled,
            DateTimeOffset lastPollAt,
            UsageSnapshot? snapshot)
        {
            if (provider is not null && !provider.Equals(id, StringComparison.Ordinal))
                return;

            providers.Add(new McpProviderState(
                id,
                name,
                enabled,
                lastPollAt == DateTimeOffset.MinValue ? null : lastPollAt,
                snapshot?.Timestamp,
                snapshot?.Error,
                snapshot?.ErrorKind?.ToString().ToLowerInvariant(),
                BuildMcpWindows(id, snapshot)));
        }
    }

    private static IReadOnlyList<McpUsageWindow> BuildMcpWindows(string provider, UsageSnapshot? snapshot)
    {
        return provider switch
        {
            "cursor" => new[]
            {
                new McpUsageWindow("auto", "Auto", snapshot?.FiveHour?.Utilization, snapshot?.FiveHour?.ResetsAt),
                new McpUsageWindow("api", "API", snapshot?.Weekly?.Utilization, snapshot?.Weekly?.ResetsAt),
            },
            "grok" => new[]
            {
                new McpUsageWindow("weekly", "7d", snapshot?.FiveHour?.Utilization, snapshot?.FiveHour?.ResetsAt),
                new McpUsageWindow("monthly", "Mo", snapshot?.Weekly?.Utilization, snapshot?.Weekly?.ResetsAt),
            },
            _ => new[]
            {
                new McpUsageWindow("five_hour", "5h", snapshot?.FiveHour?.Utilization, snapshot?.FiveHour?.ResetsAt),
                new McpUsageWindow("weekly", "Weekly", snapshot?.Weekly?.Utilization, snapshot?.Weekly?.ResetsAt),
            },
        };
    }

    private static string? NormalizeMcpProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        string normalized = provider.Trim().ToLowerInvariant();
        return normalized is "claude" or "codex" or "cursor" or "grok"
            ? normalized
            : throw new ArgumentException($"Unknown provider: {provider}");
    }

    private async Task<McpUiActionResult> InvokeMcpUiActionAsync(McpUiActionRequest request)
    {
        string action = request.Action.Trim().ToLowerInvariant();
        string message;

        switch (action)
        {
            case "refresh":
                await RefreshForMcpAsync(NormalizeMcpProvider(request.Provider));
                message = "Refresh requested.";
                break;

            case "set_paused":
                SetPaused(RequireBool(request.Paused, "paused"));
                message = $"Paused set to {_paused.ToString().ToLowerInvariant()}.";
                break;

            case "set_provider_enabled":
                await SetProviderEnabledForMcpAsync(
                    NormalizeMcpProvider(RequireString(request.Provider, "provider"))!,
                    RequireBool(request.Enabled, "enabled"));
                message = $"Provider {request.Provider} enabled set to {request.Enabled!.Value.ToString().ToLowerInvariant()}.";
                break;

            case "set_icon_display":
                SetIconDisplayForMcp(ParseIconDisplayMode(RequireString(request.Mode, "mode")));
                message = $"Icon display set to {_settings.IconMode}.";
                break;

            case "set_poll_interval":
                SetPollIntervalForMcp(RequireInt(request.Minutes, "minutes"));
                message = $"Poll interval set to {_settings.PollMinutes} minutes.";
                break;

            case "set_language":
                SetLanguage(request.Language ?? "");
                message = string.IsNullOrEmpty(_settings.Language)
                    ? "Language set to system default."
                    : $"Language set to {_settings.Language}.";
                break;

            case "set_threshold_notifications":
                _settings.EnableThresholdNotifications = RequireBool(request.Enabled, "enabled");
                _settings.Save();
                _notifyItem.Checked = _settings.EnableThresholdNotifications;
                message = $"Threshold notifications set to {_settings.EnableThresholdNotifications.ToString().ToLowerInvariant()}.";
                break;

            case "set_taskbar_widget":
                SetTaskbarWidgetForMcp(request.Visible, request.Offset);
                message = $"Taskbar widget visible={_settings.ShowTaskbarWidget.ToString().ToLowerInvariant()}, offset={_settings.TaskbarWidgetOffset}.";
                break;

            case "set_startup":
                _settings.RunAtStartup = RequireBool(request.Enabled, "enabled");
                _startupItem.Checked = _settings.RunAtStartup;
                message = $"Run at startup set to {_settings.RunAtStartup.ToString().ToLowerInvariant()}.";
                break;

            case "set_auto_update":
                SetAutoUpdateForMcp(RequireBool(request.Enabled, "enabled"));
                message = $"Auto update set to {_settings.AutoUpdate.ToString().ToLowerInvariant()}.";
                break;

            case "set_proxy":
                await SetProxyForMcpAsync(request);
                message = $"Proxy set to {FormatProxyMode(_settings.ProxyMode)}.";
                break;

            case "set_storage":
                SetStorageForMcp(request);
                message = $"Storage set to {FormatStorageMode(_settings.StorageMode)}.";
                break;

            case "show_details":
                ShowDetailsForMcp();
                message = "Details popup shown.";
                break;

            case "hide_details":
                _detailsForm.Hide();
                message = "Details popup hidden.";
                break;

            case "toggle_details":
                if (_detailsForm.Visible)
                {
                    _detailsForm.Hide();
                    message = "Details popup hidden.";
                }
                else
                {
                    ShowDetailsForMcp();
                    message = "Details popup shown.";
                }
                break;

            case "open_log":
                OpenLogFile();
                message = $"Log opened: {Log.FilePath}";
                break;

            case "check_update":
                message = await CheckUpdateForMcpAsync(request.AllowUpdateLaunch ?? false);
                break;

            case "show_about":
                ShowAboutForMcp();
                message = "About window shown.";
                break;

            case "exit_app":
                ScheduleExitForMcp();
                message = "Application exit scheduled.";
                break;

            default:
                throw new ArgumentException($"Unknown UI action: {request.Action}");
        }

        return new McpUiActionResult(DateTimeOffset.UtcNow, action, true, message, BuildMcpUiState());
    }

    private McpUiState BuildMcpUiState() => new(
        DateTimeOffset.UtcNow,
        _detailsForm.Visible,
        _anchor.Visible,
        Log.FilePath,
        new McpUiSettings(
            _paused,
            _settings.RunAtStartup,
            _settings.ShowClaude,
            _settings.ShowCodex,
            _settings.ShowCursor,
            _settings.ShowGrok,
            FormatIconDisplayMode(_settings.IconMode),
            _settings.PollMinutes,
            _settings.Language,
            _settings.EnableThresholdNotifications,
            _settings.ShowTaskbarWidget,
            _settings.TaskbarWidgetOffset,
            _settings.AutoUpdate,
            _settings.DisableMirrorDownload,
            FormatProxyMode(_settings.ProxyMode),
            _settings.ProxyProtocol,
            _settings.ProxyHost,
            _settings.ProxyPort,
            FormatStorageMode(_settings.StorageMode),
            _settings.CustomStorageRoot,
            _settings.RoamingConfigDirectory,
            _settings.RoamingSettingsPath),
        new McpUiAllowedValues(
            new[] { "claude", "codex", "cursor", "grok" },
            new[] { "none", "single", "double" },
            AllowedPollMinutes,
            new[] { "", "zh-CN", "en" },
            new[] { "direct", "system", "custom" },
            new[] { "socks5", "http" },
            new[] { "appData", "portable", "custom" },
            new[]
            {
                "refresh",
                "set_paused",
                "set_provider_enabled",
                "set_icon_display",
                "set_poll_interval",
                "set_language",
                "set_threshold_notifications",
                "set_taskbar_widget",
                "set_startup",
                "set_auto_update",
                "set_proxy",
                "set_storage",
                "show_details",
                "hide_details",
                "toggle_details",
                "open_log",
                "check_update",
                "show_about",
                "exit_app",
            }));

    private async Task SetProviderEnabledForMcpAsync(string provider, bool enabled)
    {
        switch (provider)
        {
            case "claude":
                if (_settings.ShowClaude == enabled)
                    return;
                _settings.ShowClaude = enabled;
                _settings.Save();
                _showClaudeItem.Checked = enabled;
                _claudeIcons.ApplyMode(EffectiveMode(enabled));
                if (!enabled)
                    ResetClaudeAuthState();
                ApplyTimers();
                UpdateAnchor();
                UpdateWidget();
                if (enabled)
                    await RefreshClaudeAsync(force: true);
                break;

            case "codex":
                if (_settings.ShowCodex == enabled)
                    return;
                _settings.ShowCodex = enabled;
                _settings.Save();
                _showCodexItem.Checked = enabled;
                _codexIcons.ApplyMode(EffectiveMode(enabled));
                if (!enabled)
                    ResetCodexAuthState();
                ApplyTimers();
                UpdateAnchor();
                UpdateWidget();
                if (enabled)
                    await RefreshCodexAsync(force: true);
                break;

            case "cursor":
                if (_settings.ShowCursor == enabled)
                    return;
                _settings.ShowCursor = enabled;
                _settings.Save();
                _showCursorItem.Checked = enabled;
                _cursorIcons.ApplyMode(EffectiveMode(enabled));
                ApplyTimers();
                UpdateAnchor();
                UpdateWidget();
                if (enabled)
                    await RefreshCursorAsync();
                break;

            case "grok":
                if (_settings.ShowGrok == enabled)
                    return;
                _settings.ShowGrok = enabled;
                _settings.Save();
                _showGrokItem.Checked = enabled;
                _grokIcons.ApplyMode(EffectiveMode(enabled));
                if (!enabled)
                    ResetGrokAuthState();
                ApplyTimers();
                UpdateAnchor();
                UpdateWidget();
                if (enabled)
                    await RefreshGrokAsync(force: true);
                break;
        }
    }

    private void SetIconDisplayForMcp(IconDisplayMode mode)
    {
        _settings.IconMode = mode;
        _settings.Save();
        _claudeIcons.ApplyMode(EffectiveMode(_settings.ShowClaude));
        _codexIcons.ApplyMode(EffectiveMode(_settings.ShowCodex));
        _cursorIcons.ApplyMode(EffectiveMode(_settings.ShowCursor));
        _grokIcons.ApplyMode(EffectiveMode(_settings.ShowGrok));
        foreach (ToolStripItem sibling in _iconDisplayParent.DropDownItems)
            if (sibling is ToolStripMenuItem mi && mi.Tag is IconDisplayMode itemMode)
                mi.Checked = itemMode == mode;
        UpdateAnchor();
    }

    private void SetPollIntervalForMcp(int minutes)
    {
        if (Array.IndexOf(AllowedPollMinutes, minutes) < 0)
            throw new ArgumentException($"Unsupported poll interval: {minutes}");

        _settings.PollMinutes = minutes;
        _settings.Save();
        _baseIntervalMs = minutes * 60_000;
        _claudeRetryCount = 0;
        _claudeFastPollsRemaining = 0;
        _codexRetryCount = 0;
        _grokRetryCount = 0;
        _claudeTimer.Interval = _baseIntervalMs;
        _codexTimer.Interval = _baseIntervalMs;
        _cursorTimer.Interval = _baseIntervalMs;
        _grokTimer.Interval = _baseIntervalMs;
        foreach (ToolStripItem sibling in _intervalParent.DropDownItems)
            if (sibling is ToolStripMenuItem mi && mi.Tag is int itemMinutes)
                mi.Checked = itemMinutes == minutes;
    }

    private void SetTaskbarWidgetForMcp(bool? visible, int? offset)
    {
        if (visible is not null)
            _settings.ShowTaskbarWidget = visible.Value;
        if (offset is not null)
        {
            _settings.TaskbarWidgetOffset = offset.Value;
            _taskbarWidget.SetOffset(offset.Value);
        }
        _settings.Save();
        _widgetItem.Checked = _settings.ShowTaskbarWidget;
        _taskbarWidget.Visible = _settings.ShowTaskbarWidget;
        if (_settings.ShowTaskbarWidget)
            UpdateWidget();
    }

    private void SetAutoUpdateForMcp(bool enabled)
    {
        _settings.AutoUpdate = enabled;
        _settings.Save();
        _autoUpdateItem.Checked = enabled;
        if (enabled && !_paused)
            _updateTimer.Start();
        else
            _updateTimer.Stop();
    }

    private async Task SetProxyForMcpAsync(McpUiActionRequest request)
    {
        if (request.Mode is not null)
            _settings.ProxyMode = ParseProxyMode(request.Mode);
        if (request.Protocol is not null)
            _settings.ProxyProtocol = ParseProxyProtocol(request.Protocol);
        if (request.Host is not null)
            _settings.ProxyHost = string.IsNullOrWhiteSpace(request.Host)
                ? throw new ArgumentException("Proxy host cannot be empty.")
                : request.Host.Trim();
        if (request.Port is not null)
            _settings.ProxyPort = request.Port is > 0 and <= 65535
                ? request.Port.Value
                : throw new ArgumentException($"Invalid proxy port: {request.Port}");

        _settings.Save();
        foreach (ToolStripItem sibling in _proxyParent.DropDownItems)
            if (sibling is ToolStripMenuItem mi && mi.Tag is ProxyMode mode)
                mi.Checked = mode == _settings.ProxyMode;
        await RefreshAllAsync(forceClaude: true, forceCodex: true);
    }

    private void SetStorageForMcp(McpUiActionRequest request)
    {
        SettingsStorageMode mode = ParseStorageMode(RequireString(request.Mode, "mode"));
        SwitchStorageMode(mode, request.CustomRoot, showErrorDialog: false);
    }

    private void ShowDetailsForMcp()
    {
        IReadOnlyList<DetailsForm.Entry> entries = BuildDetailsEntries();
        if (entries.Count == 0)
            throw new InvalidOperationException("No providers are visible.");
        _detailsForm.ShowAt(Cursor.Position, entries);
    }

    private async Task<string> CheckUpdateForMcpAsync(bool allowUpdateLaunch)
    {
        if (_updateInProgress)
            throw new InvalidOperationException("Update check is already in progress.");

        _updateInProgress = true;
        try
        {
            UpdateCheckOutcome outcome = await AutoUpdate.HasUpdateAsync(_settings.DisableMirrorDownload);
            string message = outcome switch
            {
                UpdateCheckOutcome.Available => $"Update available: local={AutoUpdate.LocalCommitCount}, remote={AutoUpdate.RemoteCommitCount}.",
                UpdateCheckOutcome.UpToDate => $"Up to date: local={AutoUpdate.LocalCommitCount}, remote={AutoUpdate.RemoteCommitCount}.",
                UpdateCheckOutcome.Failed => $"Update check failed: {AutoUpdate.FailureReason}",
                _ => outcome.ToString(),
            };

            if (allowUpdateLaunch && outcome == UpdateCheckOutcome.Available)
            {
                bool launched = AutoUpdate.LaunchUpdate();
                message += launched ? " Updater launched." : " Updater launch failed.";
            }

            return message;
        }
        finally
        {
            _updateInProgress = false;
        }
    }

    private void ShowAboutForMcp()
    {
        if (_aboutForm is { IsDisposed: false })
        {
            _aboutForm.Activate();
            return;
        }

        _aboutForm = new AboutForm();
        _aboutForm.FormClosed += (_, _) => _aboutForm = null;
        _aboutForm.Show();
        _aboutForm.Activate();
    }

    private void ScheduleExitForMcp()
    {
        Task.Run(async () =>
        {
            await Task.Delay(500);
            _uiContext?.Post(_ =>
            {
                if (!_disposed)
                    ExitThread();
            }, null);
        });
    }

    private static bool RequireBool(bool? value, string name) =>
        value ?? throw new ArgumentException($"Missing {name}.");

    private static int RequireInt(int? value, string name) =>
        value ?? throw new ArgumentException($"Missing {name}.");

    private static string RequireString(string? value, string name) =>
        value is null ? throw new ArgumentException($"Missing {name}.") : value;

    private static IconDisplayMode ParseIconDisplayMode(string mode) =>
        mode.Trim().ToLowerInvariant() switch
        {
            "none" => IconDisplayMode.None,
            "single" => IconDisplayMode.Single,
            "double" => IconDisplayMode.Double,
            _ => throw new ArgumentException($"Unsupported icon display mode: {mode}"),
        };

    private static ProxyMode ParseProxyMode(string mode) =>
        mode.Trim().ToLowerInvariant() switch
        {
            "direct" => ProxyMode.Direct,
            "system" => ProxyMode.System,
            "custom" => ProxyMode.Custom,
            _ => throw new ArgumentException($"Unsupported proxy mode: {mode}"),
        };

    private static SettingsStorageMode ParseStorageMode(string mode) =>
        mode.Trim().ToLowerInvariant() switch
        {
            "appdata" or "app_data" or "app-data" => SettingsStorageMode.AppData,
            "portable" => SettingsStorageMode.Portable,
            "custom" => SettingsStorageMode.Custom,
            _ => throw new ArgumentException($"Unsupported storage mode: {mode}"),
        };

    private static string ParseProxyProtocol(string protocol) =>
        protocol.Trim().ToLowerInvariant() switch
        {
            "socks" or "socks5" => "socks5",
            "http" or "https" => "http",
            _ => throw new ArgumentException($"Unsupported proxy protocol: {protocol}"),
        };

    private static string FormatIconDisplayMode(IconDisplayMode mode) => mode switch
    {
        IconDisplayMode.None => "none",
        IconDisplayMode.Single => "single",
        IconDisplayMode.Double => "double",
        _ => mode.ToString(),
    };

    private static string FormatProxyMode(ProxyMode mode) => mode switch
    {
        ProxyMode.Direct => "direct",
        ProxyMode.System => "system",
        ProxyMode.Custom => "custom",
        _ => mode.ToString(),
    };

    private static string FormatStorageMode(SettingsStorageMode mode) => mode switch
    {
        SettingsStorageMode.AppData => "appData",
        SettingsStorageMode.Portable => "portable",
        SettingsStorageMode.Custom => "custom",
        _ => mode.ToString(),
    };

    private Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<T>(ct);

        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
            return action();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(async _ =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            try
            {
                tcs.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);

        return tcs.Task.WaitAsync(ct);
    }

    private async Task RefreshClaudeAsync(bool force = false)
    {
        if (_claudeBusy || _disposed || _paused)
            return;
        _claudeBusy = true;
        try
        {
            if (!force && UserActivity.IsAway(IdlePause))
            {
                if (!_claudeIdlePaused)
                {
                    _claudeIdlePaused = true;
                    Log.Info("Claude polling paused while user is idle or workstation is locked");
                }

                _claudeTimer.Interval = ToTimerInterval(IdleCheckInterval);
                return;
            }

            if (_claudeIdlePaused)
            {
                _claudeIdlePaused = false;
                Log.Info("Claude polling resumed after user activity");
            }

            if (_claudeAuthPaused && !force)
            {
                string signature = await Task.Run(ClaudeUsageProvider.CredentialWatchSignature);
                if (signature == _claudeAuthCredentialSignature)
                {
                    _claudeTimer.Interval = _baseIntervalMs;
                    return;
                }

                Log.Info("Claude credential signature changed; resuming auth polling");
                _claudeAuthPaused = false;
            }

            UsageSnapshot snap = await _claude.GetUsageAsync(CancellationToken.None);
            if (_disposed)
                return;
            _claudeLastPollAt = DateTimeOffset.UtcNow;
            _claudeSnap = snap;
            _claudeIcons.Update(snap);
            RefreshDetailsIfVisible();

            if (snap.ErrorKind == UsageErrorKind.Auth)
            {
                _claudeAuthPaused = true;
                _claudeRetryCount = 0;
                _claudeFastPollsRemaining = 0;
                _claudeAuthCredentialSignature = await Task.Run(ClaudeUsageProvider.CredentialWatchSignature);
                _claudeTimer.Interval = _baseIntervalMs;
                Log.Warn("Claude auth polling paused until credentials change");
                NotifyClaudeAuthErrorOnce();
            }
            else
            {
                ResetClaudeAuthState();
                // Setting Interval restarts the countdown — exactly what we want for
                // adapting cadence (backoff on error, fast-poll right after reset).
                _claudeTimer.Interval = NextClaudeIntervalMs(snap);
            }
        }
        finally
        {
            _claudeBusy = false;
        }
    }

    private int NextClaudeIntervalMs(UsageSnapshot snap)
    {
        if (snap.Error is not null)
        {
            _claudeRetryCount++;
            _claudeFastPollsRemaining = 0;
            // 2^(retry-1), capped to avoid shifting past int width.
            int shift = Math.Min(_claudeRetryCount - 1, 16);
            long ms = (long)_baseIntervalMs * (1L << shift);
            long cap = (long)MaxBackoff.TotalMilliseconds;
            return (int)Math.Min(ms, cap);
        }

        _claudeRetryCount = 0;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan? resetDelay = EarliestClaudeResetDelay(snap, now);
        if (resetDelay is { } due && due <= TimeSpan.Zero)
            return ToTimerInterval(ClaudeFastPollAfterReset);

        bool usageIncreased = ClaudeUsageIncreased(_claudeLastSuccessfulSnap, snap);
        _claudeLastSuccessfulSnap = snap;

        if (usageIncreased)
            _claudeFastPollsRemaining = ClaudeActivePollFollowUps + 1;
        else if (_claudeFastPollsRemaining > 0)
            _claudeFastPollsRemaining--;

        int interval = _claudeFastPollsRemaining > 0
            ? Math.Min(_baseIntervalMs, ToTimerInterval(ClaudeActivePollInterval))
            : _baseIntervalMs;

        if (resetDelay is { } upcoming
            && upcoming + ClaudeResetPollBuffer < TimeSpan.FromMilliseconds(interval))
            return ToTimerInterval(upcoming + ClaudeResetPollBuffer);

        return interval;
    }

    private static TimeSpan? EarliestClaudeResetDelay(UsageSnapshot snap, DateTimeOffset now)
    {
        TimeSpan? earliest = null;
        Add(snap.FiveHour?.ResetsAt);
        Add(snap.Weekly?.ResetsAt);
        return earliest;

        void Add(DateTimeOffset? reset)
        {
            if (reset is null)
                return;

            TimeSpan delay = reset.Value - now;
            if (earliest is null || delay < earliest.Value)
                earliest = delay;
        }
    }

    private static bool ClaudeUsageIncreased(UsageSnapshot? previous, UsageSnapshot current) =>
        previous is not null
        && (MetricIncreased(previous.FiveHour, current.FiveHour)
            || MetricIncreased(previous.Weekly, current.Weekly));

    private static bool MetricIncreased(UsageMetric? previous, UsageMetric? current) =>
        previous is not null
        && current is not null
        && current.Utilization > previous.Utilization + 0.05;

    private static int ToTimerInterval(TimeSpan interval)
    {
        long ms = (long)Math.Ceiling(interval.TotalMilliseconds);
        if (ms < 1)
            return 1;
        return ms > int.MaxValue ? int.MaxValue : (int)ms;
    }

    private void NotifyClaudeAuthErrorOnce()
    {
        if (_claudeAuthNotified)
            return;

        _claudeAuthNotified = true;
        bool shown = _claudeIcons.ShowNotification(Strings.Claude_AuthRequiredTitle, Strings.Claude_AuthRequiredBody);
        if (!shown)
            shown = _anchor.ShowNotification(Strings.Claude_AuthRequiredTitle, Strings.Claude_AuthRequiredBody);

        if (!shown)
            Log.Warn("Claude auth notification was not shown because no tray icon was available");
    }

    private void ResetClaudeAuthState()
    {
        _claudeAuthPaused = false;
        _claudeAuthNotified = false;
        _claudeAuthCredentialSignature = string.Empty;
    }

    private async Task RefreshCodexAsync(bool force = false)
    {
        if (_codexBusy || _disposed || _paused)
            return;
        _codexBusy = true;
        try
        {
            if (!force && UserActivity.IsAway(IdlePause))
            {
                if (!_codexIdlePaused)
                {
                    _codexIdlePaused = true;
                    Log.Info("Codex polling paused while user is idle or workstation is locked");
                }

                _codexTimer.Interval = ToTimerInterval(IdleCheckInterval);
                return;
            }

            if (_codexIdlePaused)
            {
                _codexIdlePaused = false;
                Log.Info("Codex polling resumed after user activity");
            }

            if (_codexAuthPaused && !force)
            {
                string signature = await Task.Run(CodexUsageProvider.CredentialWatchSignature);
                if (signature == _codexAuthCredentialSignature)
                {
                    _codexTimer.Interval = _baseIntervalMs;
                    return;
                }

                Log.Info("Codex credential signature changed; resuming auth polling");
                _codexAuthPaused = false;
            }

            UsageSnapshot snap = await _codex.GetUsageAsync(CancellationToken.None);
            if (_disposed)
                return;
            _codexLastPollAt = DateTimeOffset.UtcNow;
            _codexSnap = snap;
            _codexIcons.Update(snap);
            RefreshDetailsIfVisible();

            if (snap.ErrorKind == UsageErrorKind.Auth)
            {
                _codexAuthPaused = true;
                _codexRetryCount = 0;
                _codexAuthCredentialSignature = await Task.Run(CodexUsageProvider.CredentialWatchSignature);
                _codexTimer.Interval = _baseIntervalMs;
                Log.Warn("Codex auth polling paused until credentials change");
                NotifyCodexAuthErrorOnce();
            }
            else
            {
                ResetCodexAuthState();
                _codexTimer.Interval = NextCodexIntervalMs(snap);
            }
        }
        finally
        {
            _codexBusy = false;
        }
    }

    // Codex doesn't expose reset times the way Claude does, so the only signal
    // we adapt on is success/failure: back off geometrically while errors persist
    // (curl DNS / TLS / Cloudflare hiccups) so the log isn't flooded, and snap
    // back to the user-chosen interval the moment a poll succeeds.
    private int NextCodexIntervalMs(UsageSnapshot snap)
    {
        if (snap.Error is not null)
        {
            _codexRetryCount++;
            int shift = Math.Min(_codexRetryCount - 1, 16);
            long ms = (long)_baseIntervalMs * (1L << shift);
            long cap = (long)MaxBackoff.TotalMilliseconds;
            return (int)Math.Min(ms, cap);
        }

        _codexRetryCount = 0;
        return _baseIntervalMs;
    }

    private void NotifyCodexAuthErrorOnce()
    {
        if (_codexAuthNotified)
            return;

        _codexAuthNotified = true;
        bool shown = _codexIcons.ShowNotification(Strings.Codex_AuthRequiredTitle, Strings.Codex_AuthRequiredBody);
        if (!shown)
            shown = _anchor.ShowNotification(Strings.Codex_AuthRequiredTitle, Strings.Codex_AuthRequiredBody);

        if (!shown)
            Log.Warn("Codex auth notification was not shown because no tray icon was available");
    }

    private void ResetCodexAuthState()
    {
        _codexAuthPaused = false;
        _codexAuthNotified = false;
        _codexAuthCredentialSignature = string.Empty;
    }

    private async Task RefreshCursorAsync()
    {
        if (_cursorBusy || _disposed || _paused)
            return;
        _cursorBusy = true;
        try
        {
            UsageSnapshot snap = await _cursor.GetUsageAsync(CancellationToken.None);
            if (_disposed)
                return;
            _cursorLastPollAt = DateTimeOffset.UtcNow;
            _cursorSnap = snap;
            _cursorIcons.Update(snap);
            RefreshDetailsIfVisible();
        }
        finally
        {
            _cursorBusy = false;
        }
    }

    private async Task RefreshGrokAsync(bool force = false)
    {
        if (_grokBusy || _disposed || _paused)
            return;
        _grokBusy = true;
        try
        {
            if (!force && UserActivity.IsAway(IdlePause))
            {
                if (!_grokIdlePaused)
                {
                    _grokIdlePaused = true;
                    Log.Info("Grok polling paused while user is idle or workstation is locked");
                }

                _grokTimer.Interval = ToTimerInterval(IdleCheckInterval);
                return;
            }

            if (_grokIdlePaused)
            {
                _grokIdlePaused = false;
                Log.Info("Grok polling resumed after user activity");
            }

            if (_grokAuthPaused && !force)
            {
                string signature = await Task.Run(GrokUsageProvider.CredentialWatchSignature);
                if (signature == _grokAuthCredentialSignature)
                {
                    _grokTimer.Interval = _baseIntervalMs;
                    return;
                }

                Log.Info("Grok credential signature changed; resuming auth polling");
                _grokAuthPaused = false;
            }

            UsageSnapshot snap = await _grok.GetUsageAsync(CancellationToken.None);
            if (_disposed)
                return;
            _grokLastPollAt = DateTimeOffset.UtcNow;
            _grokSnap = snap;
            _grokIcons.Update(snap);
            RefreshDetailsIfVisible();

            if (snap.ErrorKind == UsageErrorKind.Auth)
            {
                _grokAuthPaused = true;
                _grokRetryCount = 0;
                _grokAuthCredentialSignature = await Task.Run(GrokUsageProvider.CredentialWatchSignature);
                _grokTimer.Interval = _baseIntervalMs;
                Log.Warn("Grok auth polling paused until credentials change");
                NotifyGrokAuthErrorOnce();
            }
            else
            {
                ResetGrokAuthState();
                _grokTimer.Interval = NextGrokIntervalMs(snap);
            }
        }
        finally
        {
            _grokBusy = false;
        }
    }

    private int NextGrokIntervalMs(UsageSnapshot snap)
    {
        if (snap.Error is not null)
        {
            _grokRetryCount++;
            int shift = Math.Min(_grokRetryCount - 1, 16);
            long ms = (long)_baseIntervalMs * (1L << shift);
            long cap = (long)MaxBackoff.TotalMilliseconds;
            return (int)Math.Min(ms, cap);
        }

        _grokRetryCount = 0;
        return _baseIntervalMs;
    }

    private void NotifyGrokAuthErrorOnce()
    {
        if (_grokAuthNotified)
            return;

        _grokAuthNotified = true;
        bool shown = _grokIcons.ShowNotification(Strings.Grok_AuthRequiredTitle, Strings.Grok_AuthRequiredBody);
        if (!shown)
            shown = _anchor.ShowNotification(Strings.Grok_AuthRequiredTitle, Strings.Grok_AuthRequiredBody);

        if (!shown)
            Log.Warn("Grok auth notification was not shown because no tray icon was available");
    }

    private void ResetGrokAuthState()
    {
        _grokAuthPaused = false;
        _grokAuthNotified = false;
        _grokAuthCredentialSignature = string.Empty;
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateInProgress || _disposed || _paused)
            return;
        _updateInProgress = true;
        try
        {
            Log.Info($"AutoUpdate check started (manual={manual})");
            UpdateCheckOutcome outcome = await AutoUpdate.HasUpdateAsync(_settings.DisableMirrorDownload);
            if (_disposed)
                return;

            switch (outcome)
            {
                case UpdateCheckOutcome.Available:
                    string body = string.Format(
                        Strings.Update_FoundBodyFormat,
                        AutoUpdate.RemoteCommitCount > 0 ? AutoUpdate.RemoteCommitCount.ToString() : "?");
                    ShowUpdateToast(Strings.Update_FoundTitle, body);
                    // Give the toast a moment to render before the process exits.
                    await Task.Delay(800);
                    if (_disposed)
                        return;
                    bool launched = AutoUpdate.LaunchUpdate();
                    if (!launched && manual)
                    {
                        ShowUpdateToast(
                            Strings.Update_NoneTitle,
                            string.Format(Strings.Update_FailedFormat, "launch failed"));
                    }
                    break;

                case UpdateCheckOutcome.UpToDate when manual:
                    ShowUpdateToast(Strings.Update_NoneTitle, Strings.Update_NoneBody);
                    break;

                case UpdateCheckOutcome.Failed when manual:
                    // Surface failure to the user — they pressed the button and
                    // deserve to know the check didn't actually conclude. Auto
                    // checks stay silent (already logged) to avoid nagging.
                    ShowUpdateToast(
                        Strings.Update_NoneTitle,
                        string.Format(Strings.Update_FailedFormat,
                            string.IsNullOrEmpty(AutoUpdate.FailureReason) ? "unknown" : AutoUpdate.FailureReason));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"AutoUpdate check threw: {ex.Message}");
            if (manual)
            {
                ShowUpdateToast(
                    Strings.Update_NoneTitle,
                    string.Format(Strings.Update_FailedFormat, ex.Message));
            }
        }
        finally
        {
            _updateInProgress = false;
        }
    }

    private void ShowUpdateToast(string title, string body)
    {
        // Route the toast through whichever icon is currently visible.
        if (_anchor.Visible && _anchor.ShowNotification(title, body))
            return;
        if (_settings.ShowClaude && _claudeIcons.ShowNotification(title, body))
            return;
        if (_settings.ShowCodex && _codexIcons.ShowNotification(title, body))
            return;
        if (_settings.ShowCursor && _cursorIcons.ShowNotification(title, body))
            return;
        if (_settings.ShowGrok && _grokIcons.ShowNotification(title, body))
            return;
        Log.Warn("AutoUpdate toast had no visible tray icon to surface through");
    }

    private void HandleLeftClick()
    {
        if (_disposed)
            return;
        if (_detailsForm.Visible)
        {
            _detailsForm.Hide();
            return;
        }
        // After a hook-driven hide (clicking the icon while the popup is open),
        // the icon's NIN_SELECT arrives next. Without this guard we'd reopen
        // immediately and make the icon feel un-toggleable.
        if (_detailsForm.WasRecentlyHidden())
            return;

        IReadOnlyList<DetailsForm.Entry> entries = BuildDetailsEntries();
        if (entries.Count == 0)
            return; // All providers hidden — nothing to show; right-click for menu.

        _detailsForm.ShowAt(Cursor.Position, entries);
        // Refresh in the background so the popup reflects current state without
        // forcing the user to wait. RefreshDetailsIfVisible() will repaint rows
        // as each provider completes. When the user opens the popup with stale
        // or missing data on a provider (no snapshot yet, or the last poll
        // errored), force-refresh that provider so the click bypasses the idle
        // and auth-paused gates — the user explicitly asked for fresh data.
        bool forceClaude = _settings.ShowClaude && IsSnapshotMissing(_claudeSnap);
        bool forceCodex = _settings.ShowCodex && IsSnapshotMissing(_codexSnap);
        bool forceGrok = _settings.ShowGrok && IsSnapshotMissing(_grokSnap);
        _ = RefreshAllAsync(forceClaude, forceCodex, forceGrok);
    }

    private static bool IsSnapshotMissing(UsageSnapshot? snap) =>
        snap is null || snap.Error is not null;

    private void RefreshDetailsIfVisible()
    {
        if (_detailsForm is { Visible: true })
            _detailsForm.UpdateRows(BuildDetailsEntries());
        UpdateWidget();
    }

    private void UpdateWidget()
    {
        if (!_settings.ShowTaskbarWidget)
            return;
        _taskbarWidget.Update(BuildWidgetColumns());
    }

    private IReadOnlyList<TaskbarWidget.Column> BuildWidgetColumns()
    {
        var list = new List<TaskbarWidget.Column>(4);
        if (_settings.ShowClaude)
            list.Add(BuildColumn(_claudeIcons, _claudeSnap));
        if (_settings.ShowCodex)
            list.Add(BuildColumn(_codexIcons, _codexSnap));
        if (_settings.ShowCursor)
            list.Add(BuildColumn(_cursorIcons, _cursorSnap));
        if (_settings.ShowGrok)
            list.Add(BuildColumn(_grokIcons, _grokSnap));
        return list;
    }

    private static TaskbarWidget.Column BuildColumn(ProviderIcons icons, UsageSnapshot? snap)
    {
        // Both rows' labels use the primary (bright) accent so the secondary row's
        // deeper shade doesn't render as hard-to-read text on the taskbar.
        Color labelColor = icons.PrimarySpec.Bg;
        return new(
            BuildCell(icons.PrimarySpec, labelColor, snap?.FiveHour, snap?.Error),
            BuildCell(icons.SecondarySpec, labelColor, snap?.Weekly, snap?.Error));
    }

    private static TaskbarWidget.Cell BuildCell(
        WindowSpec spec, Color labelColor, UsageMetric? metric, string? error) =>
        new(
            Label: spec.Label,
            LabelColor: labelColor,
            Accent: spec.Bg,
            Percent: error is null ? metric?.Utilization : null,
            IsError: error is not null,
            ResetsAt: error is null ? metric?.ResetsAt : null);

    private IReadOnlyList<DetailsForm.Entry> BuildDetailsEntries()
    {
        var list = new List<DetailsForm.Entry>(8);
        if (_settings.ShowClaude)
        {
            list.Add(BuildEntry("Claude", _claudeIcons, _claudeSnap, primary: true));
            list.Add(BuildEntry("Claude", _claudeIcons, _claudeSnap, primary: false));
        }
        if (_settings.ShowCodex)
        {
            list.Add(BuildEntry("Codex", _codexIcons, _codexSnap, primary: true));
            list.Add(BuildEntry("Codex", _codexIcons, _codexSnap, primary: false));
        }
        if (_settings.ShowCursor)
        {
            list.Add(BuildEntry("Cursor", _cursorIcons, _cursorSnap, primary: true));
            list.Add(BuildEntry("Cursor", _cursorIcons, _cursorSnap, primary: false));
        }
        if (_settings.ShowGrok)
        {
            list.Add(BuildEntry("Grok", _grokIcons, _grokSnap, primary: true));
            list.Add(BuildEntry("Grok", _grokIcons, _grokSnap, primary: false));
        }
        return list;
    }

    private static DetailsForm.Entry BuildEntry(
        string displayName, ProviderIcons icons, UsageSnapshot? snap, bool primary)
    {
        WindowSpec spec = primary ? icons.PrimarySpec : icons.SecondarySpec;
        UsageMetric? metric = primary ? snap?.FiveHour : snap?.Weekly;
        return new DetailsForm.Entry(
            ProviderName: displayName,
            WindowLabel: spec.Label,
            WindowColor: spec.Bg,
            LongDate: spec.LongDate,
            Metric: metric,
            Error: snap?.Error);
    }

    private static int ClampPollMinutes(int minutes) =>
        Array.IndexOf(AllowedPollMinutes, minutes) >= 0 ? minutes : 5;

    private static string FormatMinutes(int minutes) =>
        minutes < 60
            ? string.Format(Strings.Interval_MinutesFormat, minutes)
            : string.Format(Strings.Interval_HoursFormat, minutes / 60);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _mcpServer.Dispose();
            _claudeTimer.Dispose();
            _codexTimer.Dispose();
            _cursorTimer.Dispose();
            _grokTimer.Dispose();
            _updateTimer.Dispose();
            _settingsReloadTimer.Dispose();
            _settingsWatcher?.Dispose();
            _claudeIcons.Dispose();
            _codexIcons.Dispose();
            _cursorIcons.Dispose();
            _grokIcons.Dispose();
            _anchor.Visible = false;
            _anchor.Dispose();
            _anchorIcon?.Dispose();
            _aboutForm?.Dispose();
            _detailsForm.Dispose();
            _taskbarWidget.Dispose();
            _systemChangeListener.Dispose();
        }
        base.Dispose(disposing);
    }

    /// Per-window display metadata: background color, short label shown in tooltip,
    /// and whether the reset time needs a date prefix (long windows like weekly or
    /// monthly billing cycles) or can omit it (short windows like 5-hour).
    private sealed record WindowSpec(Color Bg, string Label, bool LongDate);

    /// Hidden top-level window that listens for system theme and display changes.
    /// Windows broadcasts WM_SETTINGCHANGE("ImmersiveColorSet") (and WM_THEMECHANGED)
    /// to top-level windows only — our embedded taskbar widget is a child window and
    /// wouldn't reliably receive it, so we watch here and fan notifications out.
    /// The handle is created on the UI thread, so WndProc and callbacks run there.
    private sealed class SystemChangeListener : NativeWindow, IDisposable
    {
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_THEMECHANGED = 0x031A;
        private const int WM_DPICHANGED = 0x02E0;

        private readonly Action _onThemeChange;
        private readonly Action _onDisplayChange;

        public SystemChangeListener(Action onThemeChange, Action onDisplayChange)
        {
            _onThemeChange = onThemeChange;
            _onDisplayChange = onDisplayChange;
            CreateHandle(new CreateParams { Caption = "JeekSystemChangeWatcher" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_THEMECHANGED)
            {
                _onThemeChange();
            }
            else if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED)
            {
                _onDisplayChange();
            }
            else if (m.Msg == WM_SETTINGCHANGE)
            {
                string? area = m.LParam != IntPtr.Zero ? Marshal.PtrToStringUni(m.LParam) : null;
                if (area == "ImmersiveColorSet")
                    _onThemeChange();
                else
                    _onDisplayChange();
            }
            base.WndProc(ref m);
        }

        public void Dispose() => DestroyHandle();
    }

    /// Which of the two windows is the one shown when IconMode = Single. Claude
    /// and Codex surface 5h (Primary); Cursor surfaces API (Secondary).
    private enum SingleModeWindow { Primary, Secondary }

    /// Tracks the highest threshold (in NotificationThresholds) we've already
    /// notified for the current window cycle. Resets when the window's ResetsAt
    /// changes — i.e., the window rolled over to a new cycle.
    private sealed class WindowThresholdState
    {
        public DateTimeOffset? LastSeenReset;
        public int LastNotifiedThreshold;
    }

    /// Holds two tray icons (primary + secondary window) for one provider.
    private sealed class ProviderIcons : IDisposable
    {
        private static readonly int[] NotificationThresholds = { 80, 95 };
        private const int TooltipRemainingWidth = 7;

        private readonly string _displayName;
        private readonly WindowSpec _primarySpec;
        private readonly WindowSpec _secondarySpec;
        private readonly SingleModeWindow _singleModeWindow;
        private readonly TrayIcon _primary;
        private readonly TrayIcon _secondary;
        private readonly Func<bool> _notifyEnabled;
        private readonly WindowThresholdState _primaryThreshold = new();
        private readonly WindowThresholdState _secondaryThreshold = new();
        private readonly int _tooltipWindowLabelWidth;
        private Icon? _primaryIcon;
        private Icon? _secondaryIcon;
        private IconDisplayMode _mode = IconDisplayMode.Double;
        private UsageSnapshot? _lastSnap;
        private bool _paused;

        public ProviderIcons(
            string displayName,
            WindowSpec primary,
            WindowSpec secondary,
            SingleModeWindow singleModeWindow,
            Guid primaryGuid,
            Guid secondaryGuid,
            ContextMenuStrip menu,
            Func<bool> notifyEnabled,
            Action onLeftClick
        )
        {
            _displayName = displayName;
            _primarySpec = primary;
            _secondarySpec = secondary;
            _singleModeWindow = singleModeWindow;
            _notifyEnabled = notifyEnabled;
            _tooltipWindowLabelWidth = Math.Max(primary.Label.Length, secondary.Label.Length);

            // Keep the rendered Icon objects tracked so they are disposed when
            // replaced; TrayIcon owns a separate HICON copy for the shell.
            _primaryIcon = IconRenderer.Render(primary.Bg, null, isError: true);
            _secondaryIcon = IconRenderer.Render(secondary.Bg, null, isError: true);

            _primary = new TrayIcon(primaryGuid, menu)
            {
                Icon = _primaryIcon,
                Text = Strings.Tray_Loading,
                LeftClick = onLeftClick,
            };
            _secondary = new TrayIcon(secondaryGuid, menu)
            {
                Icon = _secondaryIcon,
                Text = Strings.Tray_Loading,
                LeftClick = onLeftClick,
            };
        }

        public WindowSpec PrimarySpec => _primarySpec;
        public WindowSpec SecondarySpec => _secondarySpec;

        /// Switches between None / Single / Double. Updates icon content for the
        /// new mode using the most recent snapshot (if any), then applies tray
        /// visibility. State first / visibility second ensures the shell never
        /// renders stale content when an icon becomes visible.
        public void ApplyMode(IconDisplayMode mode)
        {
            _mode = mode;
            if (_lastSnap is not null)
                RenderState(_lastSnap);

            bool primaryVisible = mode == IconDisplayMode.Double
                || (mode == IconDisplayMode.Single && _singleModeWindow == SingleModeWindow.Primary);
            bool secondaryVisible = mode == IconDisplayMode.Double
                || (mode == IconDisplayMode.Single && _singleModeWindow == SingleModeWindow.Secondary);
            _primary.Visible = primaryVisible;
            _secondary.Visible = secondaryVisible;
        }

        public void Update(UsageSnapshot snap)
        {
            _lastSnap = snap;
            if (_mode == IconDisplayMode.None)
                return;
            RenderState(snap);
        }

        // Re-render icons and tooltips to add/remove the paused marker. The
        // metric value stays frozen while paused; only the status overlay changes.
        public void SetPaused(bool paused)
        {
            _paused = paused;
            if (_lastSnap is not null)
            {
                RenderState(_lastSnap);
            }
            else
            {
                string text = paused
                    ? Strings.Tray_Loading + Strings.Tray_PausedSuffix
                    : Strings.Tray_Loading;
                ApplyPlaceholderIcon(_primary, ref _primaryIcon, _primarySpec, text);
                ApplyPlaceholderIcon(_secondary, ref _secondaryIcon, _secondarySpec, text);
            }
        }

        public bool ShowNotification(string title, string message)
        {
            return _mode switch
            {
                IconDisplayMode.Double => _primary.ShowNotification(title, message),
                IconDisplayMode.Single when _singleModeWindow == SingleModeWindow.Primary => _primary.ShowNotification(title, message),
                IconDisplayMode.Single => _secondary.ShowNotification(title, message),
                _ => false,
            };
        }

        private void RenderState(UsageSnapshot snap)
        {
            if (_mode == IconDisplayMode.None)
                return;

            bool error = snap.Error is not null;

            if (_mode == IconDisplayMode.Double)
            {
                ApplyTo(_primary, ref _primaryIcon, _primarySpec, snap.FiveHour, error, snap.Error, _primaryThreshold,
                    BuildLine(_primarySpec, snap.FiveHour, error, snap.Error));
                ApplyTo(_secondary, ref _secondaryIcon, _secondarySpec, snap.Weekly, error, snap.Error, _secondaryThreshold,
                    BuildLine(_secondarySpec, snap.Weekly, error, snap.Error));
                return;
            }

            // Single mode: render the chosen icon with a two-line tooltip
            // showing both windows. Notifications for the hidden window are
            // routed through the visible icon so threshold alerts still fire.
            string twoLine = TruncateLine(BuildLine(_primarySpec, snap.FiveHour, error, snap.Error))
                + "\r\n"
                + TruncateLine(BuildLine(_secondarySpec, snap.Weekly, error, snap.Error));

            if (_singleModeWindow == SingleModeWindow.Primary)
            {
                ApplyTo(_primary, ref _primaryIcon, _primarySpec, snap.FiveHour, error, snap.Error, _primaryThreshold, twoLine);
                if (!error && snap.Weekly is not null)
                    CheckThresholds(_primary, _secondarySpec, snap.Weekly, _secondaryThreshold);
            }
            else
            {
                ApplyTo(_secondary, ref _secondaryIcon, _secondarySpec, snap.Weekly, error, snap.Error, _secondaryThreshold, twoLine);
                if (!error && snap.FiveHour is not null)
                    CheckThresholds(_secondary, _primarySpec, snap.FiveHour, _primaryThreshold);
            }
        }

        private string BuildLine(WindowSpec spec, UsageMetric? metric, bool error, string? errorText) =>
            error ? $"{PaddedTooltipLabel(spec)}: {errorText}"
            : metric is null ? $"{PaddedTooltipLabel(spec)}: {Strings.Tray_NoData}"
            : $"{PaddedTooltipLabel(spec)}: {metric.Utilization,5:0.#}% · {UsageFormatting.FormatResetAligned(metric.ResetsAt, spec.LongDate, TooltipRemainingWidth)}";

        private string PaddedTooltipLabel(WindowSpec spec) =>
            $"{_displayName} {spec.Label.PadRight(_tooltipWindowLabelWidth)}";

        private void ApplyTo(
            TrayIcon target,
            ref Icon? current,
            WindowSpec spec,
            UsageMetric? metric,
            bool error,
            string? errorText,
            WindowThresholdState thresholdState,
            string tooltip
        )
        {
            Icon rendered = IconRenderer.Render(
                spec.Bg,
                metric?.Utilization,
                error || metric is null,
                isPaused: _paused
            );
            string text = Truncate(_paused ? tooltip + Strings.Tray_PausedSuffix : tooltip);
            ReplaceIconAndText(target, ref current, rendered, text);

            if (!error && metric is not null)
                CheckThresholds(target, spec, metric, thresholdState);
        }

        private void ApplyPlaceholderIcon(
            TrayIcon target,
            ref Icon? current,
            WindowSpec spec,
            string text)
        {
            ReplaceIconAndText(
                target,
                ref current,
                IconRenderer.Render(spec.Bg, null, isError: true, isPaused: _paused),
                text);
        }

        private static void ReplaceIconAndText(TrayIcon target, ref Icon? current, Icon rendered, string text)
        {
            target.SetIconAndText(rendered, text);
            current?.Dispose();
            current = rendered;
        }

        // Each (window, threshold) fires at most once per window cycle. We treat
        // a change in ResetsAt as a window rollover and re-arm; that way a user
        // who hovers around 80% doesn't get spammed.
        private void CheckThresholds(
            TrayIcon target,
            WindowSpec spec,
            UsageMetric metric,
            WindowThresholdState state)
        {
            if (state.LastSeenReset != metric.ResetsAt)
            {
                state.LastSeenReset = metric.ResetsAt;
                state.LastNotifiedThreshold = 0;
            }

            if (!_notifyEnabled())
                return;

            int crossed = 0;
            foreach (int t in NotificationThresholds)
                if (metric.Utilization >= t)
                    crossed = t;

            if (crossed <= state.LastNotifiedThreshold)
                return;

            target.ShowNotification(
                Strings.Notify_Title,
                string.Format(
                    Strings.Notify_BodyFormat,
                    _displayName,
                    spec.Label,
                    metric.Utilization.ToString("0.#")));
            state.LastNotifiedThreshold = crossed;
        }

        // NIF_TIP allows up to 127 wchars (TrayIcon enforces the hard cap);
        // single-icon two-line tooltips need the headroom. Per-line truncation
        // keeps both lines visible when one is unusually long (e.g. error text).
        private static string Truncate(string s) => s.Length <= 127 ? s : s[..127];
        private static string TruncateLine(string s) => s.Length <= 60 ? s : s[..60];

        public void Dispose()
        {
            _primary.Visible = false;
            _secondary.Visible = false;
            _primary.Dispose();
            _secondary.Dispose();
            _primaryIcon?.Dispose();
            _secondaryIcon?.Dispose();
        }
    }
}
