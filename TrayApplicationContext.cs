using WinTimer = System.Windows.Forms.Timer;

namespace JeekTokenPlanUsage;

public sealed class TrayApplicationContext : ApplicationContext
{
    // Allowed base intervals for Claude (minutes). Each poll may fall back to
    // a real messages-API call that consumes quota, so the floor is 5 minutes.
    private static readonly int[] AllowedClaudeMinutes = { 5, 10, 30, 60 };

    // After a successful poll whose returned reset time is already in the past,
    // poll again at this cadence until the API rolls over to a new window.
    private static readonly TimeSpan ClaudeFastPollAfterReset = TimeSpan.FromSeconds(60);

    // Cap on exponential backoff during sustained errors.
    private static readonly TimeSpan ClaudeMaxBackoff = TimeSpan.FromHours(1);

    // Codex is cheap (no hammer-prone endpoint), so keep a fixed interval.
    private static readonly TimeSpan CodexInterval = TimeSpan.FromSeconds(60);

    private readonly ClaudeUsageProvider _claude = new();
    private readonly CodexUsageProvider _codex = new();

    private readonly ProviderIcons _claudeIcons;
    private readonly ProviderIcons _codexIcons;

    private readonly WinTimer _claudeTimer;
    private readonly WinTimer _codexTimer;

    private readonly AppSettings _settings;
    private readonly NotifyIcon _anchor;
    private Icon? _anchorIcon;

    private int _claudeBaseIntervalMs;
    private int _claudeRetryCount;

    private bool _claudeBusy;
    private bool _codexBusy;
    private bool _disposed;

