using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace JeekTokenPlanUsage;

/// A small usage widget that lives *on* the taskbar (next to the tray clock),
/// showing a labeled progress bar + percentage for each enabled provider window.
///
/// Like CodeZeno's Claude-Code-Usage-Monitor, this is not a Windows AppBar (which
/// would reserve a separate full-width strip). Instead it embeds a layered child
/// window into the taskbar (`Shell_TrayWnd`) via SetParent and positions itself
/// just left of the tray notification area, so it rides along with the taskbar
/// (hides on fullscreen, scrolls with auto-hide, correct z-order). When embedding
/// fails it falls back to a topmost popup pinned to the same spot.
internal sealed class TaskbarWidget : IDisposable
{
    /// One row of the widget: a labeled bar + percentage (+ reset countdown) for
    /// a single usage window. LabelColor is the provider's bright accent (shared by
    /// both rows so the label stays legible); Accent is the per-window bar shade.
    public sealed record Cell(string Label, Color LabelColor, Color Accent, double? Percent, bool IsError, DateTimeOffset? ResetsAt);

    /// One provider column: its primary window (top row) and secondary (bottom row).
    public sealed record Column(Cell Primary, Cell Secondary);

    // Layout, in device-independent pixels (scaled by DPI at render time).
    private const int Pad = 5;
    private const int DividerW = 3;
    private const int DividerGap = 8;
    private const int LabelGap = 3;
    private const int BarW = 40;
    private const int BarH = 7;
    private const int BarPctGap = 3;
    private const int PctRemainGap = 10;
    private const int ColGap = 11;
    // Gap between a separator and the title of the column after it (kept larger
    // than the gap to the preceding column so the next title isn't cramped).
    private const int SepTitleGap = 7;
    private const int RowGap = 5;
    private const int FontPx = 12;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CHILD = 0x40000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;

    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_DPICHANGED = 0x02E0;
    private const int WM_DPICHANGED_BEFOREPARENT = 0x02E2;
    private const int WM_DPICHANGED_AFTERPARENT = 0x02E3;
    private const int MK_LBUTTON = 0x0001;

    private const int DragThresholdDip = 3;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")]
    private static extern int ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    private static readonly int WM_TASKBARCREATED = (int)RegisterWindowMessageW("TaskbarCreated");

    private readonly ContextMenuStrip? _menu;
    private readonly Action _onLeftClick;
    private readonly Action<int> _onOffsetChanged;
    private readonly WidgetWindow _window;
    private readonly MenuOwnerWindow _menuOwner;
    private readonly System.Windows.Forms.Timer _repositionTimer;

    private IReadOnlyList<Column> _columns = Array.Empty<Column>();
    private bool _visible;
    private bool _embedded;
    private bool _disposed;
    private double _scale = 1.0;
    private string _lastRemainSig = string.Empty;
    private bool _lastDark = true;

    private int _offset;
    private int _width;
    private int _height;

    private bool _dragging;
    private bool _dragMoved;
    private int _dragStartCursorX;
    private int _dragStartOffset;

