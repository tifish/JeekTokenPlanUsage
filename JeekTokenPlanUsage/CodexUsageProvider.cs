using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JeekTokenPlanUsage.Resources;

namespace JeekTokenPlanUsage;

/// Reads Codex usage from the ChatGPT backend usage endpoint.
/// The endpoint sits behind Cloudflare bot protection that rejects .NET's
/// HttpClient (TLS fingerprint), but accepts the system curl.exe, so we shell
/// out to curl. The OAuth token is fed via curl's stdin config to keep it off
/// the process command line.
public sealed class CodexUsageProvider : IUsageProvider
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string StatusMarker = "HTTPSTATUS:";
    private const int CliRefreshTimeoutSeconds = 30;

    public static bool HasLocalCredentials() => File.Exists(AuthPath());

    public static string CredentialWatchSignature() => AuthWatchSignature(AuthPath());

    private static readonly string CurlPath = ResolveCurl();

    public string Name => "Codex";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct)
    {
        if (CurlPath is "")
            return UsageSnapshot.FromError(Strings.Codex_CurlNotFound);

        CodexAuth? auth = ReadAuth(out string? credError);
        if (auth is null)
            return UsageSnapshot.FromError(credError ?? Strings.Codex_ReadCredFailed);

        FetchResult result = await FetchUsageAsync(auth, ct);
        if (result.AuthFailed)
        {
            Log.Warn("Codex usage auth failed; attempting CLI refresh");
            RefreshCodexToken();

            CodexAuth? refreshed = ReadAuth(out _);
            if (refreshed is null || string.Equals(refreshed.AccessToken, auth.AccessToken, StringComparison.Ordinal))
            {
                Log.Warn("Codex auth retry skipped: refreshed credential unavailable or unchanged");
                return UsageSnapshot.FromError(Strings.Codex_TokenInvalid, UsageErrorKind.Auth);
            }

            result = await FetchUsageAsync(refreshed, ct);
        }

        if (result.AuthFailed)
            return UsageSnapshot.FromError(Strings.Codex_TokenInvalid, UsageErrorKind.Auth);
        if (result.Snapshot is not null)
            return result.Snapshot;
        return UsageSnapshot.FromError(result.Error ?? Strings.Error_FetchUsage);
    }

    private static async Task<FetchResult> FetchUsageAsync(CodexAuth auth, CancellationToken ct)
    {
        string config = BuildCurlConfig(auth.AccessToken, auth.AccountId);
        (string stdout, string stderr, int exitCode) = await RunCurlAsync(config, ct);
        if (exitCode != 0)
        {
            Log.Warn($"Codex curl failed: exit {exitCode}; {Trim(stderr)}");
            return FetchResult.Failed(string.Format(Strings.Codex_CurlFailedFormat, exitCode, Trim(stderr)));
        }

        (string body, int status) = SplitStatus(stdout);
        if (status is 401 or 403)
        {
            Log.Warn($"Codex usage auth error HTTP {status}");
            return FetchResult.Auth();
        }
        if (status == 429)
        {
            Log.Warn("Codex usage HTTP 429");
            return FetchResult.Failed(Strings.Error_RateLimit429);
        }
        if (status is < 200 or >= 300)
        {
            Log.Warn($"Codex usage HTTP {status}");
            return FetchResult.Failed($"HTTP {status}");
        }

        return FetchResult.Ok(Parse(body));
    }

    private static string BuildCurlConfig(string token, string? account)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"url = \"{UsageUrl}\"");
        sb.AppendLine($"header = \"Authorization: Bearer {token}\"");
        if (!string.IsNullOrEmpty(account))
            sb.AppendLine($"header = \"ChatGPT-Account-Id: {account}\"");
        sb.AppendLine("user-agent = \"codex-cli\"");
        // Route curl through the same proxy the HttpClient paths use. curl does
        // not read the Windows system proxy on its own, so we pass the resolved
        // URI explicitly; null means a direct connection, and noproxy="*" also
        // neutralizes any inherited http_proxy/https_proxy environment vars.
        Uri? proxy = AppProxy.ResolveProxy(new Uri(UsageUrl));
        if (proxy is not null)
            sb.AppendLine($"proxy = \"{proxy.AbsoluteUri}\"");
        else
            sb.AppendLine("noproxy = \"*\"");
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
                return UsageSnapshot.FromError(Strings.Codex_ResponseMissingRateLimit);

            ParsedWindow? primary = ParseWindow(rl, "primary_window");
            ParsedWindow? secondary = ParseWindow(rl, "secondary_window");
            UsageMetric? fiveHour = null;
            UsageMetric? weekly = null;

            // primary_window used to always be the short window and
            // secondary_window the weekly window. The backend now puts a
            // plan's only weekly limit in primary_window, so use the explicit
            // duration when present and retain the old positional mapping as
            // a compatibility fallback for older responses.
            bool primaryClassified = AssignByDuration(primary, ref fiveHour, ref weekly);
            bool secondaryClassified = AssignByDuration(secondary, ref fiveHour, ref weekly);
            if (!primaryClassified && primary is not null)
            {
                if (fiveHour is null)
                    fiveHour = primary.Metric;
                else
                    weekly ??= primary.Metric;
            }
            if (!secondaryClassified && secondary is not null)
            {
                if (weekly is null)
                    weekly = secondary.Metric;
                else
                    fiveHour ??= secondary.Metric;
            }

            return new UsageSnapshot
            {
                FiveHour = fiveHour,
                Weekly = weekly,
            };
        }
        catch (JsonException ex)
        {
            return UsageSnapshot.FromError(string.Format(Strings.Error_ParseFormat, ex.Message));
        }
    }

    private static ParsedWindow? ParseWindow(JsonElement rateLimit, string property)
    {
        if (!rateLimit.TryGetProperty(property, out JsonElement el) || el.ValueKind != JsonValueKind.Object)
            return null;

        double used = el.TryGetProperty("used_percent", out JsonElement u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble()
            : 0;

        DateTimeOffset? reset = null;
        if (el.TryGetProperty("reset_at", out JsonElement r) && r.ValueKind == JsonValueKind.Number)
            reset = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64());
        else if (el.TryGetProperty("reset_after_seconds", out JsonElement ra) && ra.ValueKind == JsonValueKind.Number)
            reset = DateTimeOffset.UtcNow.AddSeconds(ra.GetInt64());

        long? windowSeconds = el.TryGetProperty("limit_window_seconds", out JsonElement ws)
            && ws.ValueKind == JsonValueKind.Number
            ? ws.GetInt64()
            : null;

        return new ParsedWindow(new UsageMetric(used, reset), windowSeconds);
    }

    private static bool AssignByDuration(
        ParsedWindow? window,
        ref UsageMetric? fiveHour,
        ref UsageMetric? weekly)
    {
        if (window?.LimitWindowSeconds is not > 0)
            return false;

        if (window.LimitWindowSeconds <= (long)TimeSpan.FromDays(1).TotalSeconds)
            fiveHour ??= window.Metric;
        else
            weekly ??= window.Metric;

        return true;
    }

    private static CodexAuth? ReadAuth(out string? error)
    {
        error = null;
        string authPath = AuthPath();
        if (!File.Exists(authPath))
        {
            error = Strings.Codex_AuthNotFound;
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(authPath));
            if (!doc.RootElement.TryGetProperty("tokens", out JsonElement tokens) ||
                !tokens.TryGetProperty("access_token", out JsonElement at) ||
                at.ValueKind != JsonValueKind.String)
            {
                error = Strings.Codex_AuthMissingToken;
                return null;
            }

            string? token = at.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                error = Strings.Codex_AuthMissingToken;
                return null;
            }

            string? account = tokens.TryGetProperty("account_id", out JsonElement acc) && acc.ValueKind == JsonValueKind.String
                ? acc.GetString()
                : null;

            return new CodexAuth(token, account);
        }
        catch (Exception ex)
        {
            Log.Error($"Codex auth read failed: {ex.Message}");
            error = string.Format(Strings.Codex_ReadAuthFailedFormat, ex.Message);
            return null;
        }
    }

    private static void RefreshCodexToken()
    {
        string codexPath = ResolveCodexPath();
        string lower = codexPath.ToLowerInvariant();
        string fileName;
        string[] args;

        if (lower.EndsWith(".cmd"))
        {
            fileName = "cmd.exe";
            args = new[] { "/c", $"\"{codexPath}\" exec ." };
        }
        else if (lower.EndsWith(".ps1"))
        {
            fileName = "powershell.exe";
            args = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", codexPath, "exec", "." };
        }
        else
        {
            fileName = codexPath;
            args = new[] { "exec", "." };
        }

        Log.Info($"Attempting Codex token refresh via {Path.GetFileName(codexPath)}");
        ProcessResult result = RunProcess(
            fileName,
            args,
            TimeSpan.FromSeconds(CliRefreshTimeoutSeconds),
            purpose: "Codex token refresh",
            captureOutput: false,
            logSuccess: true);
        if (!result.Succeeded)
            Log.Warn($"Codex token refresh failed: {ProcessSummary(result)}");
    }

    private static string ResolveCodexPath()
    {
        foreach (string name in new[] { "codex.cmd", "codex.ps1", "codex.exe", "codex" })
        {
            ProcessResult found = RunProcess(
                "where.exe",
                new[] { name },
                TimeSpan.FromSeconds(5),
                purpose: $"resolve {name}",
                captureOutput: true,
                logFailure: false);
            if (found.Succeeded)
            {
                string? first = found.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first))
                {
                    Log.Info($"Resolved Codex CLI path: {Path.GetFileName(first.Trim())}");
                    return first.Trim();
                }
            }
        }

        Log.Warn("Codex CLI path not found with where.exe; falling back to codex.cmd");
        return "codex.cmd";
    }

    private static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string purpose,
        bool captureOutput,
        bool logFailure = true,
        bool logSuccess = false)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                var result = new ProcessResult(false, Error: "Process.Start returned null");
                if (logFailure)
                    Log.Warn($"{purpose} failed: {ProcessSummary(result)}");
                return result;
            }

            process.StandardInput.Close();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                var result = new ProcessResult(false, TimedOut: true);
                if (logFailure)
                    Log.Warn($"{purpose} timed out after {timeout.TotalSeconds:0.#} seconds");
                return result;
            }

            string rawOutput = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            var completed = new ProcessResult(process.ExitCode == 0, captureOutput ? rawOutput : string.Empty, process.ExitCode);
            if (completed.Succeeded)
            {
                if (logSuccess)
                    Log.Info($"{purpose} succeeded");
            }
            else if (logFailure)
            {
                Log.Warn($"{purpose} failed: {ProcessSummary(completed)}");
            }

            return completed;
        }
        catch (Exception ex)
        {
            var result = new ProcessResult(false, Error: ex.Message);
            if (logFailure)
                Log.Warn($"{purpose} failed: {ProcessSummary(result)}");
            return result;
        }
    }

    private static string ProcessSummary(ProcessResult result)
    {
        if (result.TimedOut)
            return "timeout";
        if (result.ExitCode is int exitCode)
            return $"exit {exitCode}";
        return result.Error ?? "unknown";
    }

    private static string ResolveCurl()
    {
        string sys = Path.Combine(Environment.SystemDirectory, "curl.exe");
        return File.Exists(sys) ? sys : "curl.exe";
    }

    private static string Trim(string s) => s.Length <= 80 ? s.Trim() : s[..80].Trim();

    private static string AuthPath()
    {
        string? codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
            return Path.Combine(codexHome, "auth.json");

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
    }

    private static string AuthWatchSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return $"codex:{path}|missing";

            return $"codex:{path}|present|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"codex:{path}|unavailable";
        }
    }

    private sealed record CodexAuth(string AccessToken, string? AccountId);
    private sealed record ParsedWindow(UsageMetric Metric, long? LimitWindowSeconds);
    private readonly record struct FetchResult(UsageSnapshot? Snapshot, bool AuthFailed, string? Error)
    {
        public static FetchResult Ok(UsageSnapshot snap) => new(snap, false, null);
        public static FetchResult Auth() => new(null, true, null);
        public static FetchResult Failed(string error) => new(null, false, error);
    }

    private readonly record struct ProcessResult(
        bool Succeeded,
        string Output = "",
        int? ExitCode = null,
        bool TimedOut = false,
        string? Error = null);
}
