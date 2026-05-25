using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JeekTokenPlanUsage.Resources;
using Microsoft.Data.Sqlite;

namespace JeekTokenPlanUsage;

/// Reads Cursor usage from the dashboard "current period" endpoint.
/// The endpoint exposes two pools (Auto + Composer / API) used by Cursor 2.0
/// pricing. Authentication uses the WorkosCursorSessionToken cookie value,
/// which is reconstructed from the JWT stored by the Cursor IDE in its
/// state.vscdb SQLite database under key `cursorAuth/accessToken`.
public sealed class CursorUsageProvider : IUsageProvider
{
    private const string UsageUrl = "https://cursor.com/api/dashboard/get-current-period-usage";
    // Used only to read `startOfMonth` (subscription anchor day), from which we
    // compute the next billing-cycle reset — get-current-period-usage doesn't
    // expose period boundaries.
    private const string LegacyUsageUrl = "https://cursor.com/api/usage";
    private const string DashboardReferer = "https://cursor.com/dashboard?tab=spending";

    private static readonly string StateDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cursor", "User", "globalStorage", "state.vscdb");

    public static bool HasLocalCredentials() => File.Exists(StateDbPath);

    private readonly HttpClient _http;

    public CursorUsageProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public string Name => "Cursor";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct)
    {
        (string? sessionToken, string? userId, string? credError) = BuildSessionToken();
        if (sessionToken is null || userId is null)
            return UsageSnapshot.FromError(credError ?? Strings.Cursor_ReadCredFailed);

        // Fire both calls in parallel: the dashboard endpoint for utilization,
        // the legacy endpoint just to read `startOfMonth` for reset computation.
        Task<(string? body, string? error)> usageTask = FetchUsageAsync(sessionToken, ct);
        Task<DateTimeOffset?> resetTask = FetchNextResetAsync(sessionToken, userId, ct);

        (string? body, string? error) = await usageTask;
        if (error is not null) return UsageSnapshot.FromError(error);

        DateTimeOffset? reset = await resetTask;
        return Parse(body!, reset);
    }

    private async Task<(string? body, string? error)> FetchUsageAsync(string sessionToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, UsageUrl)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={sessionToken}");
        req.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
        req.Headers.Referrer = new Uri(DashboardReferer);
        req.Headers.Accept.ParseAdd("*/*");

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, string.Format(Strings.Error_NetworkFormat, ex.Message));
        }

        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (null, Strings.Cursor_TokenInvalid);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                return (null, Strings.Error_RateLimit429);
            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode}");

            return (await resp.Content.ReadAsStringAsync(ct), null);
        }
    }

    // Best-effort: returns null on any failure so a missing reset never blocks the
    // main usage display.
    private async Task<DateTimeOffset?> FetchNextResetAsync(string sessionToken, string userId, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{LegacyUsageUrl}?user={Uri.EscapeDataString(userId)}");
            req.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={sessionToken}");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            string body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("startOfMonth", out JsonElement som)
                || som.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(som.GetString(), out DateTimeOffset start))
                return null;

            // The subscription anchor stays fixed; the *next* reset is the first
            // monthly anniversary strictly after now.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset next = start;
            while (next <= now) next = next.AddMonths(1);
            return next;
        }
        catch
        {
            return null;
        }
    }

    private static UsageSnapshot Parse(string body, DateTimeOffset? reset)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("planUsage", out JsonElement plan) || plan.ValueKind != JsonValueKind.Object)
                return UsageSnapshot.FromError(Strings.Cursor_ResponseMissingPlan);

            return new UsageSnapshot
            {
                // Slot 1 = Auto + Composer pool; Slot 2 = API pool. Both share the
                // monthly billing-cycle reset, so we put the same reset on both.
                FiveHour = ReadPercent(plan, "autoPercentUsed", reset),
                Weekly = ReadPercent(plan, "apiPercentUsed", reset),
            };
        }
        catch (JsonException ex)
        {
            return UsageSnapshot.FromError(string.Format(Strings.Error_ParseFormat, ex.Message));
        }
    }

    private static UsageMetric? ReadPercent(JsonElement plan, string property, DateTimeOffset? reset)
    {
        if (!plan.TryGetProperty(property, out JsonElement el) || el.ValueKind != JsonValueKind.Number)
            return null;
        return new UsageMetric(el.GetDouble(), reset);
    }

    /// Builds the WorkosCursorSessionToken cookie value: `{userId}%3A%3A{jwt}`.
    /// The user ID is extracted from the JWT's `sub` claim (format `auth0|USERID`),
    /// and also returned separately so callers can use it as a query parameter.
    private static (string? sessionToken, string? userId, string? error) BuildSessionToken()
    {
        (string? jwt, string? error) = ReadAccessTokenJwt();
        if (jwt is null) return (null, null, error);

        if (!TryExtractUserId(jwt, out string? userId, out string? jwtError))
            return (null, null, jwtError);

        return ($"{userId}%3A%3A{jwt}", userId, null);
    }

    private static (string? jwt, string? error) ReadAccessTokenJwt()
    {
        if (!File.Exists(StateDbPath))
            return (null, Strings.Cursor_DbNotFound);

        // Open read-only with shared cache so an open Cursor instance doesn't block us.
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = StateDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        try
        {
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken'";
            object? raw = cmd.ExecuteScalar();
            string? token = raw as string;
            return string.IsNullOrEmpty(token)
                ? (null, Strings.Cursor_CredEmpty)
                : (token, null);
        }
        catch (Exception ex)
        {
            return (null, string.Format(Strings.Cursor_ReadCredFailedFormat, ex.Message));
        }
    }

    private static bool TryExtractUserId(string jwt, out string? userId, out string? error)
    {
        userId = null;
        error = null;
        string[] parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            error = Strings.Jwt_Invalid;
            return false;
        }

        try
        {
            byte[] payloadBytes = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payloadBytes);
            if (!doc.RootElement.TryGetProperty("sub", out JsonElement sub) || sub.ValueKind != JsonValueKind.String)
            {
                error = Strings.Jwt_MissingSub;
                return false;
            }
            string? subValue = sub.GetString();
            if (string.IsNullOrEmpty(subValue))
            {
                error = Strings.Jwt_EmptySub;
                return false;
            }
            // sub format is e.g. "auth0|USERID" — take the segment after the bar.
            int bar = subValue.IndexOf('|');
            userId = bar >= 0 ? subValue[(bar + 1)..] : subValue;
            return !string.IsNullOrEmpty(userId);
        }
        catch (Exception ex)
        {
            error = string.Format(Strings.Jwt_ParseFailedFormat, ex.Message);
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        int rem = padded.Length % 4;
        if (rem > 0) padded += new string('=', 4 - rem);
        return Convert.FromBase64String(padded);
    }
}
