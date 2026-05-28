using System.Globalization;
using System.Runtime.InteropServices;
using JeekTokenPlanUsage.Resources;
using WinTimer = System.Windows.Forms.Timer;

namespace JeekTokenPlanUsage;

public sealed class TrayApplicationContext : ApplicationContext
{
    // Allowed base polling intervals (minutes), shared across all three
    // providers. Claude's fallback path may consume quota at the shorter end;
    // 1 minute is offered for users who accept that cost in exchange for
    // fresher data.
    private static readonly int[] AllowedPollMinutes = { 1, 5, 10, 30, 60 };

    // After a successful Claude poll whose returned reset time is already in
    // the past, poll again at this cadence until the API rolls over to a new
    // window. Claude-only; Codex / Cursor don't need this.
    private static readonly TimeSpan ClaudeFastPollAfterReset = TimeSpan.FromSeconds(10);

    // While Claude usage is actively increasing, keep a short burst of faster
    // polls so the tray catches up without permanently raising fallback cost.
    private static readonly TimeSpan ClaudeActivePollInterval = TimeSpan.FromMinutes(2);
    private const int ClaudeActivePollFollowUps = 2;

    // Timer-driven Claude polls pause while the user is away; manual refresh
    // still bypasses this so the menu stays responsive.
    private static readonly TimeSpan ClaudeIdlePause = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClaudeIdleCheckInterval = TimeSpan.FromSeconds(30);

    // Poll just after an upcoming reset instead of waiting a full base interval.
    private static readonly TimeSpan ClaudeResetPollBuffer = TimeSpan.FromSeconds(5);

    // Cap on Claude's exponential backoff during sustained errors.
    private static readonly TimeSpan ClaudeMaxBackoff = TimeSpan.FromHours(1);

    private readonly ClaudeUsageProvider _claude = new();
    private readonly CodexUsageProvider _codex = new();
    private readonly CursorUsageProvider _cursor = new();

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

    private readonly ProviderIcons _claudeIcons;
    private readonly ProviderIcons _codexIcons;
    private readonly ProviderIcons _cursorIcons;

    private readonly WinTimer _claudeTimer;
    private readonly WinTimer _codexTimer;
    private readonly WinTimer _cursorTimer;

    private readonly AppSettings _settings;
    private readonly TrayIcon _anchor;
    private Icon? _anchorIcon;
    private readonly DetailsForm _detailsForm;
    private readonly TaskbarWidget _taskbarWidget;
    private readonly SystemChangeListener _systemChangeListener;

    private UsageSnapshot? _claudeSnap;
    private UsageSnapshot? _codexSnap;
    private UsageSnapshot? _cursorSnap;

    private int _baseIntervalMs;
    private int _claudeRetryCount;
    private int _claudeFastPollsRemaining;
    private string _claudeAuthCredentialSignature = string.Empty;
    private string _codexAuthCredentialSignature = string.Empty;
    private UsageSnapshot? _claudeLastSuccessfulSnap;

    private bool _claudeBusy;
    private bool _claudeAuthPaused;
    private bool _claudeAuthNotified;
    private bool _claudeIdlePaused;
    private bool _codexBusy;
    private bool _codexAuthPaused;
    private bool _codexAuthNotified;
    private bool _cursorBusy;
    private bool _disposed;

    // Menu items kept as fields so they can be re-localized in place when the
    // user switches language without rebuilding (and re-wiring) the menu.
    // Not readonly: BuildMenu assigns them, which the compiler can't see as
    // a constructor-only init even though it's only called from the ctor.
    private ToolStripMenuItem _refreshItem = null!;
    private ToolStripMenuItem _startupItem = null!;
    private ToolStripMenuItem _showClaudeItem = null!;
    private ToolStripMenuItem _showCodexItem = null!;
    private ToolStripMenuItem _showCursorItem = null!;
    private ToolStripMenuItem _iconDisplayParent = null!;
    private ToolStripMenuItem _intervalParent = null!;
    private ToolStripMenuItem _languageItem = null!;
    private ToolStripMenuItem _languageAutoItem = null!;
    private ToolStripMenuItem _notifyItem = null!;
    private ToolStripMenuItem _widgetItem = null!;
    private ToolStripMenuItem _openLogItem = null!;
    private ToolStripMenuItem _checkUpdateItem = null!;
    private ToolStripMenuItem _autoUpdateItem = null!;
    private ToolStripMenuItem _exitItem = null!;

