using System.Net.Http.Headers;

namespace JeekTokenPlanUsage;

/// Mirror selector for github.com release downloads. Tests the direct URL and
/// two well-known China-friendly proxies in parallel; the first one to return
/// the leading bytes of the response wins, and its index is cached for the
/// remainder of the process lifetime. Reset() clears that cache when the user
/// toggles DisableMirrorDownload at runtime so a stale choice doesn't stick.
internal static class GitHubMirrors
{
    public static string[] GetMirrors(string url) =>
    [
        url,
        url.Replace("https://github.com/", "https://ghfast.top/https://github.com/"),
        url.Replace("https://github.com/", "https://gh-proxy.com/github.com/"),
    ];

    private static int _fastestMirrorIndex = -1;

    public static void Reset() => _fastestMirrorIndex = -1;

    public static async Task<string> GetFastestMirrorAsync(string url)
    {
        if (_fastestMirrorIndex == -1 && !await ProbeMirrorSpeedAsync(url))
            return "";

        return GetMirrors(url)[_fastestMirrorIndex];
    }

    private static async Task<bool> ProbeMirrorSpeedAsync(string url)
    {
        _fastestMirrorIndex = -1;

        var mirrors = GetMirrors(url);
        var ctsList = mirrors.Select(_ => new CancellationTokenSource()).ToList();

        var tasks = mirrors
            .Select(
                (mirror, idx) =>
                {
                    var cts = ctsList[idx];
                    return Task.Run(
                        async () =>
                        {
                            try
                            {
                                using var client = CreateClient();
                                var request = new HttpRequestMessage(HttpMethod.Get, mirror);
                                // Tiny ranged GET — enough to confirm the mirror
                                // can actually serve the artifact without paying
                                // the cost of a full download.
                                request.Headers.Range = new RangeHeaderValue(0, 102399);

                                using var response = await client.SendAsync(
                                    request,
                                    HttpCompletionOption.ResponseHeadersRead,
                                    cts.Token
                                );
                                response.EnsureSuccessStatusCode();
                                using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                                var buffer = new byte[102400];
                                int read,
                                    total = 0;
                                while (
                                    (read = await stream.ReadAsync(
                                        buffer.AsMemory(total, buffer.Length - total),
                                        cts.Token
                                    )) > 0
                                    && total < buffer.Length
                                )
                                {
                                    total += read;
                                }

                                return (success: true, index: idx);
                            }
                            catch
                            {
                                return (success: false, index: idx);
                            }
                        },
                        cts.Token
                    );
                }
            )
            .ToList();

        while (tasks.Count > 0)
        {
            var finished = await Task.WhenAny(tasks);
            var (ok, idx) = finished.Result;
            if (ok)
            {
                _fastestMirrorIndex = idx;
                foreach (var cts in ctsList)
                {
                    try { cts.Cancel(); } catch { }
                }
                return true;
            }

            int pos = tasks.IndexOf(finished);
            tasks.RemoveAt(pos);
            ctsList.RemoveAt(pos);
        }

        return false;
    }

    internal static HttpClient CreateClient(int timeoutSeconds = 5)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        client.DefaultRequestHeaders.Add(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
        );
        return client;
    }

    /// GET a tiny text artifact (e.g. version.txt). Returns null on any
    /// network or HTTP error so the caller can decide how to fall back.
    /// HttpClient handles 3xx redirects transparently by default.
    internal static async Task<string?> DownloadTextAsync(string url)
    {
        try
        {
            using var client = CreateClient();
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