    public TrayApplicationContext()
    {
        _settings = AppSettings.Load();
        _claudeBaseIntervalMs = ClampClaudeMinutes(_settings.ClaudePollMinutes) * 60_000;

        ContextMenuStrip menu = BuildMenu(
            out ToolStripMenuItem startupItem,
            out ToolStripMenuItem showClaudeItem,
            out ToolStripMenuItem showCodexItem,
            out ToolStripMenuItem intervalParent);

        // Frame colors echo the brand icons: Claude = orange, Codex = blue;
        // the 5-hour window uses the brighter shade, the weekly window the deeper shade.
        _claudeIcons = new ProviderIcons("Claude",
            fiveHourBg: Color.FromArgb(255, 146, 48), weeklyBg: Color.FromArgb(198, 93, 20), menu);
        _codexIcons = new ProviderIcons("Codex",
            fiveHourBg: Color.FromArgb(64, 140, 255), weeklyBg: Color.FromArgb(30, 85, 200), menu);

        _anchorIcon = IconRenderer.Render(Color.FromArgb(100, 100, 100), null, isError: true);
        _anchor = new NotifyIcon
        {
            Icon = _anchorIcon,
            Text = "JeekTokenPlanUsage",
            ContextMenuStrip = menu,
        };

        _claudeIcons.SetVisible(_settings.ShowClaude);
        _codexIcons.SetVisible(_settings.ShowCodex);
        UpdateAnchor();

        _claudeTimer = new WinTimer { Interval = _claudeBaseIntervalMs };
        _claudeTimer.Tick += async (_, _) => await RefreshClaudeAsync();

        _codexTimer = new WinTimer { Interval = (int)CodexInterval.TotalMilliseconds };
        _codexTimer.Tick += async (_, _) => await RefreshCodexAsync();

        WireIntervalMenu(intervalParent);
        ApplyTimers();

        if (_settings.ShowClaude) _ = RefreshClaudeAsync();
        if (_settings.ShowCodex) _ = RefreshCodexAsync();

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
            if (next) await RefreshClaudeAsync();
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
            if (next) await RefreshCodexAsync();
        };
    }

    private void UpdateAnchor()
    {
        _anchor.Visible = !_settings.ShowClaude && !_settings.ShowCodex;
    }

    private void ApplyTimers()
    {
        if (_settings.ShowClaude) _claudeTimer.Start();
        else _claudeTimer.Stop();

        if (_settings.ShowCodex) _codexTimer.Start();
        else _codexTimer.Stop();
    }

    private void WireIntervalMenu(ToolStripMenuItem intervalParent)
    {
        foreach (ToolStripItem raw in intervalParent.DropDownItems)
        {
            if (raw is not ToolStripMenuItem item || item.Tag is not int minutes) continue;
            item.Checked = (minutes == _settings.ClaudePollMinutes);
            item.Click += (_, _) =>
            {
                _settings.ClaudePollMinutes = minutes;
                _settings.Save();
                _claudeBaseIntervalMs = minutes * 60_000;
                _claudeRetryCount = 0;
                _claudeTimer.Interval = _claudeBaseIntervalMs;
                foreach (ToolStripItem sibling in intervalParent.DropDownItems)
                    if (sibling is ToolStripMenuItem mi) mi.Checked = ReferenceEquals(mi, item);
            };
        }
    }

    private ContextMenuStrip BuildMenu(
        out ToolStripMenuItem startupItem,
        out ToolStripMenuItem showClaudeItem,
        out ToolStripMenuItem showCodexItem,
        out ToolStripMenuItem intervalParent)
    {
        var menu = new ContextMenuStrip();

        var refreshItem = new ToolStripMenuItem("立即刷新");
        refreshItem.Click += async (_, _) => await RefreshAllAsync();

        startupItem = new ToolStripMenuItem("开机启动");
        showClaudeItem = new ToolStripMenuItem("显示 Claude 用量");
        showCodexItem = new ToolStripMenuItem("显示 Codex 用量");

        intervalParent = new ToolStripMenuItem("Claude 刷新间隔");
        foreach (int min in AllowedClaudeMinutes)
            intervalParent.DropDownItems.Add(new ToolStripMenuItem(FormatMinutes(min)) { Tag = min });

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitThread();

        menu.Items.Add(refreshItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(showClaudeItem);
        menu.Items.Add(showCodexItem);
        menu.Items.Add(intervalParent);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        return menu;
    }

    private async Task RefreshAllAsync()
    {
        var tasks = new List<Task>();
        if (_settings.ShowClaude) tasks.Add(RefreshClaudeAsync());
        if (_settings.ShowCodex) tasks.Add(RefreshCodexAsync());
        await Task.WhenAll(tasks);
    }

    private async Task RefreshClaudeAsync()
    {
        if (_claudeBusy || _disposed) return;
        _claudeBusy = true;
        try
        {
            UsageSnapshot snap = await _claude.GetUsageAsync(CancellationToken.None);
            if (_disposed) return;
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
            long ms = (long)_claudeBaseIntervalMs * (1L << shift);
            long cap = (long)ClaudeMaxBackoff.TotalMilliseconds;
            return (int)Math.Min(ms, cap);
        }

        _claudeRetryCount = 0;

        // Either window's reset time being in the past means the API hasn't
        // rolled over yet — poll faster so the new window appears promptly.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool pastReset =
            (snap.FiveHour?.ResetsAt is { } a && a <= now) ||
            (snap.Weekly?.ResetsAt is { } b && b <= now);

        return pastReset
            ? (int)ClaudeFastPollAfterReset.TotalMilliseconds
            : _claudeBaseIntervalMs;
    }

    private async Task RefreshCodexAsync()
    {
        if (_codexBusy || _disposed) return;
        _codexBusy = true;
        try
        {
            UsageSnapshot snap = await _codex.GetUsageAsync(CancellationToken.None);
            if (!_disposed) _codexIcons.Update(snap);
        }
        finally
        {
            _codexBusy = false;
        }
    }

    private static int ClampClaudeMinutes(int minutes)
        => Array.IndexOf(AllowedClaudeMinutes, minutes) >= 0 ? minutes : AllowedClaudeMinutes[0];

    private static string FormatMinutes(int minutes)
        => minutes < 60 ? $"{minutes} 分钟" : $"{minutes / 60} 小时";

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _claudeTimer.Dispose();
            _codexTimer.Dispose();
            _claudeIcons.Dispose();
            _codexIcons.Dispose();
            _anchor.Visible = false;
            _anchor.Dispose();
            _anchorIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// Holds the two tray icons (5-hour + weekly) for one provider.
    private sealed class ProviderIcons : IDisposable
    {
        private readonly string _displayName;
        private readonly Color _fiveHourBg;
        private readonly Color _weeklyBg;
        private readonly NotifyIcon _fiveHour;
        private readonly NotifyIcon _weekly;
        private Icon? _fiveHourIcon;
        private Icon? _weeklyIcon;

        public ProviderIcons(string displayName, Color fiveHourBg, Color weeklyBg, ContextMenuStrip menu)
        {
            _displayName = displayName;
            _fiveHourBg = fiveHourBg;
            _weeklyBg = weeklyBg;
            _fiveHour = MakeIcon(fiveHourBg, menu);
            _weekly = MakeIcon(weeklyBg, menu);
        }

        private static NotifyIcon MakeIcon(Color bg, ContextMenuStrip menu) => new()
        {
            Icon = IconRenderer.Render(bg, null, isError: true),
            Text = "加载中…",
            ContextMenuStrip = menu,
        };

        public void SetVisible(bool visible)
        {
            _fiveHour.Visible = visible;
            _weekly.Visible = visible;
        }

        public void Update(UsageSnapshot snap)
        {
            bool error = snap.Error is not null;
            ApplyTo(_fiveHour, ref _fiveHourIcon, _fiveHourBg, "5h", isWeekly: false, snap.FiveHour, error, snap.Error);
            ApplyTo(_weekly, ref _weeklyIcon, _weeklyBg, "周", isWeekly: true, snap.Weekly, error, snap.Error);
        }

        private void ApplyTo(NotifyIcon target, ref Icon? current, Color bg, string windowName,
            bool isWeekly, UsageMetric? metric, bool error, string? errorText)
        {
            Icon rendered = IconRenderer.Render(bg, metric?.Utilization, error || metric is null);
            target.Icon = rendered;
            current?.Dispose();
            current = rendered;

            target.Text = error
                ? Truncate($"{_displayName} {windowName}: {errorText}")
                : metric is null
                    ? $"{_displayName} {windowName}: 无数据"
                    : Truncate($"{_displayName} {windowName}: {metric.Utilization:0.#}% · 重置 {FormatReset(metric.ResetsAt, isWeekly)}");
        }

        private static string FormatReset(DateTimeOffset? reset, bool isWeekly)
        {
            if (reset is null) return "?";
            DateTimeOffset local = reset.Value.ToLocalTime();
            string absolute = isWeekly ? local.ToString("MM-dd HH:mm") : local.ToString("HH:mm");
            return $"{absolute} (剩 {FormatRemaining(reset.Value - DateTimeOffset.Now)})";
        }

        private static string FormatRemaining(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero) return "0m";
            int days = remaining.Days;
            int hours = remaining.Hours;
            int minutes = remaining.Minutes;
            if (days > 0) return $"{days}d {hours}h";
            if (hours > 0) return $"{hours}h {minutes}m";
            return $"{Math.Max(1, minutes)}m";
        }

        private static string Truncate(string s) => s.Length <= 63 ? s : s[..63];

        public void Dispose()
        {
            _fiveHour.Visible = false;
            _weekly.Visible = false;
            _fiveHour.Dispose();
            _weekly.Dispose();
            _fiveHourIcon?.Dispose();
            _weeklyIcon?.Dispose();
        }
    }
}
