using System.Globalization;

namespace JeekTokenPlanUsage;

internal static class Program
{
    private const string MutexName = "Global\\JeekTokenPlanUsage.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
            return;

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
        string lang = AppSettings.PeekLanguage();
        if (string.IsNullOrEmpty(lang))
            return;

        try
        {
            var culture = CultureInfo.GetCultureInfo(lang);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException) { }
    }
}
