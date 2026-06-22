namespace JeekTokenPlanUsage;

/// Tiny HTTP helper for the GitHub mirror/update path. Mirror selection and
/// speed probing live in JeekTools.GitHubMirrors; this only adds the small text
/// fetch (version.txt) that JeekTools doesn't provide. The plain HttpClient
/// honors the app proxy via HttpClient.DefaultProxy, installed in Program.Main.
internal static class MirrorHttp
{
    /// GET a tiny text artifact. Returns null on any network or HTTP error so the
    /// caller can fall back. HttpClient follows 3xx redirects by default.
    public static async Task<string?> DownloadTextAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Add(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
            );
            using var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }
}
