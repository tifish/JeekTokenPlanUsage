using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JeekTokenPlanUsage.Resources;

namespace JeekTokenPlanUsage;

/// Reads Claude Code usage from the undocumented OAuth usage endpoint,
/// using the access token maintained by Claude Code in ~/.claude/.credentials.json.
///
/// When that endpoint rate-limits (429) or otherwise fails, falls back to a
/// minimal POST /v1/messages call and reads the anthropic-ratelimit-unified-*
/// response headers — they carry the same usage data and are returned even on
/// error responses.
///
/// On 429 from the OAuth endpoint, applies an internal cooldown to that
/// endpoint only (ladder: 5/15/30/60 min). During cooldown the caller's poll
/// cadence is unaffected — we just skip OAuth and go straight to the messages
/// fallback, so we stop hammering the rate-limited endpoint without lowering
/// data freshness for the user.
public sealed class ClaudeUsageProvider : IUsageProvider
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string MessagesUrl = "https://api.anthropic.com/v1/messages";

    // Tried in order — the first model whose response carries rate-limit headers wins.
    private static readonly string[] ModelFallbackChain =
    {
        "claude-3-haiku-20240307",
        "claude-haiku-4-5-20251001",
    };

    // Cooldown applied to the OAuth usage endpoint after a 429. Each consecutive
    // 429 (after the previous cooldown elapsed) moves one step further down.
    private static readonly TimeSpan[] OauthCooldownLadder =
    {
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
    };

    private static readonly string CredentialsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    public static bool HasLocalCredentials() => File.Exists(CredentialsPath);

    private readonly HttpClient _http;

    private DateTimeOffset _oauthCooldownUntil = DateTimeOffset.MinValue;
    private int _oauthRateLimitStreak;

    public ClaudeUsageProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("JeekTokenPlanUsage/1.0");
    }

    public string Name => "Claude";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct)
    {
        string? token = ReadAccessToken(out string? credError);
        if (token is null)
            return UsageSnapshot.FromError(credError ?? Strings.Claude_ReadCredFailed);

        // If OAuth was recently rate-limited, skip it for this poll and go
        // straight to the messages fallback — calling cadence is unchanged.
        bool skipPrimary = DateTimeOffset.UtcNow < _oauthCooldownUntil;
        ProviderResult primary = skipPrimary
            ? ProviderResult.Skipped()
            : await TryUsageEndpointAsync(token, ct);

        if (primary.AuthFailed)
            return UsageSnapshot.FromError(Strings.Claude_TokenInvalid);

        if (primary.RateLimited)
        {
            // Extend cooldown using the next rung of the ladder.
            TimeSpan cd = OauthCooldownLadder[Math.Min(_oauthRateLimitStreak, OauthCooldownLadder.Length - 1)];
            _oauthCooldownUntil = DateTimeOffset.UtcNow + cd;
            _oauthRateLimitStreak++;
        }
        else if (primary.Snapshot is not null)
        {
            // OAuth came back fine — reset the cooldown ladder.
            _oauthRateLimitStreak = 0;
            _oauthCooldownUntil = DateTimeOffset.MinValue;
        }

        // Primary fully succeeded (with reset times) — no need to spend a messages call.
        if (primary.Snapshot is { } ok
            && ok.FiveHour?.ResetsAt is not null
            && ok.Weekly?.ResetsAt is not null)
            return ok;

        // Otherwise fall back to Messages API: as the data source if primary failed
        // or was skipped, or to fill in missing reset times if primary returned partial data.
        ProviderResult fallback = await TryMessagesFallbackAsync(token, ct);
        if (fallback.AuthFailed)
            return UsageSnapshot.FromError(Strings.Claude_TokenInvalid);

        if (primary.Snapshot is not null && fallback.Snapshot is not null)
            return Merge(primary.Snapshot, fallback.Snapshot);

        if (primary.Snapshot is not null)
            return primary.Snapshot;
        if (fallback.Snapshot is not null)
            return fallback.Snapshot;

        return UsageSnapshot.FromError(primary.Error ?? fallback.Error ?? Strings.Error_FetchUsage);
    }

    private async Task<ProviderResult> TryUsageEndpointAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        string body;
        try
        {
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ProviderResult.Auth();

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                return ProviderResult.RateLimit("oauth/usage HTTP 429");

            if (!resp.IsSuccessStatusCode)
                return ProviderResult.Failed($"oauth/usage HTTP {(int)resp.StatusCode}");

            // Body read kept inside the same try: a socket abort during the
            // body read (e.g. app shutdown, network reset) raises SocketException
            // 995 here, not from SendAsync.
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProviderResult.Failed(string.Format(Strings.Error_NetworkFormat, ex.Message));
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            var snap = new UsageSnapshot
            {
                FiveHour = ParseOauthMetric(root, "five_hour"),
                Weekly = ParseOauthMetric(root, "seven_day"),
            };
            return ProviderResult.Ok(snap);
        }
        catch (JsonException ex)
        {
            return ProviderResult.Failed(string.Format(Strings.Error_ParseFormat, ex.Message));
        }
    }

    private async Task<ProviderResult> TryMessagesFallbackAsync(string token, CancellationToken ct)
    {
        string? lastError = null;

        foreach (string model in ModelFallbackChain)
        {
            string payload = JsonSerializer.Serialize(new
            {
                model,
                max_tokens = 1,
                messages = new[] { new { role = "user", content = "." } },
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, MessagesUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = string.Format(Strings.Error_NetworkFormat, ex.Message);
                continue;
            }

            using (resp)
            {
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return ProviderResult.Auth();

                // Rate-limit headers are present on error responses too (including 429),
                // so we attempt to read them regardless of status code.
                UsageSnapshot? fromHeaders = ReadRateLimitHeaders(resp);
                if (fromHeaders is not null)
                    return ProviderResult.Ok(fromHeaders);

                lastError = string.Format(Strings.Claude_MessagesNoHeaderFormat, model, (int)resp.StatusCode);
            }
        }

        return ProviderResult.Failed(lastError ?? Strings.Claude_MessagesFallbackFailed);
    }

    private static UsageSnapshot? ReadRateLimitHeaders(HttpResponseMessage resp)
    {
        UsageMetric? fiveHour = ReadHeaderMetric(resp, "anthropic-ratelimit-unified-5h-utilization",
                                                       "anthropic-ratelimit-unified-5h-reset");
        UsageMetric? weekly = ReadHeaderMetric(resp, "anthropic-ratelimit-unified-7d-utilization",
                                                     "anthropic-ratelimit-unified-7d-reset");
        if (fiveHour is null && weekly is null)
            return null;
        return new UsageSnapshot { FiveHour = fiveHour, Weekly = weekly };
    }

    private static UsageMetric? ReadHeaderMetric(HttpResponseMessage resp, string utilHeader, string resetHeader)
    {
        if (!TryGetHeader(resp, utilHeader, out string? utilStr)
            || !double.TryParse(utilStr, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double util))
            return null;

        // Headers report utilization as a 0..1 fraction; the rest of the app uses 0..100.
        double percent = util * 100.0;

        DateTimeOffset? reset = null;
        if (TryGetHeader(resp, resetHeader, out string? resetStr)
            && long.TryParse(resetStr, out long unix))
            reset = DateTimeOffset.FromUnixTimeSeconds(unix);

        return new UsageMetric(percent, reset);
    }

    private static bool TryGetHeader(HttpResponseMessage resp, string name, out string? value)
    {
        if (resp.Headers.TryGetValues(name, out var values))
        {
            value = values.FirstOrDefault();
            return value is not null;
        }
        value = null;
        return false;
    }

    private static UsageSnapshot Merge(UsageSnapshot primary, UsageSnapshot fallback) => new()
    {
        FiveHour = MergeMetric(primary.FiveHour, fallback.FiveHour),
        Weekly = MergeMetric(primary.Weekly, fallback.Weekly),
    };

    // Primary supplies the utilization; fallback only fills missing reset times.
    private static UsageMetric? MergeMetric(UsageMetric? primary, UsageMetric? fallback)
    {
        if (primary is null) return fallback;
        if (primary.ResetsAt is not null || fallback?.ResetsAt is null) return primary;
        return primary with { ResetsAt = fallback.ResetsAt };
    }

    private static UsageMetric? ParseOauthMetric(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement el) || el.ValueKind != JsonValueKind.Object)
            return null;

        double util = el.TryGetProperty("utilization", out JsonElement u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble()
            : 0;

        DateTimeOffset? reset = null;
        if (el.TryGetProperty("resets_at", out JsonElement r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), out DateTimeOffset parsed))
        {
            reset = parsed;
        }

        return new UsageMetric(util, reset);
    }

    private static string? ReadAccessToken(out string? error)
    {
        error = null;
        if (!File.Exists(CredentialsPath))
        {
            error = Strings.Claude_CredNotFound;
            return null;
        }

        try
        {
            string json = File.ReadAllText(CredentialsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauth)
                && oauth.TryGetProperty("accessToken", out JsonElement tok)
                && tok.ValueKind == JsonValueKind.String)
            {
                return tok.GetString();
            }
            error = Strings.Claude_CredMissingToken;
            return null;
        }
        catch (Exception ex)
        {
            error = string.Format(Strings.Claude_ReadCredFailedFormat, ex.Message);
            return null;
        }
    }

    private readonly record struct ProviderResult(
        UsageSnapshot? Snapshot,
        bool AuthFailed,
        bool RateLimited,
        string? Error)
    {
        public static ProviderResult Ok(UsageSnapshot snap) => new(snap, false, false, null);
        public static ProviderResult Auth() => new(null, true, false, null);
        public static ProviderResult RateLimit(string error) => new(null, false, true, error);
        public static ProviderResult Failed(string error) => new(null, false, false, error);
        // Used when we deliberately did not attempt this path (e.g. OAuth in cooldown).
        // Distinguished from Failed so the "both endpoints failed" error doesn't surface
        // a misleading "skipped" message when the fallback is what actually failed.
        public static ProviderResult Skipped() => new(null, false, false, null);
    }
}
