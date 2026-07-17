using System.Runtime.InteropServices;
using JeekTokenPlanUsage.Resources;

namespace JeekTokenPlanUsage;

/// Compact borderless popup that shows each enabled provider's windows in one
/// glance — utilization %, reset time, and any recent error. Left-clicking any
/// tray icon toggles it; it auto-hides on focus loss or Escape.
internal sealed class DetailsForm : Form
{
    public sealed record Entry(
        string ProviderName,
        string WindowLabel,
        Color WindowColor,
        bool LongDate,
        UsageMetric? Metric,
        string? Error);

    private const int IndicatorSize = 10;
    private const int IndicatorOpticalOffset = 4;
    private const int RowVPad = 4;

    // Mirrors IconRenderer's warn threshold so the popup and icon flag the same
    // window as "hot" (in the tray icon's amber).
    private const double WarnPercent = 80;

    // Background / text / muted / border come from SystemColors, which TrayApplicationContext
    // keeps live by re-applying Application.SetColorMode on each OS theme switch.
    // Usage-warning and error have no system equivalent, so they're explicit and
    // follow the (live) Application.IsDarkModeEnabled — warning matches the tray icon's amber.
    private static Color WarnText => Application.IsDarkModeEnabled
        ? Color.FromArgb(255, 214, 10)
        : Color.FromArgb(160, 120, 0);
    private static Color ErrorText => Application.IsDarkModeEnabled
        ? Color.FromArgb(235, 90, 90)
        : Color.FromArgb(200, 30, 30);
    private static Color MutedText => SystemColors.GrayText;
    private static Color BorderColor => SystemColors.ControlDark;

    private readonly TableLayoutPanel _table;
    private readonly List<Row> _rows = new();
    private Font? _boldFont;
    private DateTime _lastHideUtc = DateTime.MinValue;
    private IReadOnlyList<Entry> _lastEntries = Array.Empty<Entry>();

    private sealed class Row
    {
        public Panel Indicator = null!;
        public Label Provider = null!;
        public Label Window = null!;
        public Label Value = null!;
        public Label Remaining = null!;
        public Label ResetTime = null!;
        public string ProviderName = string.Empty;
        public string WindowLabel = string.Empty;
    }

    // Held as a field so the GC can't collect the delegate while the OS still
    // has it registered as the hook callback.
    private LowLevelMouseProc? _hookProc;
    private IntPtr _hookId = IntPtr.Zero;

    public DetailsForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.ControlText;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        KeyPreview = true;
        Padding = new Padding(10, 8, 10, 8);
        DoubleBuffered = true;

