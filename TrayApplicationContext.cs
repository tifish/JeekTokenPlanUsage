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
    // window. Claude-only — Codex / Cursor don't need this.
    private static readonly TimeSpan ClaudeFastPollAfterReset = TimeSpan.FromSeconds(60);

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

    private int _baseIntervalMs;
    private int _claudeRetryCount;

    private bool _claudeBusy;
    private bool _codexBusy;
    private bool _cursorBusy;
    private bool _disposed;

    public TrayApplicationContext()
    {
        _settings = AppSettings.Load();
        _baseIntervalMs = ClampPollMinutes(_settings.PollMinutes) * 60_000;

        ContextMenuStrip menu = BuildMenu(
            out ToolStripMenuItem startupItem,
            out ToolStripMenuItem showClaudeItem,
            out ToolStripMenuItem showCodexItem,
            out ToolStripMenuItem showCursorItem,
            out ToolStripMenuItem intervalParent
        );

        // Frame colors echo the brand icons: Claude = orange, Codex = blue, Cursor = monochrome dark;
        // the shorter window uses the brighter shade, the longer one the deeper shade.
        _claudeIcons = new ProviderIcons(
            "Claude",
            new WindowSpec(Color.FromArgb(255, 146, 48), "5h", LongDate: false),
            new WindowSpec(Color.FromArgb(198, 93, 20), "周", LongDate: true),
            ClaudePrimaryGuid,
            ClaudeSecondaryGuid,
            menu
        );
        _codexIcons = new ProviderIcons(
            "Codex",
            new WindowSpec(Color.FromArgb(64, 140, 255), "5h", LongDate: false),
            new WindowSpec(Color.FromArgb(30, 85, 200), "周", LongDate: true),
            CodexPrimaryGuid,
            CodexSecondaryGuid,
            menu
        );
        // Cursor's brand mark is monochrome with a slight cool cast; we use a light
        // and a deep cool slate. Avoid going near pure black — it disappears on
        // dark taskbars. Both pools reset on the monthly billing cycle, so both
        // use the long (MM-dd) date format.
        _cursorIcons = new ProviderIcons(
            "Cursor",
            new WindowSpec(Color.FromArgb(170, 175, 190), "Auto", LongDate: true),
            new WindowSpec(Color.FromArgb(85, 95, 120), "API", LongDate: true),
            CursorPrimaryGuid,
            CursorSecondaryGuid,
            menu
        );

        _anchorIcon = IconRenderer.Render(Color.FromArgb(100, 100, 100), null, isError: true);
        _anchor = new TrayIcon(AnchorGuid, menu)
        {
            Icon = _anchorIcon,
            Text = "JeekTokenPlanUsage",
        };

        _claudeIcons.SetVisible(_settings.ShowClaude);
        _codexIcons.SetVisible(_settings.ShowCodex);
        _cursorIcons.SetVisible(_settings.ShowCursor);
        UpdateAnchor();

        _claudeTimer = new WinTimer { Interval = _baseIntervalMs };
        _claudeTimer.Tick += async (_, _) => await RefreshClaudeAsync();

        _codexTimer = new WinTimer { Interval = _baseIntervalMs };
        _codexTimer.Tick += async (_, _) => await RefreshCodexAsync();

        _cursorTimer = new WinTimer { Interval = _baseIntervalMs };
        _cursorTimer.Tick += async (_, _) => await RefreshCursorAsync();

        WireIntervalMenu(intervalParent);
        ApplyTimers();

        if (_settings.ShowClaude)
            _ = RefreshClaudeAsync();
        if (_settings.ShowCodex)
            _ = RefreshCodexAsync();
        if (_settings.ShowCursor)
            _ = RefreshCursorAsync();

        startupItem.Checked = _settings.RunAtStartup;
        startupItem.Click += (_, _) =>
        {
            bool next = !startupItem.Checked;
            _settings.RunAtStartup = next;
            startupItem.Checked = next;
        };

        showClaudeItem.Checked = _settings.ShowClaude;
        showClaudeItem.Click += async (_, _) =>
        {
            bool next = !showClaudeItem.Checked;
            _settings.ShowClaude = next;
            _settings.Save();
            showClaudeItem.Checked = next;
            _claudeIcons.SetVisible(next);
            ApplyTimers();
            UpdateAnchor();
            if (next)
                await RefreshClaudeAsync();
        };

        showCodexItem.Checked = _settings.ShowCodex;
        showCodexItem.Click += async (_, _) =>
        {
            bool next = !showCodexItem.Checked;
            _settings.ShowCodex = next;
            _settings.Save();
            showCodexItem.Checked = next;
            _codexIcons.SetVisible(next);
            ApplyTimers();
            UpdateAnchor();
            if (next)
                await RefreshCodexAsync();
        };

        showCursorItem.Checked = _settings.ShowCursor;
        showCursorItem.Click += async (_, _) =>
        {
            bool next = !showCursorItem.Checked;
            _settings.ShowCursor = next;
            _settings.Save();
            showCursorItem.Checked = next;
            _cursorIcons.SetVisible(next);
            ApplyTimers();
            UpdateAnchor();
            if (next)
                await RefreshCursorAsync();
        };
    }

    private void UpdateAnchor()
    {
        _anchor.Visible = !_settings.ShowClaude && !_settings.ShowCodex && !_settings.ShowCursor;
    }

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

    private void WireIntervalMenu(ToolStripMenuItem intervalParent)
    {
        foreach (ToolStripItem raw in intervalParent.DropDownItems)
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
                _claudeTimer.Interval = _baseIntervalMs;
                _codexTimer.Interval = _baseIntervalMs;
                _cursorTimer.Interval = _baseIntervalMs;
                foreach (ToolStripItem sibling in intervalParent.DropDownItems)
                    if (sibling is ToolStripMenuItem mi)
                        mi.Checked = ReferenceEquals(mi, item);
            };
        }
    }

    private ContextMenuStrip BuildMenu(
        out ToolStripMenuItem startupItem,
        out ToolStripMenuItem showClaudeItem,
        out ToolStripMenuItem showCodexItem,
        out ToolStripMenuItem showCursorItem,
        out ToolStripMenuItem intervalParent
    )
    {
        var menu = new ContextMenuStrip();

        var refreshItem = new ToolStripMenuItem("立即刷新");
        refreshItem.Click += async (_, _) => await RefreshAllAsync();

        startupItem = new ToolStripMenuItem("开机启动");
        showClaudeItem = new ToolStripMenuItem("显示 Claude 用量");
        showCodexItem = new ToolStripMenuItem("显示 Codex 用量");
        showCursorItem = new ToolStripMenuItem("显示 Cursor 用量");

        intervalParent = new ToolStripMenuItem("刷新间隔");
        foreach (int min in AllowedPollMinutes)
            intervalParent.DropDownItems.Add(
                new ToolStripMenuItem(FormatMinutes(min)) { Tag = min }
            );

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitThread();

        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(showClaudeItem);
        menu.Items.Add(showCodexItem);
        menu.Items.Add(showCursorItem);
        menu.Items.Add(intervalParent);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    private async Task RefreshAllAsync()
    {
        var tasks = new List<Task>();
        if (_settings.ShowClaude)
            tasks.Add(RefreshClaudeAsync());
        if (_settings.ShowCodex)
            tasks.Add(RefreshCodexAsync());
        if (_settings.ShowCursor)
            tasks.Add(RefreshCursorAsync());
        await Task.WhenAll(tasks);
    }

    private async Task RefreshClaudeAsync()
    {
        if (_claudeBusy || _disposed)
            return;
        _claudeBusy = true;
        try
        {
            UsageSnapshot snap = await _claude.GetUsageAsync(CancellationToken.None);
            if (_disposed)
                return;
            _claudeIcons.Update(snap);
            // Setting Interval restarts the countdown — exactly what we want for
            // adapting cadence (backoff on error, fast-poll right after reset).
            _claudeTimer.Interval = NextClaudeIntervalMs(snap);
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
            // 2^(retry-1), capped to avoid shifting past int width.
            int shift = Math.Min(_claudeRetryCount - 1, 16);
            long ms = (long)_baseIntervalMs * (1L << shift);
            long cap = (long)ClaudeMaxBackoff.TotalMilliseconds;
            return (int)Math.Min(ms, cap);
        }

        _claudeRetryCount = 0;

        // Either window's reset time being in the past means the API hasn't
        // rolled over yet — poll faster so the new window appears promptly.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool pastReset =
            (snap.FiveHour?.ResetsAt is { } a && a <= now)
            || (snap.Weekly?.ResetsAt is { } b && b <= now);

        return pastReset ? (int)ClaudeFastPollAfterReset.TotalMilliseconds : _baseIntervalMs;
    }

    private async Task RefreshCodexAsync()
    {
        if (_codexBusy || _disposed)
            return;
        _codexBusy = true;
        try
        {
            UsageSnapshot snap = await _codex.GetUsageAsync(CancellationToken.None);
            if (!_disposed)
                _codexIcons.Update(snap);
        }
        finally
        {
            _codexBusy = false;
        }
    }

    private async Task RefreshCursorAsync()
    {
        if (_cursorBusy || _disposed)
            return;
        _cursorBusy = true;
        try
        {
            UsageSnapshot snap = await _cursor.GetUsageAsync(CancellationToken.None);
            if (!_disposed)
                _cursorIcons.Update(snap);
        }
        finally
        {
            _cursorBusy = false;
        }
    }

    private static int ClampPollMinutes(int minutes) =>
        Array.IndexOf(AllowedPollMinutes, minutes) >= 0 ? minutes : 5;

    private static string FormatMinutes(int minutes) =>
        minutes < 60 ? $"{minutes} 分钟" : $"{minutes / 60} 小时";

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _claudeTimer.Dispose();
            _codexTimer.Dispose();
            _cursorTimer.Dispose();
            _claudeIcons.Dispose();
            _codexIcons.Dispose();
            _cursorIcons.Dispose();
            _anchor.Visible = false;
            _anchor.Dispose();
            _anchorIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// Per-window display metadata: background color, short label shown in tooltip,
    /// and whether the reset time needs a date prefix (long windows like weekly or
    /// monthly billing cycles) or can omit it (short windows like 5-hour).
    private sealed record WindowSpec(Color Bg, string Label, bool LongDate);

    /// Holds two tray icons (primary + secondary window) for one provider.
    private sealed class ProviderIcons : IDisposable
    {
        private readonly string _displayName;
        private readonly WindowSpec _primarySpec;
        private readonly WindowSpec _secondarySpec;
        private readonly TrayIcon _primary;
        private readonly TrayIcon _secondary;
        private Icon? _primaryIcon;
        private Icon? _secondaryIcon;

        public ProviderIcons(
            string displayName,
            WindowSpec primary,
            WindowSpec secondary,
            Guid primaryGuid,
            Guid secondaryGuid,
            ContextMenuStrip menu
        )
        {
            _displayName = displayName;
            _primarySpec = primary;
            _secondarySpec = secondary;

            // Hold the initial placeholder icons in our own fields so the HICON
            // stays valid for as long as the shell references it.
            _primaryIcon = IconRenderer.Render(primary.Bg, null, isError: true);
            _secondaryIcon = IconRenderer.Render(secondary.Bg, null, isError: true);

            _primary = new TrayIcon(primaryGuid, menu)
            {
                Icon = _primaryIcon,
                Text = "加载中…",
            };
            _secondary = new TrayIcon(secondaryGuid, menu)
            {
                Icon = _secondaryIcon,
                Text = "加载中…",
            };
        }

        public void SetVisible(bool visible)
        {
            _primary.Visible = visible;
            _secondary.Visible = visible;
        }

        public void Update(UsageSnapshot snap)
        {
            bool error = snap.Error is not null;
            ApplyTo(_primary, ref _primaryIcon, _primarySpec, snap.FiveHour, error, snap.Error);
            ApplyTo(_secondary, ref _secondaryIcon, _secondarySpec, snap.Weekly, error, snap.Error);
        }

        private void ApplyTo(
            TrayIcon target,
            ref Icon? current,
            WindowSpec spec,
            UsageMetric? metric,
            bool error,
            string? errorText
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

            target.Text =
                error ? Truncate($"{_displayName} {spec.Label}: {errorText}")
                : metric is null ? $"{_displayName} {spec.Label}: 无数据"
                : Truncate(
                    $"{_displayName} {spec.Label}: {metric.Utilization:0.#}% · {FormatReset(metric.ResetsAt, spec.LongDate)}"
                );
        }

        private static string FormatReset(DateTimeOffset? reset, bool longDate)
        {
            if (reset is null)
                return "?";
            DateTimeOffset local = reset.Value.ToLocalTime();
            string absolute = longDate ? local.ToString("MM-dd HH:mm") : local.ToString("HH:mm");
            return $"剩 {FormatRemaining(reset.Value - DateTimeOffset.Now)} · 重置 {absolute}";
        }

        private static string FormatRemaining(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero)
                return "0m";
            int days = remaining.Days;
            int hours = remaining.Hours;
            int minutes = remaining.Minutes;
            if (days > 0)
                return $"{days}d {hours}h";
            if (hours > 0)
                return $"{hours}h {minutes}m";
            return $"{Math.Max(1, minutes)}m";
        }

        private static string Truncate(string s) => s.Length <= 63 ? s : s[..63];

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
