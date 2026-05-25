using System.Runtime.InteropServices;

namespace JeekTokenPlanUsage;

/// Tray icon backed by direct Shell_NotifyIcon P/Invoke so each icon carries a
/// stable NIF_GUID. Without that, Windows 11's drag-to-reorder is unreliable
/// when multiple icons originate from the same executable — the shell uses
/// (exe path + position) as the persistence key and can't tell two icons
/// apart during a reorder. With a GUID, every icon has a stable identity
/// that survives refreshes and reorders.
internal sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYMESSAGE = 0x0400 + 1024;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;

    private const int NIM_ADD = 0x0;
    private const int NIM_MODIFY = 0x1;
    private const int NIM_DELETE = 0x2;
    private const int NIM_SETVERSION = 0x4;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;
    private const uint NIF_GUID = 0x20;
    private const uint NIF_SHOWTIP = 0x80;

    private const uint NIIF_WARNING = 0x2;
    private const uint NIIF_RESPECT_QUIET_TIME = 0x80;

    private const uint NOTIFYICON_VERSION_4 = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private static readonly int WM_TASKBARCREATED = (int)RegisterWindowMessageW("TaskbarCreated");

    private readonly Guid _guid;
    private readonly ContextMenuStrip? _menu;
    private readonly MessageWindow _window;
    private readonly uint _uid = 1;

    private IntPtr _hIcon;
    private string _text = string.Empty;
    private bool _visible;
    private bool _added;
    private bool _disposed;

    public TrayIcon(Guid guid, ContextMenuStrip? menu)
    {
        _guid = guid;
        _menu = menu;
        _window = new MessageWindow(this);
    }

    public Icon? Icon
    {
        set
        {
            _hIcon = value?.Handle ?? IntPtr.Zero;
            if (_added)
                Modify();
        }
    }

    public string Text
    {
        set
        {
            // szTip is 128 wchars including null terminator.
            string v = value ?? string.Empty;
            if (v.Length > 127) v = v[..127];
            _text = v;
            if (_added)
                Modify();
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value) Add();
            else Remove();
        }
    }

    private NOTIFYICONDATAW BuildData(uint flags)
    {
        return new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = _uid,
            uFlags = flags,
            uCallbackMessage = WM_TRAYMESSAGE,
            hIcon = _hIcon,
            szTip = _text,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            guidItem = _guid,
        };
    }

    private void Add()
    {
        var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            // The GUID is likely still registered to a previous instance
            // (or a stale entry from a moved executable). Force-delete it
            // and retry — this is the documented recovery path.
            var del = BuildData(NIF_GUID);
            Shell_NotifyIconW(NIM_DELETE, ref del);
            if (!Shell_NotifyIconW(NIM_ADD, ref data))
                return;
        }
        _added = true;

        var ver = BuildData(NIF_GUID);
        ver.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref ver);
    }

    private void Modify()
    {
        var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_MODIFY, ref data))
        {
            // Shell forgot us — re-add (e.g., we missed TaskbarCreated).
            _added = false;
            Add();
        }
    }

    /// Shows a Windows toast/balloon via Shell_NotifyIcon NIF_INFO. Caps to the
    /// struct's szInfoTitle (64 wchars incl. null) and szInfo (256 wchars incl.
    /// null) limits. Re-asserts the full flag set used by Modify() so the
    /// standard tooltip stays enabled — under NOTIFYICON_VERSION_4, NIF_SHOWTIP
    /// is a state bit and gets dropped if any NIM_MODIFY call omits it.
    public void ShowNotification(string title, string message)
    {
        if (!_added) return;
        string t = title ?? string.Empty;
        if (t.Length > 63) t = t[..63];
        string m = message ?? string.Empty;
        if (m.Length > 255) m = m[..255];

        var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO | NIF_GUID | NIF_SHOWTIP);
        data.szInfoTitle = t;
        data.szInfo = m;
        data.dwInfoFlags = NIIF_WARNING | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void Remove()
    {
        if (!_added) return;
        var data = BuildData(NIF_GUID);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    private void OnTrayMessage(IntPtr lParam)
    {
        // Version-4 callback packs the notification message in lParam LOWORD.
        int msg = (int)((long)lParam & 0xFFFF);
        switch (msg)
        {
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
    }

    private void ShowMenu()
    {
        if (_menu is null) return;
        // SetForegroundWindow before Show so the menu dismisses correctly when
        // the user clicks elsewhere — without it, clicks outside the menu
        // leave it stuck on screen.
        SetForegroundWindow(_window.Handle);
        GetCursorPos(out POINT p);
        _menu.Show(p.X, p.Y);
    }

    private void OnTaskbarCreated()
    {
        if (!_visible) return;
        _added = false;
        Add();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Remove();
        _window.DestroyHandle();
    }

    private sealed class MessageWindow : NativeWindow
    {
        private readonly TrayIcon _owner;

        public MessageWindow(TrayIcon owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams { Caption = "JeekTrayIcon" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_TRAYMESSAGE)
                _owner.OnTrayMessage(m.LParam);
            else if (m.Msg == WM_TASKBARCREATED)
                _owner.OnTaskbarCreated();
            base.WndProc(ref m);
        }
    }
}
