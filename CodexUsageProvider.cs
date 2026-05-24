using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace JeekTokenPlanUsage;

/// Reads Codex usage from the official ChatGPT backend usage endpoint.
/// The endpoint sits behind Cloudflare bot protection that rejects .NET's
/// HttpClient (TLS fingerprint), but accepts the system curl.exe, so we shell
/// out to curl. The OAuth token is fed via curl's stdin config to keep it off
/// the process command line.
public sealed class CodexUsageProvider : IUsageProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/codex/usage";
    private const string StatusMarker = "HTTPSTATUS:";

    private static readonly string AuthPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    public static bool HasLocalCredentials() => File.Exists(AuthPath);

    private static readonly string CurlPath = ResolveCurl();

    public string Name => "Codex";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct)
    {
        if (CurlPath is "")
            return UsageSnapshot.FromError("未找到 curl.exe");

        (string? token, string? account, string? credError) = ReadAuth();
        if (token is null)
            return UsageSnapshot.FromError(credError ?? "无法读取 Codex 凭据");

        string config = BuildCurlConfig(token, account);

        (string stdout, string stderr, int exitCode) = await RunCurlAsync(config, ct);
        if (exitCode != 0)
            return UsageSnapshot.FromError($"curl 失败({exitCode}): {Trim(stderr)}");

        (string body, int status) = SplitStatus(stdout);
        if (status == 401)
            return UsageSnapshot.FromError("Token 失效 (401)，请重新登录 Codex");
        if (status == 429)
            return UsageSnapshot.FromError("接口限流 (429)");
        if (status is < 200 or >= 300)
            return UsageSnapshot.FromError($"HTTP {status}");

        return Parse(body);
    }

    private static string BuildCurlConfig(string token, string? account)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"url = \"{UsageUrl}\"");
        sb.AppendLine($"header = \"Authorization: Bearer {token}\"");
        if (!string.IsNullOrEmpty(account))
            sb.AppendLine($"header = \"chatgpt-account-id: {account}\"");
        sb.AppendLine("header = \"originator: codex_cli_rs\"");
        sb.AppendLine("user-agent = \"codex_cli_rs/0.121.0\"");
        sb.AppendLine("max-time = 20");
        sb.AppendLine("silent");
        sb.AppendLine("show-error");
        sb.AppendLine($"write-out = \"\\n{StatusMarker}%{{http_code}}\"");
        return sb.ToString();
    }

    private static async Task<(string stdout, string stderr, int exitCode)> RunCurlAsync(string config, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = CurlPath,
            Arguments = "--config -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return ("", ex.Message, -1);
        }

        await proc.StandardInput.WriteAsync(config);
        proc.StandardInput.Close();

        Task<string> outTask = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> errTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            throw;
        }

        return (await outTask, await errTask, proc.ExitCode);
    }

    private static (string body, int status) SplitStatus(string stdout)
    {
        int idx = stdout.LastIndexOf(StatusMarker, StringComparison.Ordinal);
        if (idx < 0)
            return (stdout, 0);

        string body = stdout[..idx];
        string statusText = stdout[(idx + StatusMarker.Length)..].Trim();
        return int.TryParse(statusText, out int status) ? (body, status) : (body, 0);
    }

    private static UsageSnapshot Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("rate_limit", out JsonElement rl) || rl.ValueKind != JsonValueKind.Object)
                return UsageSnapshot.FromError("响应缺少 rate_limit");

            return new UsageSnapshot
            {
                FiveHour = ParseWindow(rl, "primary_window"),
                Weekly = ParseWindow(rl, "secondary_window"),
            };
        }
        catch (JsonException ex)
        {
            return UsageSnapshot.FromError($"解析失败: {ex.Message}");
        }
    }

    private static UsageMetric? ParseWindow(JsonElement rateLimit, string property)
    {
        if (!rateLimit.TryGetProperty(property, out JsonElement el) || el.ValueKind != JsonValueKind.Object)
            return null;

        double used = el.TryGetProperty("used_percent", out JsonElement u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble()
            : 0;

        DateTimeOffset? reset = null;
        if (el.TryGetProperty("reset_at", out JsonElement r) && r.ValueKind == JsonValueKind.Number)
            reset = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64());

        return new UsageMetric(used, reset);
    }

    private static (string? token, string? account, string? error) ReadAuth()
    {
        if (!File.Exists(AuthPath))
            return (null, null, "未找到 ~/.codex/auth.json");

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(AuthPath));
            if (!doc.RootElement.TryGetProperty("tokens", out JsonElement tokens) ||
                !tokens.TryGetProperty("access_token", out JsonElement at) ||
                at.ValueKind != JsonValueKind.String)
            {
                return (null, null, "凭据缺少 access_token (请登录 Codex)");
            }

            string? account = tokens.TryGetProperty("account_id", out JsonElement acc) && acc.ValueKind == JsonValueKind.String
                ? acc.GetString()
                : null;

            return (at.GetString(), account, null);
        }
        catch (Exception ex)
        {
            return (null, null, $"读取凭据失败: {ex.Message}");
        }
    }

    private static string ResolveCurl()
    {
        string sys = Path.Combine(Environment.SystemDirectory, "curl.exe");
        return File.Exists(sys) ? sys : "curl.exe";
    }

    private static string Trim(string s) => s.Length <= 80 ? s.Trim() : s[..80].Trim();
}