        _table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 6,
            Dock = DockStyle.Fill,
        };
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Controls.Add(_table);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
                Hide();
        };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            _lastHideUtc = DateTime.UtcNow;
            UninstallMouseHook();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UninstallMouseHook();
            _boldFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// True if the form was hidden within the last `withinMs` milliseconds.
    /// Used to suppress the immediate "click the icon while popup is open"
    /// reopen — the mouse hook hides the form on the click-down, then the
    /// shell's NIN_SELECT lands on button-up. Without this guard the icon
    /// would feel un-toggleable.
    public bool WasRecentlyHidden(int withinMs = 250) =>
        (DateTime.UtcNow - _lastHideUtc).TotalMilliseconds < withinMs;

    /// Re-applies theme-dependent colors after a system light/dark switch. Updates
    /// even when hidden so the next open is already correct.
    public void NotifyThemeChanged()
    {
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.ControlText;
        UpdateRows(_lastEntries);
        Invalidate(true);
    }

    public void UpdateRows(IReadOnlyList<Entry> entries)
    {
        _lastEntries = entries;

        // Only the value labels actually change between refreshes; the row
        // structure (which providers, in what order) is set when the user
        // opens the popup. Rebuilding 24 controls every poll caused the
        // TableLayoutPanel to mis-lay-out and collapse the form.
        if (StructureMatches(entries))
        {
            for (int i = 0; i < entries.Count; i++)
                ApplyValues(_rows[i], entries[i]);
            return;
        }

        Rebuild(entries);
    }

    private bool StructureMatches(IReadOnlyList<Entry> entries)
    {
        if (entries.Count != _rows.Count)
            return false;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].ProviderName != _rows[i].ProviderName
                || entries[i].WindowLabel != _rows[i].WindowLabel)
                return false;
        return true;
    }

    private void Rebuild(IReadOnlyList<Entry> entries)
    {
        SuspendLayout();
        _table.SuspendLayout();

        // Snapshot first, then clear, then dispose — Dispose() removes the
        // control from its parent, which would mutate the collection mid-
        // iteration if we disposed inside a foreach over _table.Controls.
        Control[] old = _table.Controls.Cast<Control>().ToArray();
        _table.Controls.Clear();
        _table.RowStyles.Clear();
        foreach (Control c in old)
            c.Dispose();
        _rows.Clear();

        _table.RowCount = Math.Max(entries.Count, 1);
        for (int i = 0; i < entries.Count; i++)
        {
            _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Row row = CreateRow(entries[i]);
            _table.Controls.Add(row.Indicator, 0, i);
            _table.Controls.Add(row.Provider, 1, i);
            _table.Controls.Add(row.Window, 2, i);
            _table.Controls.Add(row.Value, 3, i);
            _table.Controls.Add(row.Remaining, 4, i);
            _table.Controls.Add(row.ResetTime, 5, i);
            _rows.Add(row);
        }

        _table.ResumeLayout(performLayout: true);
        ResumeLayout(performLayout: true);
    }

    public void ShowAt(Point cursor, IReadOnlyList<Entry> entries)
    {
        Rectangle screen = Screen.FromPoint(cursor).WorkingArea;

        // Create/move the native window on the target monitor before measuring
        // the rows.  The percentage labels use an explicit bold font, so it must
        // be created for this monitor's DPI rather than the startup monitor's.
        Location = cursor;
        _ = Handle;
        UpdateRows(entries);

        // PreferredSize is computed before the window is shown, accounting for
        // the just-applied row content.
        Size size = PreferredSize;
        int x = cursor.X - size.Width / 2;
        x = Math.Clamp(x, screen.Left + 8, screen.Right - size.Width - 8);
        int y = cursor.Y - size.Height - 8;
        if (y < screen.Top + 8)
            y = cursor.Y + 24;
        Location = new Point(x, y);
        Show();
        ForceForeground();
        InstallMouseHook();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RecreateBoldFont();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        RecreateBoldFont();
    }

    private void RecreateBoldFont()
    {
        Font? oldFont = _boldFont;
        _boldFont = new Font(Font, FontStyle.Bold);

        // Reassign every value label before disposing the old shared font.
        // Error/no-data rows deliberately continue to use the normal font.
        for (int i = 0; i < _rows.Count && i < _lastEntries.Count; i++)
            ApplyValues(_rows[i], _lastEntries[i]);

        oldFont?.Dispose();
        PerformLayout();
    }

    // Tray clicks never grant our form true input activation, so we can't rely
    // on Form.Deactivate to dismiss the popup — it only fires after the user
    // has clicked *into* the form once. A low-level mouse hook lets us watch
    // global button-down events without consuming them: clicks outside our
    // bounds hide the popup, clicks inside the form fall through normally,
    // and the target window of an outside click still receives the input.
    private void InstallMouseHook()
    {
        if (_hookId != IntPtr.Zero)
            return;
        _hookProc = OnMouseHook;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandleW(null), 0);
    }

    private void UninstallMouseHook()
    {
        if (_hookId == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _hookProc = null;
    }

    private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN ||
                msg == WM_MBUTTONDOWN || msg == WM_NCLBUTTONDOWN)
            {
                MSLLHOOKSTRUCT data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (!Bounds.Contains(data.pt.X, data.pt.Y))
                {
                    // BeginInvoke so we don't tie up the hook callback — Windows
                    // will silently unhook us if a callback exceeds its budget.
                    BeginInvoke(new Action(Hide));
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // Tray icons don't transfer foreground to our process when clicked, so a
    // plain Show()/Activate() leaves the form visible-but-unfocused — and the
    // shell promptly steals focus back, firing Deactivate and hiding the popup
    // before the user sees it. Attaching to the current foreground thread's
    // input queue bypasses Windows' foreground-stealing prevention long enough
    // for SetForegroundWindow to actually stick.
    private void ForceForeground()
    {
        IntPtr fore = GetForegroundWindow();
        if (fore == Handle)
            return;

        uint foreThread = GetWindowThreadProcessId(fore, out _);
        uint currentThread = GetCurrentThreadId();
        if (foreThread == 0 || foreThread == currentThread)
        {
            SetForegroundWindow(Handle);
            return;
        }

        AttachThreadInput(foreThread, currentThread, true);
        try { SetForegroundWindow(Handle); }
        finally { AttachThreadInput(foreThread, currentThread, false); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_NCLBUTTONDOWN = 0x00A1;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private Row CreateRow(Entry e)
    {
        var row = new Row
        {
            ProviderName = e.ProviderName,
            WindowLabel = e.WindowLabel,
            Indicator = new Panel
            {
                Size = new Size(IndicatorSize, IndicatorSize),
                Margin = new Padding(0, RowVPad + IndicatorOpticalOffset, 8, RowVPad),
                Anchor = AnchorStyles.Left,
            },
            Provider = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, RowVPad, 8, RowVPad),
                Text = e.ProviderName,
            },
            Window = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, RowVPad, 16, RowVPad),
                Text = e.WindowLabel,
            },
            Value = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, RowVPad, 16, RowVPad),
            },
            Remaining = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, RowVPad, 10, RowVPad),
                // No explicit color: inherit the form's ControlText (default) so the
                // reset/time text isn't muted and follows the theme on a live switch.
            },
            ResetTime = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, RowVPad, 0, RowVPad),
            },
        };
        ApplyValues(row, e);
        return row;
    }

    private void ApplyValues(Row row, Entry e)
    {
        row.Indicator.BackColor = e.WindowColor;

        if (e.Error is not null)
        {
            row.Value.Text = e.Error;
            row.Value.ForeColor = ErrorText;
            row.Value.Font = Font;
            ClearReset(row);
        }
        else if (e.Metric is null)
        {
            row.Value.Text = Strings.Tray_NoData;
            row.Value.ForeColor = MutedText;
            row.Value.Font = Font;
            ClearReset(row);
        }
        else
        {
            double util = e.Metric.Utilization;
            row.Value.Text = $"{util:0.#}%";
            row.Value.Font = _boldFont ?? Font;
            row.Value.ForeColor = util >= WarnPercent ? WarnText : SystemColors.ControlText;
            ApplyReset(row, e);
        }
    }

    private static void ClearReset(Row row)
    {
        row.Remaining.Text = string.Empty;
        row.ResetTime.Text = string.Empty;
    }

    private static void ApplyReset(Row row, Entry e)
    {
        if (UsageFormatting.FormatResetParts(e.Metric!.ResetsAt, e.LongDate) is not { } reset)
        {
            row.Remaining.Text = "?";
            row.ResetTime.Text = string.Empty;
            return;
        }

        row.Remaining.Text = reset.Remaining;
        row.ResetTime.Text = reset.Absolute;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(BorderColor);
        Rectangle r = ClientRectangle;
        e.Graphics.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
    }
}