    public TaskbarWidget(ContextMenuStrip? menu, Action onLeftClick, Action<int> onOffsetChanged, int initialOffset)
    {
        _menu = menu;
        _onLeftClick = onLeftClick;
        _onOffsetChanged = onOffsetChanged;
        _offset = Math.Max(0, initialOffset);

        _menuOwner = new MenuOwnerWindow();
        _window = new WidgetWindow(this);

        _repositionTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _repositionTimer.Tick += (_, _) => OnTick();
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value)
                _repositionTimer.Start();
            else
                _repositionTimer.Stop();
            RefreshVisual();
        }
    }

    /// Replace the displayed columns and repaint. Safe to call while hidden;
    /// content is cached and rendered the next time the widget becomes visible.
    public void Update(IReadOnlyList<Column> columns)
    {
        _columns = columns;
        if (_visible)
            RefreshVisual();
    }

    /// Re-renders immediately after a system light/dark switch.
    public void NotifyThemeChanged()
    {
        if (_visible)
            RefreshVisual();
    }

    /// Recomputes the taskbar-derived scale after display or DPI changes.
    public void NotifyDisplayChanged()
    {
        if (_visible)
            RefreshVisual();
    }

    private int Sc(int dip) => (int)Math.Round(dip * _scale);

    // The measured taskbar height drives both the widget height and its scale —
    // a standard 48px taskbar maps to scale 1.0. This sidesteps the unreliable
    // per-window DPI reported for a cross-process taskbar child and guarantees
    // the widget always fits the taskbar exactly (at any display scaling).
    private static int TaskbarHeight()
    {
        IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && GetWindowRect(taskbar, out RECT tb))
        {
            int h = tb.Bottom - tb.Top;
            if (h > 0)
                return h;
        }
        return 48;
    }

    private void RefreshVisual()
    {
        if (!_visible || _columns.Count == 0)
        {
            ShowWindow(_window.Handle, SW_HIDE);
            return;
        }

        EnsureEmbedded();
        Render();
        Position();
        ShowWindow(_window.Handle, SW_SHOWNA);
    }

    // Runs each second while visible: tracks the taskbar, and re-renders when the
    // reset countdown text changes (≈ once a minute) or the system switches between
    // light/dark, so the labels stay live and the colors match the taskbar.
    private void OnTick()
    {
        if (_dragging || !_visible || _columns.Count == 0)
            return;

        bool layoutChanged = TaskbarHeight() != _height;
        if (layoutChanged || RemainingSignature() != _lastRemainSig || IsTaskbarDark() != _lastDark)
        {
            Render();
            Position();
            return;
        }

        Position();
    }

    // The widget is painted onto the taskbar, whose color follows the Windows
    // ("system") theme — which can differ from the app theme.
    private static bool IsTaskbarDark() => SystemTheme.TaskbarDark;

    private static string RemainText(Cell c) =>
        !c.IsError && c.ResetsAt is DateTimeOffset r
            ? UsageFormatting.FormatRemainingShort(r - DateTimeOffset.Now)
            : string.Empty;

    private static string PctText(Cell c) =>
        c.IsError ? "ERR"
        : c.Percent is double p ? $"{(int)Math.Round(p)}%"
        : "--";

    private static int Ceil(float v) => (int)Math.Ceiling(v);

    private string RemainingSignature()
    {
        var sb = new System.Text.StringBuilder();
        foreach (Column c in _columns)
            sb.Append(RemainText(c.Primary)).Append('|').Append(RemainText(c.Secondary)).Append(';');
        return sb.ToString();
    }

    private void EnsureEmbedded()
    {
        if (_embedded)
            return;
        IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero)
        {
            // No taskbar yet: run as a topmost popup pinned over the tray area.
            IntPtr ex = GetWindowLongPtr(_window.Handle, GWL_EXSTYLE);
            SetWindowLongPtr(_window.Handle, GWL_EXSTYLE,
                (IntPtr)((long)ex | WS_EX_TOPMOST));
            return;
        }

        IntPtr style = GetWindowLongPtr(_window.Handle, GWL_STYLE);
        long ns = ((long)style & ~0x80000000L) | WS_CHILD | WS_CLIPSIBLINGS;
        SetWindowLongPtr(_window.Handle, GWL_STYLE, (IntPtr)ns);
        SetParent(_window.Handle, taskbar);
        _embedded = true;
    }

    private void Position()
    {
        if (_width == 0 || _height == 0)
            return;
        IntPtr taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out RECT tb))
            return;

        int trayLeft = tb.Right;
        IntPtr tray = FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out RECT tr))
            trayLeft = tr.Left;

        int offsetPx = Sc(_offset);
        int y = Math.Max(tb.Top, tb.Bottom - _height);

        if (_embedded)
        {
            int x = trayLeft - tb.Left - _width - offsetPx;
            MoveWindow(_window.Handle, x, y - tb.Top, _width, _height, false);
        }
        else
        {
            int x = trayLeft - _width - offsetPx;
            MoveWindow(_window.Handle, x, y, _width, _height, false);
            SetWindowPos(_window.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private void Render()
    {
        int taskbarH = TaskbarHeight();
        _scale = Math.Clamp(taskbarH / 48.0, 0.5, 4.0);
        bool dark = IsTaskbarDark();
        _lastDark = dark;

        // Opaque background matching the taskbar color (no transparency).
        Color bgColor = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        Color trackColor = dark ? Color.FromArgb(72, 72, 72) : Color.FromArgb(205, 205, 205);

        float fontPx = Sc(FontPx);
        using var font = new Font("Segoe UI", fontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        using var boldFont = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel);

        int n = _columns.Count;

        // Label / percentage / remaining widths are all measured per-column (not a
        // global max) so each provider's cell hugs its own content instead of being
        // padded out to the widest value across all providers (e.g. a "8%" column no
        // longer reserves room for "100%"). Measured with the same typographic format
        // used for drawing — the default StringFormat's padding would inflate widths.
        int[] colLabelW = new int[n], colPctW = new int[n], colRemainW = new int[n];
        using (var meas = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap })
        using (var mb = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(mb))
        {
            mg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            for (int ci = 0; ci < n; ci++)
            {
                Column c = _columns[ci];
                colLabelW[ci] = Ceil(Math.Max(
                    mg.MeasureString(c.Primary.Label, font, PointF.Empty, meas).Width,
                    mg.MeasureString(c.Secondary.Label, font, PointF.Empty, meas).Width)) + Sc(2);
                colPctW[ci] = Ceil(Math.Max(
                    mg.MeasureString(PctText(c.Primary), boldFont, PointF.Empty, meas).Width,
                    mg.MeasureString(PctText(c.Secondary), boldFont, PointF.Empty, meas).Width)) + Sc(2);
                float r = Math.Max(
                    mg.MeasureString(RemainText(c.Primary), font, PointF.Empty, meas).Width,
                    mg.MeasureString(RemainText(c.Secondary), font, PointF.Empty, meas).Width);
                // Zero when this provider has no reset time — the column collapses away.
                colRemainW[ci] = r > 0 ? Ceil(r) + Sc(2) : 0;
            }
        }

        int barW = Sc(BarW), barH = Sc(BarH), labelGap = Sc(LabelGap), barPctGap = Sc(BarPctGap);
        int colGap = Sc(ColGap), pad = Sc(Pad), rowGap = Sc(RowGap);
        int contentX = pad + Sc(DividerW) + Sc(DividerGap);

        int[] colCellW = new int[n];
        int totalCells = 0;
        for (int ci = 0; ci < n; ci++)
        {
            int prg = colRemainW[ci] > 0 ? Sc(PctRemainGap) : 0;
            colCellW[ci] = colLabelW[ci] + labelGap + barW + barPctGap + colPctW[ci] + prg + colRemainW[ci];
            totalCells += colCellW[ci];
        }

        // Fill the taskbar's height; the two rows split the remaining space.
        _height = taskbarH;
        int rowH = Math.Max(1, (_height - 2 * pad - rowGap) / 2);
        _width = contentX + totalCells + colGap * (n - 1) + pad;

        using var bmp = new Bitmap(_width, _height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(bgColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // ClearTypeGridFit (subpixel + hinting) is safe because the widget
            // has an opaque background; AntiAlias would render small UI text
            // blurry due to lack of grid alignment.
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Left grab-handle divider (also signals the widget is draggable).
            using (var divBrush = new SolidBrush(trackColor))
            {
                int divTop = Sc(4), divBottom = _height - Sc(4);
                g.FillRectangle(divBrush, pad, divTop, Sc(DividerW), divBottom - divTop);
            }

            int row1 = pad;
            int row2 = pad + rowH + rowGap;
            int sepTop = Sc(4), sepBottom = _height - Sc(4);
            using var sepBrush = new SolidBrush(trackColor);
            int x = contentX;
            for (int ci = 0; ci < n; ci++)
            {
                // Thin vertical separator between providers, biased toward the
                // previous column so the following title keeps a little breathing room.
                if (ci > 0)
                {
                    int sepW = Sc(1);
                    g.FillRectangle(sepBrush, x - Sc(SepTitleGap) - sepW, sepTop, sepW, sepBottom - sepTop);
                }

                Column c = _columns[ci];
                int lw = colLabelW[ci], pw = colPctW[ci], rw = colRemainW[ci];
                int prg = rw > 0 ? Sc(PctRemainGap) : 0;
                DrawCell(g, font, boldFont, c.Primary, x, row1, rowH, lw, labelGap, barW, barH, barPctGap, pw,
                    prg, rw, trackColor, dark);
                DrawCell(g, font, boldFont, c.Secondary, x, row2, rowH, lw, labelGap, barW, barH, barPctGap, pw,
                    prg, rw, trackColor, dark);
                x += colCellW[ci] + colGap;
            }
        }

        _lastRemainSig = RemainingSignature();
        PushBitmap(bmp);
    }

    private void DrawCell(
        Graphics g, Font font, Font boldFont, Cell cell, int x, int top, int rowH,
        int labelW, int labelGap, int barW, int barH, int barPctGap, int pctW,
        int pctRemainGap, int remainW,
        Color trackColor,
        bool dark)
    {
        Color textColor = ThemeTextColor(cell.LabelColor, dark);
        using var labelFmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        // Window label (5h / 7d / Auto …) painted in the provider's bright accent —
        // the color identifies the provider now that there's no text prefix.
        using var labelBrush = new SolidBrush(textColor);
        g.DrawString(cell.Label, font, labelBrush, new RectangleF(x, top, labelW, rowH), labelFmt);

        // Bar track.
        int barX = x + labelW + labelGap;
        int barY = top + (rowH - barH) / 2;
        using (var track = new SolidBrush(trackColor))
            FillRounded(g, track, barX, barY, barW, barH);

        // Bar fill (accent), only when we have a real percentage.
        if (!cell.IsError && cell.Percent is double pct)
        {
            double frac = Math.Clamp(pct / 100.0, 0, 1);
            int fillW = (int)Math.Round(barW * frac);
            if (fillW > 0)
                using (var fill = new SolidBrush(cell.Accent))
                    FillRounded(g, fill, barX, barY, fillW, barH);
        }

        // All text in a provider cell uses the provider accent, contrast-tuned for the taskbar theme.
        string pctText = PctText(cell);

        int textX = barX + barW + barPctGap;

        // Percentage right-aligned within its (per-column) width so values line up.
        using (var pctFmt = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        })
        using (var pctBrush = new SolidBrush(textColor))
            g.DrawString(pctText, boldFont, pctBrush, new RectangleF(textX, top, pctW, rowH), pctFmt);

        // Reset countdown, left-aligned just after the percentage column.
        if (remainW > 0)
        {
            string remain = RemainText(cell);
            if (remain.Length > 0)
            {
                using var remainFmt = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap,
                };
                using var remainBrush = new SolidBrush(textColor);
                int remainX = textX + pctW + pctRemainGap;
                g.DrawString(remain, font, remainBrush, new RectangleF(remainX, top, remainW, rowH), remainFmt);
            }
        }
    }

    private static Color ThemeTextColor(Color color, bool dark) =>
        Blend(color, dark ? Color.White : Color.Black, dark ? 0.34 : 0.30);

    private static Color Blend(Color source, Color target, double amount)
    {
        int BlendChannel(int s, int t) => (int)Math.Round(s + (t - s) * amount);
        return Color.FromArgb(
            source.A,
            BlendChannel(source.R, target.R),
            BlendChannel(source.G, target.G),
            BlendChannel(source.B, target.B));
    }

    private void FillRounded(Graphics g, Brush brush, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        int r = Math.Min(Sc(2), Math.Min(w, h) / 2);
        if (r <= 0)
        {
            g.FillRectangle(brush, x, y, w, h);
            return;
        }
        int d = r * 2;
        using var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private void PushBitmap(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;

        // UpdateLayeredWindow with AC_SRC_ALPHA needs a 32bpp DIB whose alpha
        // byte is actually populated and premultiplied. GDI+'s GetHbitmap drops
        // the alpha channel (the whole window then renders transparent), so we
        // own the surface: a top-down DIB section we fill from the rendered
        // bitmap's premultiplied pixels.
        var bmih = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0, // BI_RGB
        };

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr dib = CreateDIBSection(memDc, ref bmih, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return;
        }

        IntPtr old = SelectObject(memDc, dib);
        try
        {
            CopyPremultiplied(bmp, bits);
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            // pptDst = null: keep the position set by MoveWindow; only size+content change.
            UpdateLayeredWindow(_window.Handle, screenDc, IntPtr.Zero, ref size,
                memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // Copies bmp (straight-alpha BGRA) into dst, premultiplying each pixel for
    // AC_SRC_ALPHA. Fully transparent pixels become alpha 1 (invisible but still
    // hit-testable) so the whole widget can be dragged / right-clicked.
    private static void CopyPremultiplied(Bitmap bmp, IntPtr dst)
    {
        int w = bmp.Width, h = bmp.Height;
        BitmapData data = bmp.LockBits(
            new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * h;
            byte[] buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);
            for (int i = 0; i < bytes; i += 4)
            {
                byte a = buf[i + 3];
                if (a == 0)
                {
                    buf[i] = 0; buf[i + 1] = 0; buf[i + 2] = 0; buf[i + 3] = 1;
                }
                else if (a != 255)
                {
                    buf[i] = (byte)(buf[i] * a / 255);
                    buf[i + 1] = (byte)(buf[i + 1] * a / 255);
                    buf[i + 2] = (byte)(buf[i + 2] * a / 255);
                }
            }
            Marshal.Copy(buf, 0, dst, bytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private void OnMouseDown()
    {
        GetCursorPos(out POINT p);
        _dragging = true;
        _dragMoved = false;
        _dragStartCursorX = p.X;
        _dragStartOffset = _offset;
        SetCapture(_window.Handle);
    }

    private void OnMouseMove(int wParam)
    {
        if (!_dragging || (wParam & MK_LBUTTON) == 0)
            return;
        GetCursorPos(out POINT p);
        int dxPx = p.X - _dragStartCursorX;
        if (!_dragMoved && Math.Abs(dxPx) < Sc(DragThresholdDip))
            return;
        _dragMoved = true;
        // The widget sits left of the tray; dragging it left (negative dx)
        // grows the offset (pushes it further from the tray).
        int dxDip = (int)Math.Round(dxPx / _scale);
        _offset = Math.Max(0, _dragStartOffset - dxDip);
        Position();
    }

    private void OnMouseUp()
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseCapture();
        if (_dragMoved)
            _onOffsetChanged(_offset);
        else
            _onLeftClick();
    }

    private void ShowMenu()
    {
        if (_menu is null) return;
        // A child/no-activate window can't take the foreground itself, so we
        // borrow a hidden top-level owner — same trick the tray icon uses — to
        // make the menu dismiss correctly on an outside click.
        SetForegroundWindow(_menuOwner.Handle);
        GetCursorPos(out POINT p);
        _menu.Show(p.X, p.Y);
    }

    private void OnTaskbarCreated()
    {
        // Explorer restarted: our embed and registration are gone. Re-embed and
        // re-render if we're supposed to be visible.
        _embedded = false;
        if (_visible)
            RefreshVisual();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _repositionTimer.Stop();
        _repositionTimer.Dispose();
        ShowWindow(_window.Handle, SW_HIDE);
        _window.DestroyHandle();
        _menuOwner.DestroyHandle();
    }

    /// The layered widget surface. All painting goes through UpdateLayeredWindow,
    /// so this window never handles WM_PAINT; it only routes mouse + shell messages.
    private sealed class WidgetWindow : NativeWindow
    {
        private readonly TaskbarWidget _owner;

        public WidgetWindow(TaskbarWidget owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "JeekTaskbarWidget",
                Style = WS_POPUP,
                ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                X = 0,
                Y = 0,
                Width = 10,
                Height = 10,
            });
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_LBUTTONDOWN:
                    _owner.OnMouseDown();
                    return;
                case WM_MOUSEMOVE:
                    _owner.OnMouseMove((int)m.WParam);
                    return;
                case WM_LBUTTONUP:
                    _owner.OnMouseUp();
                    return;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    _owner.ShowMenu();
                    return;
                case WM_DISPLAYCHANGE:
                case WM_DPICHANGED:
                case WM_DPICHANGED_BEFOREPARENT:
                case WM_DPICHANGED_AFTERPARENT:
                    _owner.NotifyDisplayChanged();
                    return;
                default:
                    if (m.Msg == WM_TASKBARCREATED)
                        _owner.OnTaskbarCreated();
                    break;
            }
            base.WndProc(ref m);
        }
    }

    /// Hidden top-level window used only as the foreground owner when showing the
    /// shared context menu (see ShowMenu).
    private sealed class MenuOwnerWindow : NativeWindow
    {
        public MenuOwnerWindow() => CreateHandle(new CreateParams { Caption = "JeekTaskbarWidgetMenuOwner" });
    }
}
