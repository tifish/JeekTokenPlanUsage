using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using JeekTokenPlanUsage.Resources;

namespace JeekTokenPlanUsage;

/// Reads Grok (SuperGrok / Grok Build) usage from the CLI chat-proxy billing
/// endpoints, using the OAuth session stored by the Grok CLI in ~/.grok/auth.json.
///
/// Two parallel GETs:
///   /v1/billing?format=credits  → SuperGrok weekly pool + reset window
///   /v1/billing                 → monthly included credits (used / limit)
///
/// On 401, refreshes the access token via the OIDC token endpoint (or a light
/// Grok CLI call) and retries once — same recovery shape as Codex.
public sealed class GrokUsageProvider : IUsageProvider
{
    private const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing";
    private const string TokenUrl = "https://auth.x.ai/oauth2/token";
    private const string ClientVersion = "0.2.93";
    private const int CliRefreshTimeoutSeconds = 30;

    private static readonly string AuthPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".grok",
        "auth.json");

    public static bool HasLocalCredentials() => File.Exists(AuthPath);

    public static string CredentialWatchSignature()
    {
        try
        {
            var info = new FileInfo(AuthPath);
            if (!info.Exists)
                return $"grok:{AuthPath}|missing";

            return $"grok:{AuthPath}|present|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"grok:{AuthPath}|unavailable";
        }
    }

    private readonly HttpClient _http;

    public GrokUsageProvider()
    {
        _http = new HttpClient(AppProxy.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"JeekTokenPlanUsage/1.0 (grok-cli/{ClientVersion})");
    }

    public string Name => "Grok";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct)
    {
        GrokAuth? auth = ReadAuth(out string? credError);
        if (auth is null)
            return UsageSnapshot.FromError(credError ?? Strings.Grok_ReadCredFailed, UsageErrorKind.Auth);

        if (IsExpired(auth))
        {
            Log.Info("Grok access token expired; attempting refresh before poll");
            auth = await RefreshAndRereadAsync(auth, ct);
            if (auth is null)
                return UsageSnapshot.FromError(Strings.Grok_TokenInvalid, UsageErrorKind.Auth);
        }

        FetchResult result = await FetchBillingAsync(auth, ct);
        if (result.AuthFailed)
        {
            Log.Warn("Grok billing auth failed; attempting token refresh");
            GrokAuth? refreshed = await RefreshAndRereadAsync(auth, ct);
            if (refreshed is null || string.Equals(refreshed.AccessToken, auth.AccessToken, StringComparison.Ordinal))
            {
                Log.Warn("Grok auth retry skipped: refreshed credential unavailable or unchanged");
                return UsageSnapshot.FromError(Strings.Grok_TokenInvalid, UsageErrorKind.Auth);
            }

            result = await FetchBillingAsync(refreshed, ct);
        }

        if (result.AuthFailed)
            return UsageSnapshot.FromError(Strings.Grok_TokenInvalid, UsageErrorKind.Auth);
        if (result.Snapshot is not null)
        {
            double week = result.Snapshot.FiveHour?.Utilization ?? -1;
            double month = result.Snapshot.Weekly?.Utilization ?? -1;
            Log.Info($"Grok usage refreshed: week={week:0.##}% month={month:0.##}%");
            return result.Snapshot;
        }
        return UsageSnapshot.FromError(result.Error ?? Strings.Error_FetchUsage);
    }

    private async Task<GrokAuth?> RefreshAndRereadAsync(GrokAuth previous, CancellationToken ct)
    {
        // Prefer a silent OIDC refresh so we don't spawn a CLI process on every 401.
        bool oidcOk = await TryOidcRefreshAsync(previous, ct);
        if (!oidcOk)
            RefreshViaCli();

        GrokAuth? next = ReadAuth(out _);
        if (next is null)
            return null;
        if (string.Equals(next.AccessToken, previous.AccessToken, StringComparison.Ordinal) && IsExpired(next))
            return null;
        return next;
    }

    private async Task<FetchResult> FetchBillingAsync(GrokAuth auth, CancellationToken ct)
    {
        // Weekly SuperGrok pool + monthly included credits in parallel.
        Task<(string? body, string? error, bool authFailed)> creditsTask =
            GetBillingAsync($"{BillingUrl}?format=credits", auth, ct);
        Task<(string? body, string? error, bool authFailed)> monthlyTask =
            GetBillingAsync(BillingUrl, auth, ct);

        (string? creditsBody, string? creditsError, bool creditsAuth) = await creditsTask;
        (string? monthlyBody, string? monthlyError, bool monthlyAuth) = await monthlyTask;

        if (creditsAuth || monthlyAuth)
            return FetchResult.Auth();

        if (creditsBody is null && monthlyBody is null)
            return FetchResult.Failed(creditsError ?? monthlyError ?? Strings.Error_FetchUsage);

        return FetchResult.Ok(Parse(creditsBody, monthlyBody));
    }

    private async Task<(string? body, string? error, bool authFailed)> GetBillingAsync(
        string url, GrokAuth auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        if (!string.IsNullOrEmpty(auth.UserId))
            req.Headers.TryAddWithoutValidation("x-userid", auth.UserId);
        req.Headers.TryAddWithoutValidation("x-grok-client-version", ClientVersion);
        req.Headers.Accept.ParseAdd("application/json");

        try
        {
            using HttpResponseMessage resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Log.Warn($"Grok billing auth error HTTP {(int)resp.StatusCode} for {url}");
                return (null, null, true);
            }

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Log.Warn("Grok billing HTTP 429");
                return (null, Strings.Error_RateLimit429, false);
            }

            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"Grok billing HTTP {(int)resp.StatusCode} for {url}");
                return (null, $"HTTP {(int)resp.StatusCode}", false);
            }

            return (await resp.Content.ReadAsStringAsync(ct), null, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Grok billing network error: {ex.Message}");
            return (null, string.Format(Strings.Error_NetworkFormat, ex.Message), false);
        }
    }

    private static UsageSnapshot Parse(string? creditsBody, string? monthlyBody)
    {
        try
        {
            UsageMetric? weekly = null;
            UsageMetric? monthly = null;

            if (creditsBody is not null)
                weekly = ParseCreditsWindow(creditsBody);

            if (monthlyBody is not null)
                monthly = ParseMonthlyWindow(monthlyBody);

            if (weekly is null && monthly is null)
                return UsageSnapshot.FromError(Strings.Grok_ResponseMissingConfig);

            return new UsageSnapshot
            {
                // Slot 1 = SuperGrok weekly pool; Slot 2 = monthly included credits.
                FiveHour = weekly,
                Weekly = monthly,
            };
        }
        catch (JsonException ex)
        {
            return UsageSnapshot.FromError(string.Format(Strings.Error_ParseFormat, ex.Message));
        }
    }

    private static UsageMetric? ParseCreditsWindow(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("config", out JsonElement config)
            || config.ValueKind != JsonValueKind.Object)
            return null;

        // Prefer the server's percentage when present; default to 0 so the tray
        // still shows the weekly reset countdown for SuperGrok users.
        double percent = 0;
        if (TryReadNumber(config, "creditUsagePercent", out double p))
            percent = p;
        else if (TryReadWrappedNumber(config, "creditUsagePercent", out double wrapped))
            percent = wrapped;

        DateTimeOffset? reset = null;
        if (config.TryGetProperty("currentPeriod", out JsonElement period)
            && period.ValueKind == JsonValueKind.Object)
        {
            if (period.TryGetProperty("end", out JsonElement end)
                && end.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(end.GetString(), out DateTimeOffset endAt))
                reset = endAt;
        }

        if (reset is null
            && config.TryGetProperty("billingPeriodEnd", out JsonElement bpe)
            && bpe.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(bpe.GetString(), out DateTimeOffset end2))
            reset = end2;

        // No usable signal at all (no period and no percent field) — skip the slot.
        bool hasPeriod = reset is not null
            || (config.TryGetProperty("currentPeriod", out JsonElement cp)
                && cp.ValueKind == JsonValueKind.Object);
        bool hasPercentField = config.TryGetProperty("creditUsagePercent", out _);
        if (!hasPeriod && !hasPercentField)
            return null;

        return new UsageMetric(percent, reset);
    }

    private static UsageMetric? ParseMonthlyWindow(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("config", out JsonElement config)
            || config.ValueKind != JsonValueKind.Object)
            return null;

        if (!TryReadWrappedNumber(config, "monthlyLimit", out double limit) || limit <= 0)
            return null;
        if (!TryReadWrappedNumber(config, "used", out double used))
            used = 0;

        double percent = used / limit * 100.0;

        DateTimeOffset? reset = null;
        if (config.TryGetProperty("billingPeriodEnd", out JsonElement bpe)
            && bpe.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(bpe.GetString(), out DateTimeOffset endAt))
            reset = endAt;

        return new UsageMetric(percent, reset);
    }

    private static bool TryReadNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out JsonElement el))
            return false;
        if (el.ValueKind == JsonValueKind.Number)
        {
            value = el.GetDouble();
            return true;
        }
        return false;
    }

    // Billing numbers arrive as {"val": N} wrappers from the CLI proxy.
    private static bool TryReadWrappedNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(name, out JsonElement el))
            return false;
        if (el.ValueKind == JsonValueKind.Number)
        {
            value = el.GetDouble();
            return true;
        }
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("val", out JsonElement val)
            && val.ValueKind == JsonValueKind.Number)
        {
            value = val.GetDouble();
            return true;
        }
        return false;
    }

    private static GrokAuth? ReadAuth(out string? error)
    {
        error = null;
        if (!File.Exists(AuthPath))
        {
            error = Strings.Grok_AuthNotFound;
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(AuthPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = Strings.Grok_AuthMissingToken;
                return null;
            }

            // Prefer the entry with the latest expiry (most recently refreshed).
            JsonElement best = default;
            bool found = false;
            DateTimeOffset bestExpiry = DateTimeOffset.MinValue;
            string? bestScope = null;

            foreach (JsonProperty entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (!entry.Value.TryGetProperty("key", out JsonElement keyEl)
                    || keyEl.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(keyEl.GetString()))
                    continue;

                DateTimeOffset expiry = DateTimeOffset.MinValue;
                if (entry.Value.TryGetProperty("expires_at", out JsonElement expEl)
                    && expEl.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(expEl.GetString(), out DateTimeOffset parsed))
                    expiry = parsed;

                if (!found || expiry > bestExpiry)
                {
                    best = entry.Value;
                    bestExpiry = expiry;
                    bestScope = entry.Name;
                    found = true;
                }
            }

            if (!found)
            {
                error = Strings.Grok_AuthMissingToken;
                return null;
            }

            string token = best.GetProperty("key").GetString()!;
            string? userId = best.TryGetProperty("user_id", out JsonElement uid)
                && uid.ValueKind == JsonValueKind.String
                    ? uid.GetString()
                    : null;
            string? refresh = best.TryGetProperty("refresh_token", out JsonElement rt)
                && rt.ValueKind == JsonValueKind.String
                    ? rt.GetString()
                    : null;
            string? clientId = best.TryGetProperty("oidc_client_id", out JsonElement cid)
                && cid.ValueKind == JsonValueKind.String
                    ? cid.GetString()
                    : null;
            DateTimeOffset? expiresAt = bestExpiry > DateTimeOffset.MinValue ? bestExpiry : null;

            // Fall back to client_id embedded in the scope key "issuer::client_id".
            if (string.IsNullOrEmpty(clientId) && bestScope is not null)
            {
                int sep = bestScope.LastIndexOf("::", StringComparison.Ordinal);
                if (sep >= 0 && sep + 2 < bestScope.Length)
                    clientId = bestScope[(sep + 2)..];
            }

            return new GrokAuth(token, userId, refresh, clientId, expiresAt, bestScope);
        }
        catch (Exception ex)
        {
            Log.Error($"Grok auth read failed: {ex.Message}");
            error = string.Format(Strings.Grok_ReadAuthFailedFormat, ex.Message);
            return null;
        }
    }

    private static bool IsExpired(GrokAuth auth)
    {
        if (auth.ExpiresAt is null)
            return false;
        // Refresh a minute early so a slow poll doesn't race the expiry.
        return DateTimeOffset.UtcNow >= auth.ExpiresAt.Value.AddMinutes(-1);
    }

    private async Task<bool> TryOidcRefreshAsync(GrokAuth auth, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(auth.RefreshToken) || string.IsNullOrEmpty(auth.ClientId))
        {
            Log.Info("Grok OIDC refresh skipped: missing refresh_token or client_id");
            return false;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = auth.RefreshToken!,
                    ["client_id"] = auth.ClientId!,
                }),
            };

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"Grok OIDC refresh HTTP {(int)resp.StatusCode}");
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("access_token", out JsonElement at)
                || at.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(at.GetString()))
            {
                Log.Warn("Grok OIDC refresh response missing access_token");
                return false;
            }

            string accessToken = at.GetString()!;
            string? newRefresh = doc.RootElement.TryGetProperty("refresh_token", out JsonElement nrt)
                && nrt.ValueKind == JsonValueKind.String
                    ? nrt.GetString()
                    : null;
            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out JsonElement ei)
                && ei.ValueKind == JsonValueKind.Number
                    ? ei.GetInt32()
                    : 3600;

            if (!WriteRefreshedToken(auth.ScopeKey, accessToken, newRefresh, expiresIn))
                return false;

            Log.Info("Grok OIDC token refresh succeeded");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"Grok OIDC refresh failed: {ex.Message}");
            return false;
        }
    }

    private static bool WriteRefreshedToken(
        string? scopeKey, string accessToken, string? newRefreshToken, int expiresInSeconds)
    {
        if (string.IsNullOrEmpty(scopeKey) || !File.Exists(AuthPath))
            return false;

        try
        {
            string json = File.ReadAllText(AuthPath);
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (JsonProperty entry in doc.RootElement.EnumerateObject())
                {
                    if (entry.Name != scopeKey)
                    {
                        entry.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName(entry.Name);
                    writer.WriteStartObject();
                    bool wroteKey = false;
                    bool wroteExp = false;
                    bool wroteRefresh = false;
                    foreach (JsonProperty field in entry.Value.EnumerateObject())
                    {
                        if (field.Name == "key")
                        {
                            writer.WriteString("key", accessToken);
                            wroteKey = true;
                        }
                        else if (field.Name == "expires_at")
                        {
                            writer.WriteString(
                                "expires_at",
                                DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds)
                                    .ToString("o"));
                            wroteExp = true;
                        }
                        else if (field.Name == "refresh_token" && !string.IsNullOrEmpty(newRefreshToken))
                        {
                            writer.WriteString("refresh_token", newRefreshToken);
                            wroteRefresh = true;
                        }
                        else
                        {
                            field.WriteTo(writer);
                        }
                    }

                    if (!wroteKey)
                        writer.WriteString("key", accessToken);
                    if (!wroteExp)
                        writer.WriteString(
                            "expires_at",
                            DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToString("o"));
                    if (!wroteRefresh && !string.IsNullOrEmpty(newRefreshToken))
                        writer.WriteString("refresh_token", newRefreshToken);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }

            File.WriteAllBytes(AuthPath, stream.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Grok auth.json write after refresh failed: {ex.Message}");
            return false;
        }
    }

    private static void RefreshViaCli()
    {
        string grokPath = ResolveGrokPath();
        Log.Info($"Attempting Grok token refresh via {Path.GetFileName(grokPath)} models");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = grokPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("models");

            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                Log.Warn("Grok CLI refresh failed: Process.Start returned null");
                return;
            }

            proc.StandardInput.Close();
            if (!proc.WaitForExit(CliRefreshTimeoutSeconds * 1000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                Log.Warn($"Grok CLI refresh timed out after {CliRefreshTimeoutSeconds}s");
                return;
            }

            if (proc.ExitCode == 0)
                Log.Info("Grok CLI refresh succeeded");
            else
                Log.Warn($"Grok CLI refresh failed: exit {proc.ExitCode}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Grok CLI refresh failed: {ex.Message}");
        }
    }

    private static string ResolveGrokPath()
    {
        string home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".grok", "bin", "grok.exe");
        if (File.Exists(home))
            return home;

        foreach (string name in new[] { "grok.exe", "grok.cmd", "grok" })
        {
            try
            {
                var psi = new ProcessStartInfo("where.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(name);
                using Process? p = Process.Start(psi);
                if (p is null) continue;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                string? first = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first))
                    return first.Trim();
            }
            catch { }
        }

        return "grok.exe";
    }

    private sealed record GrokAuth(
        string AccessToken,
        string? UserId,
        string? RefreshToken,
        string? ClientId,
        DateTimeOffset? ExpiresAt,
        string? ScopeKey);

    private readonly record struct FetchResult(UsageSnapshot? Snapshot, bool AuthFailed, string? Error)
    {
        public static FetchResult Ok(UsageSnapshot snap) => new(snap, false, null);
        public static FetchResult Auth() => new(null, true, null);
        public static FetchResult Failed(string error) => new(null, false, error);
    }
}
