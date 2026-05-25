using System.Globalization;

namespace JeekTokenPlanUsage;

internal static class Program
{
    private const string MutexName = "Global\\JeekTokenPlanUsage.SingleInstance";

    public static CultureInfo SystemUiCulture { get; private set; } = CultureInfo.CurrentUICulture;

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
            return;

        SystemUiCulture = CultureInfo.CurrentUICulture;
        ApplyLanguageOverride();
        ApplicationConfiguration.Initialize();
        // Follow the OS light/dark setting. Must be called before any Form is
        // created so SystemColors and control-internal palettes are remapped.
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new TrayApplicationContext());

        GC.KeepAlive(mutex);
    }

    private static void ApplyLanguageOverride()
    {
        // Resolve "follow system" (empty setting) to the UI culture captured at
        // startup. The tray menu's "System default" item uses the same SystemUiCulture,
        // so switching back to it reproduces exactly what the app shows on launch.
        try
        {
            string lang = AppSettings.PeekLanguage();
            var culture = string.IsNullOrEmpty(lang)
                ? SystemUiCulture
                : CultureInfo.GetCultureInfo(lang);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException) { }
    }
}
