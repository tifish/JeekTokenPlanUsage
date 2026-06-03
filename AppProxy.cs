using System.Net;

namespace JeekTokenPlanUsage;

/// Central proxy resolver shared by every outbound HTTP path: the three
/// provider HttpClients, the GitHub mirror/update client, and the Codex
/// curl.exe shell-out. A single IWebProxy instance is installed on each
/// HttpClientHandler; because IWebProxy.GetProxy is consulted per request,
/// switching the proxy mode from the tray menu takes effect immediately
/// without recreating any HttpClient.
internal static class AppProxy
{
    // Defaults to System mode until Configure runs, which is harmless: no
    // request fires before the tray context loads settings and configures us.
    private static AppSettings _settings = new();

    /// Shared dynamic proxy to install on every HttpClientHandler.
    public static IWebProxy Instance { get; } = new DynamicWebProxy();

    /// Point the resolver at the live settings instance. Called once at startup;
    /// later mutations to the same instance are picked up automatically.
    public static void Configure(AppSettings settings) => _settings = settings;

    /// Resolve the proxy URI to use for a destination under the current mode,
    /// or null for a direct connection. Reused when building the curl config so
    /// the shell-out matches the HttpClient paths.
    public static Uri? ResolveProxy(Uri destination) => _settings.ProxyMode switch
    {
        ProxyMode.Direct => null,
        ProxyMode.Custom => _settings.BuildCustomProxyUri(),
        // System: defer to the Windows system proxy (WinINET/WinHTTP) that
        // HttpClient.DefaultProxy already reflects. Returns null when none is set.
        _ => HttpClient.DefaultProxy.GetProxy(destination),
    };

    /// A fresh handler wired to the shared dynamic proxy. UseProxy stays true
    /// across all modes — Direct is expressed by GetProxy returning null.
    public static HttpClientHandler CreateHandler() => new()
    {
        Proxy = Instance,
        UseProxy = true,
    };

    private sealed class DynamicWebProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => ResolveProxy(destination);

        public bool IsBypassed(Uri host) => ResolveProxy(host) is null;
    }
}
