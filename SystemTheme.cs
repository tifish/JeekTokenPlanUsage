using Microsoft.Win32;

namespace JeekTokenPlanUsage;

/// Reads the current Windows light/dark setting straight from the registry, so
/// colors reflect the OS the instant it changes. WinForms' SystemColors and
/// Application.IsDarkModeEnabled are remapped once at startup and don't refresh
/// on a runtime theme switch, which is why we read this ourselves.
///
/// Two independent settings exist: the "app" mode (windows/menus/popups) and the
/// "Windows" mode (taskbar/system). They can differ, so each surface reads the
/// one that actually governs its background.
internal static class SystemTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// Dark mode for app windows, menus and popups (AppsUseLightTheme == 0).
    public static bool AppsDark => Read("AppsUseLightTheme");

    /// Dark mode for the taskbar / system surfaces (SystemUsesLightTheme == 0).
    public static bool TaskbarDark => Read("SystemUsesLightTheme");

    private static bool Read(string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            if (key?.GetValue(valueName) is int v)
                return v == 0;
        }
        catch { }
        return true; // assume dark when the value is unavailable
    }
}
