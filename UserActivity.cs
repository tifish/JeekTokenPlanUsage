using System.Runtime.InteropServices;

namespace JeekTokenPlanUsage;

internal static class UserActivity
{
    public static bool IsAway(TimeSpan idleAfter) =>
        IsWorkstationLocked()
        || (idleAfter > TimeSpan.Zero && GetIdleTime() >= idleAfter);

    private static TimeSpan GetIdleTime()
    {
        var info = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>(),
        };

        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        uint elapsedMs = unchecked(GetTickCount() - info.LastInputTick);
        return TimeSpan.FromMilliseconds(elapsedMs);
    }

    private static bool IsWorkstationLocked()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 0);
        if (desktop == IntPtr.Zero)
            return true;

        CloseDesktop(desktop);
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint LastInputTick;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
