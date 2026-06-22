using System.Globalization;
using JeekTools;

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

        ConfigureLogging();
        ConfigureProxy();

        SystemUiCulture = CultureInfo.CurrentUICulture;
        ApplyLanguageOverride();
        ApplicationConfiguration.Initialize();
        // Follow the OS light/dark setting. Must be called before any Form is
        // created so SystemColors and control-internal palettes are remapped.
        Application.SetColorMode(SystemColorMode.System);
        Application.Run(new TrayApplicationContext());

        GC.KeepAlive(mutex);
    }

    private static void ConfigureLogging()
    {
        // Route all logging through JeekTools.LogManager (ZLogger rolling files).
        // An absolute LogsDirectory overrides LogManager's default (next to the
        // exe); keeping logs under %LocalAppData% survives updates and avoids a
        // possibly read-only install directory.
        LogManager.LogsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JeekTokenPlanUsage",
            "Logs"
        );
        LogManager.ToFile = true;
        LogManager.RollingSizeKB = 1024;
        LogManager.RetainFileLimit = TimeSpan.FromDays(7);
        LogManager.EnableLogging();
    }

    private static void ConfigureProxy()
    {
        // Capture the real system proxy, then install our dynamic proxy as the
        // process-wide default so every plain HttpClient (including JeekTools'
        // mirror probing) honors the app's proxy mode. Capture must precede the
        // assignment, or AppProxy's System mode would recurse into itself.
        AppProxy.ConfigureSystemProxy(HttpClient.DefaultProxy);
        HttpClient.DefaultProxy = AppProxy.Instance;
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