    private readonly WinTimer _updateTimer;
    private bool _updateInProgress;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan UpdateInitialDelay = TimeSpan.FromSeconds(5);

    public TrayApplicationContext()
    {
        _settings = AppSettings.Load();
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

        _anchorIcon = IconRenderer.Render(Color.FromArgb(100, 100, 100), null, isError: true, placeholder: "T");
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

        _claudeIcons.ApplyMode(EffectiveMode(_settings.ShowClaude));
        _codexIcons.ApplyMode(EffectiveMode(_settings.ShowCodex));
        _cursorIcons.ApplyMode(EffectiveMode(_settings.ShowCursor));
        UpdateAnchor();
        _taskbarWidget.Visible = _settings.ShowTaskbarWidget;

        _claudeTimer = new WinTimer { Interval = _baseIntervalMs };
        _claudeTimer.Tick += async (_, _) => await RefreshClaudeAsync();

        _codexTimer = new WinTimer { Interval = _baseIntervalMs };
        _codexTimer.Tick += async (_, _) => await RefreshCodexAsync();

        _cursorTimer = new WinTimer { Interval = _baseIntervalMs };
        _cursorTimer.Tick += async (_, _) => await RefreshCursorAsync();

        WireIconDisplayMenu();
        WireIntervalMenu();
        WireLanguageMenu();
        ApplyTimers();

        if (_settings.ShowClaude)
            _ = RefreshClaudeAsync();
        if (_settings.ShowCodex)
            _ = RefreshCodexAsync();
        if (_settings.ShowCursor)
            _ = RefreshCursorAsync();

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
        if (_settings.AutoUpdate)
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
            if (!_disposed && _settings.AutoUpdate)
                await CheckForUpdatesAsync(manual: false);
        };
        initialDelayTimer.Start();
    }

    private static void OpenLogFile()
    {
        // Touch the file first so the shell-launched editor opens an existing
        // file rather than prompting "create new?". Any failure here just falls
        // through to ShellExecute, which will surface the real reason.
        string path = DiagnosticLog.FilePath;
        try
        {
            if (!File.Exists(path))
                DiagnosticLog.Info("Log file opened from tray menu");
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

    private void OnDisplayMetricsChanged()
    {
        if (_disposed)
            return;
        _taskbarWidget.NotifyDisplayChanged();
    }

    private void UpdateAnchor()
    {
        bool anyIcon = _settings.IconMode != IconDisplayMode.None
            && (_settings.ShowClaude || _settings.ShowCodex || _settings.ShowCursor);
        _anchor.Visible = !anyIcon;
    }

    private IconDisplayMode EffectiveMode(bool providerEnabled) =>
        providerEnabled ? _settings.IconMode : IconDisplayMode.None;

    private void ApplyTimers()
    {
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
                _claudeTimer.Interval = _baseIntervalMs;
                _codexTimer.Interval = _baseIntervalMs;
                _cursorTimer.Interval = _baseIntervalMs;
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

        var culture = string.IsNullOrEmpty(code)
            ? Program.SystemUiCulture
            : CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        RelocalizeMenu();
        // Tray tooltip text is rebuilt from Strings.* on next refresh tick;
        // trigger one now so the change is immediately visible.
        _ = RefreshAllAsync();
    }

    private void RelocalizeMenu()
    {
        _refreshItem.Text = Strings.Menu_RefreshNow;
        _startupItem.Text = Strings.Menu_RunAtStartup;
        _showClaudeItem.Text = Strings.Menu_ShowClaude;
        _showCodexItem.Text = Strings.Menu_ShowCodex;
        _showCursorItem.Text = Strings.Menu_ShowCursor;
        _iconDisplayParent.Text = Strings.Menu_IconDisplay;
        _intervalParent.Text = Strings.Menu_RefreshInterval;
        _languageItem.Text = Strings.Menu_Language;
        _languageAutoItem.Text = Strings.Menu_LanguageAuto;
        _notifyItem.Text = Strings.Menu_EnableNotifications;
        _widgetItem.Text = Strings.Menu_ShowTaskbarWidget;
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

        _refreshItem = new ToolStripMenuItem(Strings.Menu_RefreshNow);
        _refreshItem.Click += async (_, _) => await RefreshAllAsync(forceClaude: true, forceCodex: true);

        _startupItem = new ToolStripMenuItem(Strings.Menu_RunAtStartup);
        _showClaudeItem = new ToolStripMenuItem(Strings.Menu_ShowClaude);
        _showCodexItem = new ToolStripMenuItem(Strings.Menu_ShowCodex);
        _showCursorItem = new ToolStripMenuItem(Strings.Menu_ShowCursor);

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

        _openLogItem = new ToolStripMenuItem(Strings.Menu_OpenLog);
        _openLogItem.Click += (_, _) => OpenLogFile();

        _checkUpdateItem = new ToolStripMenuItem(Strings.Menu_CheckForUpdates);
        _checkUpdateItem.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);

        _exitItem = new ToolStripMenuItem(Strings.Menu_Exit);
        _exitItem.Click += (_, _) => ExitThread();

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
        menu.Items.Add(_iconDisplayParent);
        menu.Items.Add(_intervalParent);
        menu.Items.Add(_languageItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_checkUpdateItem);
        menu.Items.Add(_openLogItem);
        menu.Items.Add(_exitItem);

        return menu;
    }

    private async Task RefreshAllAsync(bool forceClaude = false, bool forceCodex = false)
    {
        var tasks = new List<Task>();
        if (_settings.ShowClaude)
            tasks.Add(RefreshClaudeAsync(forceClaude));
        if (_settings.ShowCodex)
            tasks.Add(RefreshCodexAsync(forceCodex));
        if (_settings.ShowCursor)
            tasks.Add(RefreshCursorAsync());
        await Task.WhenAll(tasks);
    }

    private async Task RefreshClaudeAsync(bool force = false)
    {
        if (_claudeBusy || _disposed)
            return;
        _claudeBusy = true;
        try
        {
            if (!force && UserActivity.IsAway(ClaudeIdlePause))
            {
                if (!_claudeIdlePaused)
                {
                    _claudeIdlePaused = true;
                    DiagnosticLog.Info("Claude polling paused while user is idle or workstation is locked");
                }

                _claudeTimer.Interval = ToTimerInterval(ClaudeIdleCheckInterval);
                return;
            }

            if (_claudeIdlePaused)
            {
                _claudeIdlePaused = false;
                DiagnosticLog.Info("Claude polling resumed after user activity");
            }

            if (_claudeAuthPaused && !force)
            {
                string signature = await Task.Run(ClaudeUsageProvider.CredentialWatchSignature);
                if (signature == _claudeAuthCredentialSignature)
                {
                    _claudeTimer.Interval = _baseIntervalMs;
                    return;
                }

                DiagnosticLog.Info("Claude credential signature changed; resuming auth polling");
                _claudeAuthPaused = false;
            }

            UsageSnapshot snap = await _claude.GetUsageAsync(CancellationToken.None);
            if (_disposed)
                return;
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
                DiagnosticLog.Warn("Claude auth polling paused until credentials change");
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
            long cap = (long)ClaudeMaxBackoff.TotalMilliseconds;
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
            DiagnosticLog.Warn("Claude auth notification was not shown because no tray icon was available");
    }

    private void ResetClaudeAuthState()
    {
        _claudeAuthPaused = false;
        _claudeAuthNotified = false;
        _claudeAuthCredentialSignature = string.Empty;
    }

    private async Task RefreshCodexAsync(bool force = false)
    {
        if (_codexBusy || _disposed)
            return;
        _codexBusy = true;
        try
        {
            if (_codexAuthPaused && !force)
            {
                string signature = await Task.Run(CodexUsageProvider.CredentialWatchSignature);
                if (signature == _codexAuthCredentialSignature)
                    return;

                DiagnosticLog.Info("Codex credential signature changed; resuming auth polling");
                _codexAuthPaused = false;
            }

            UsageSnapshot snap = await _codex.GetUsageAsync(CancellationToken.None);
            if (_disposed)
                return;
            _codexSnap = snap;
            _codexIcons.Update(snap);
            RefreshDetailsIfVisible();

            if (snap.ErrorKind == UsageErrorKind.Auth)
            {
                _codexAuthPaused = true;
                _codexAuthCredentialSignature = await Task.Run(CodexUsageProvider.CredentialWatchSignature);
                DiagnosticLog.Warn("Codex auth polling paused until credentials change");
                NotifyCodexAuthErrorOnce();
            }
            else
            {
                ResetCodexAuthState();
            }
        }
        finally
        {
            _codexBusy = false;
        }
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
            DiagnosticLog.Warn("Codex auth notification was not shown because no tray icon was available");
    }

    private void ResetCodexAuthState()
    {
        _codexAuthPaused = false;
        _codexAuthNotified = false;
        _codexAuthCredentialSignature = string.Empty;
    }

    private async Task RefreshCursorAsync()
    {
        if (_cursorBusy || _disposed)
            return;
        _cursorBusy = true;
        try
        {
            UsageSnapshot snap = await _cursor.GetUsageAsync(CancellationToken.None);
            if (_disposed)
                return;
            _cursorSnap = snap;
            _cursorIcons.Update(snap);
            RefreshDetailsIfVisible();
        }
        finally
        {
            _cursorBusy = false;
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateInProgress || _disposed)
            return;
        _updateInProgress = true;
        try
        {
            DiagnosticLog.Info($"AutoUpdate check started (manual={manual})");
            UpdateCheckOutcome outcome = await AutoUpdate.HasUpdateAsync(_settings.DisableMirrorDownload);
            if (_disposed)
                return;

            _settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            try { _settings.Save(); } catch { }

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
            DiagnosticLog.Error($"AutoUpdate check threw: {ex.Message}");
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
        DiagnosticLog.Warn("AutoUpdate toast had no visible tray icon to surface through");
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
        // as each provider completes.
        _ = RefreshAllAsync();
    }

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
        var list = new List<TaskbarWidget.Column>(3);
        if (_settings.ShowClaude)
            list.Add(BuildColumn(_claudeIcons, _claudeSnap));
        if (_settings.ShowCodex)
            list.Add(BuildColumn(_codexIcons, _codexSnap));
        if (_settings.ShowCursor)
            list.Add(BuildColumn(_cursorIcons, _cursorSnap));
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
        var list = new List<DetailsForm.Entry>(6);
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
            _claudeTimer.Dispose();
            _codexTimer.Dispose();
            _cursorTimer.Dispose();
            _updateTimer.Dispose();
            _claudeIcons.Dispose();
            _codexIcons.Dispose();
            _cursorIcons.Dispose();
            _anchor.Visible = false;
            _anchor.Dispose();
            _anchorIcon?.Dispose();
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

            // Hold the initial placeholder icons in our own fields so the HICON
            // stays valid for as long as the shell references it.
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
                error || metric is null
            );
            // Push the new handle to the shell first, then release the old one —
            // otherwise the shell would briefly hold a freed HICON.
            target.Icon = rendered;
            current?.Dispose();
            current = rendered;

            target.Text = Truncate(tooltip);

            if (!error && metric is not null)
                CheckThresholds(target, spec, metric, thresholdState);
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
