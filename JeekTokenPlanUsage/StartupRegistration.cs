using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace JeekTokenPlanUsage;

internal static class StartupRegistration
{
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "JeekTokenPlanUsage";
    private static string Executable => Environment.ProcessPath ?? Application.ExecutablePath;
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");

    public static bool Enabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(LegacyRunKey);
            string? legacy = key?.GetValue(AppName) as string;
            return PointsToExecutable(ShortcutPath, Executable) || IsLegacyMatch(legacy);
        }
        set
        {
            if (value)
                WriteShortcut(ShortcutPath, Executable);
            else if (PointsToExecutable(ShortcutPath, Executable))
                File.Delete(ShortcutPath);

            // Remove only this executable's legacy entry when the user changes
            // the option. New registrations never write registry values.
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            if (IsLegacyMatch(key?.GetValue(AppName) as string))
                key!.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    private static bool IsLegacyMatch(string? command) =>
        string.Equals(command?.Trim().Trim('"'), Executable, StringComparison.OrdinalIgnoreCase);

    internal static bool PointsToExecutable(string path, string executable)
    {
        if (!File.Exists(path)) return false;
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            return string.Equals((string)((dynamic)shortcut).TargetPath, executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read startup shortcut: {ex.Message}");
            return false;
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    internal static void WriteShortcut(string path, string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            shortcut = ((dynamic)shell!).CreateShortcut(path);
            ((dynamic)shortcut).TargetPath = executable;
            ((dynamic)shortcut).WorkingDirectory = Path.GetDirectoryName(executable);
            ((dynamic)shortcut).Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}
