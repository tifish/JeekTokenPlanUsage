using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    private const int CliRefreshTimeoutSeconds = 30;
    private const int WslProbeTimeoutSeconds = 5;

    // Tried in order — the first model whose response carries rate-limit headers wins.
    // Datless aliases auto-track the latest patch within a model family; only a new
    // major Haiku release (e.g. 5.0) requires editing this list.
    private static readonly string[] ModelFallbackChain =
    {
        "claude-haiku-4-5",
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

    // Claude Desktop stores its OAuth token (encrypted with Chromium OSCrypt) in
    // config.json, with the AES key kept DPAPI-protected in the sibling "Local State".
    private static readonly string DesktopConfigPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "config.json");

    public static bool HasLocalCredentials() =>
        File.Exists(CredentialsPath) || File.Exists(DesktopConfigPath) || ListWslDistros().Any(ReadWslCredentialsExists);

    public static string CredentialWatchSignature()
    {
        var parts = new List<string>
        {
            WindowsCredentialWatchSignature(CredentialsPath),
            DesktopCredentialWatchSignature(DesktopConfigPath),
        };
        foreach (string distro in ListWslDistros())
            parts.Add(WslCredentialWatchSignature(distro));

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    private readonly HttpClient _http;

    private DateTimeOffset _oauthCooldownUntil = DateTimeOffset.MinValue;
    private int _oauthRateLimitStreak;

    public ClaudeUsageProvider()
    {
        _http = new HttpClient(AppProxy.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("JeekTokenPlanUsage/1.0");
    }

    public string Name => "Claude";

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken ct)
    {
        CredentialResolution resolved = await Task.Run(
            () => ResolveCredential(refreshExpired: true, skipRefreshFor: null, rejected: null),
            ct);
        if (resolved.Credential is null)
        {
            DiagnosticLog.Warn($"Claude credential resolution failed: {resolved.Error ?? "unknown"}");
            return UsageSnapshot.FromError(resolved.Error ?? Strings.Claude_ReadCredFailed, UsageErrorKind.Auth);
        }

        DiagnosticLog.Info($"Claude resolved credential from {SourceLabel(resolved.Credential.Source)}");
        ProviderResult result = await FetchUsageWithFallbackAsync(resolved.Credential.AccessToken, ct);
        if (result.AuthFailed)
        {
            CredentialSource source = resolved.Credential.Source;
            DiagnosticLog.Warn($"Claude auth failed with {SourceLabel(source)}; attempting CLI refresh");
            CredentialResolution retryCred = await Task.Run(
                () =>
                {
                    RefreshToken(source);
                    return ResolveCredential(refreshExpired: true, skipRefreshFor: source, rejected: resolved.Credential);
                },
                ct);
            if (retryCred.Credential is null)
            {
                DiagnosticLog.Warn("Claude auth retry skipped: refreshed credential unavailable or unchanged");
                return UsageSnapshot.FromError(retryCred.Error ?? Strings.Claude_TokenInvalid, UsageErrorKind.Auth);
            }

            DiagnosticLog.Info($"Claude resolved credential from {SourceLabel(retryCred.Credential.Source)} after refresh");
            result = await FetchUsageWithFallbackAsync(retryCred.Credential.AccessToken, ct);
        }

        return ResultToSnapshot(result);
    }

    private async Task<ProviderResult> FetchUsageWithFallbackAsync(string token, CancellationToken ct)
    {
        // If OAuth was recently rate-limited, skip it for this poll and go
        // straight to the messages fallback — calling cadence is unchanged.
        bool skipPrimary = DateTimeOffset.UtcNow < _oauthCooldownUntil;
        ProviderResult primary = skipPrimary
            ? ProviderResult.Skipped()
            : await TryUsageEndpointAsync(token, ct);

        if (primary.AuthFailed)
            return primary;

        if (primary.RateLimited)
        {
            // Prefer the server hint when present, otherwise use the local ladder.
            TimeSpan cd = primary.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                ? retryAfter
                : OauthCooldownLadder[Math.Min(_oauthRateLimitStreak, OauthCooldownLadder.Length - 1)];
            cd = cd <= OauthCooldownLadder[^1] ? cd : OauthCooldownLadder[^1];
            _oauthCooldownUntil = DateTimeOffset.UtcNow + cd;
            _oauthRateLimitStreak++;
            DiagnosticLog.Warn($"Claude oauth/usage rate limited; cooldown {cd.TotalMinutes:0.#} minutes");
        }
        else if (primary.Snapshot is not null)
        {
            // Success decays the streak by one rung instead of resetting outright.
            // Under alternating 429/success this keeps the ladder elevated so the
            // next cooldown widens, letting probe cadence converge on a value the
            // endpoint actually tolerates — and WARN volume drops with it.
            if (_oauthRateLimitStreak > 0)
                _oauthRateLimitStreak--;
            _oauthCooldownUntil = DateTimeOffset.MinValue;
        }

        // Primary fully succeeded (with reset times) — no need to spend a messages call.
        if (primary.Snapshot is { } ok
            && ok.FiveHour?.ResetsAt is not null
            && ok.Weekly?.ResetsAt is not null)
            return ProviderResult.Ok(ok);

        // Otherwise fall back to Messages API: as the data source if primary failed
        // or was skipped, or to fill in missing reset times if primary returned partial data.
        ProviderResult fallback = await TryMessagesFallbackAsync(token, ct);
        if (fallback.AuthFailed)
            return fallback;

        if (primary.Snapshot is not null && fallback.Snapshot is not null)
            return ProviderResult.Ok(Merge(primary.Snapshot, fallback.Snapshot));

        if (primary.Snapshot is not null)
            return primary;
        if (fallback.Snapshot is not null)
            return fallback;

        return ProviderResult.Failed(primary.Error ?? fallback.Error ?? Strings.Error_FetchUsage);
    }

    private static UsageSnapshot ResultToSnapshot(ProviderResult result)
    {
        if (result.AuthFailed)
            return UsageSnapshot.FromError(Strings.Claude_TokenInvalid, UsageErrorKind.Auth);
        if (result.Snapshot is not null)
            return result.Snapshot;
        return UsageSnapshot.FromError(result.Error ?? Strings.Error_FetchUsage);
    }

    private static CredentialResolution ResolveCredential(
        bool refreshExpired,
        CredentialSource? skipRefreshFor,
        ClaudeCredential? rejected)
    {
        // Snapshot the sources once (Windows, Desktop, then any running WSL) so
        // both passes and the failure message see the same set without re-running
        // WSL enumeration. Empty means nothing is installed/logged in anywhere.
        List<CredentialSource> sources = GetCredentialSources().ToList();
        if (sources.Count == 0)
        {
            DiagnosticLog.Warn("Claude credential resolution found no Windows credentials or running WSL sources");
            return new(null, Strings.Claude_CredNotFound);
        }

        string? lastError = null;

        // Pass 1: take any immediately-usable credential before spending time on a
        // blocking CLI refresh. A valid Claude Desktop token must win instantly
        // even when an expired Claude Code credential sits ahead of it — otherwise
        // every poll wastes ~30s on a doomed `claude -p .` for the expired source.
        foreach (CredentialSource source in sources)
        {
            ClaudeCredential? credential = ReadCredential(source, out string? error);
            if (credential is null)
            {
                DiagnosticLog.Warn($"Claude credential read failed from {SourceLabel(source)}: {error ?? "unknown"}");
                lastError = error ?? lastError;
                continue;
            }

            if (IsRejected(credential, rejected))
            {
                DiagnosticLog.Warn($"Claude credential from {SourceLabel(source)} unchanged after refresh; skipping");
                lastError = Strings.Claude_TokenInvalid;
                continue;
            }

            if (!IsExpired(credential))
                return new(credential, null);

            DiagnosticLog.Info($"Claude credential from {SourceLabel(source)} is expired");
            lastError = Strings.Claude_TokenInvalid;
        }

        // Pass 2: nothing usable as-is. Refresh the sources that have a CLI to
        // drive (Windows / WSL); Claude Desktop rotates its own token, so there is
        // nothing to refresh for it here.
        if (refreshExpired)
        {
            foreach (CredentialSource source in sources)
            {
                if (source.Equals(skipRefreshFor) || !CanRefresh(source))
                    continue;

                RefreshToken(source);
                ClaudeCredential? credential = ReadCredential(source, out string? error);
                if (credential is not null && !IsExpired(credential) && !IsRejected(credential, rejected))
                {
                    DiagnosticLog.Info($"Claude credential refresh succeeded for {SourceLabel(source)}");
                    return new(credential, null);
                }

                DiagnosticLog.Warn($"Claude credential refresh did not produce usable credentials for {SourceLabel(source)}");
                lastError = error ?? lastError;
            }
        }

        return new(null, BuildAuthError(sources, lastError));
    }

    private static bool CanRefresh(CredentialSource source) =>
        source is WindowsCredentialSource or WslCredentialSource;

    // Build the user-facing auth failure. A specific read/parse error is more
    // useful than a generic "token invalid", so surface it as-is. Otherwise tell
    // the user which apps to re-login in — naming only the sources actually
    // present, so a Desktop-only user is not told to re-login in Claude Code.
    private static string BuildAuthError(IReadOnlyList<CredentialSource> sources, string? lastError)
    {
        if (lastError is not null
            && !string.Equals(lastError, Strings.Claude_TokenInvalid, StringComparison.Ordinal))
            return lastError;

        var apps = new List<string>();
        foreach (CredentialSource source in sources)
        {
            string? app = source switch
            {
                WindowsCredentialSource => "Claude Code",
                DesktopCredentialSource => "Claude Desktop",
                WslCredentialSource => "Claude Code (WSL)",
                _ => null,
            };
            if (app is not null && !apps.Contains(app))
                apps.Add(app);
        }

        return apps.Count == 0
            ? Strings.Claude_TokenInvalid
            : string.Format(Strings.Claude_TokenInvalidFormat, string.Join(" / ", apps));
    }

    private static bool IsRejected(ClaudeCredential credential, ClaudeCredential? rejected) =>
        rejected is not null
        && credential.Source.Equals(rejected.Source)
        && string.Equals(credential.AccessToken, rejected.AccessToken, StringComparison.Ordinal);

    private static string SourceLabel(CredentialSource source) => source switch
    {
        WindowsCredentialSource => "Windows",
        DesktopCredentialSource => "Desktop",
        WslCredentialSource wsl => $"WSL:{wsl.Distro}",
        _ => "unknown",
    };

    private static IEnumerable<CredentialSource> GetCredentialSources()
    {
        if (File.Exists(CredentialsPath))
            yield return new WindowsCredentialSource(CredentialsPath);

        if (File.Exists(DesktopConfigPath))
            yield return new DesktopCredentialSource(DesktopConfigPath);

        foreach (string distro in ListWslDistros())
            yield return new WslCredentialSource(distro);
    }

    private static ClaudeCredential? ReadCredential(CredentialSource source, out string? error)
    {
        error = null;
        // Desktop stores an encrypted token in a different shape, so it parses separately.
        if (source is DesktopCredentialSource desktop)
        {
            string? plain = ReadDesktopCredentialJson(desktop.ConfigPath, out error);
            return plain is null ? null : ParseDesktopCredential(plain, source, out error);
        }

        string? json = source switch
        {
            WindowsCredentialSource windows => ReadWindowsCredentialJson(windows.Path, out error),
            WslCredentialSource wsl => ReadWslCredentialJson(wsl.Distro, out error),
            _ => null,
        };

        return json is null ? null : ParseCredential(json, source, out error);
    }

    private static string? ReadWindowsCredentialJson(string path, out string? error)
    {
        error = null;
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error($"Claude Windows credential read failed: {ex.Message}");
            error = string.Format(Strings.Claude_ReadCredFailedFormat, ex.Message);
            return null;
        }
    }

    private static string? ReadWslCredentialJson(string distro, out string? error)
    {
        error = null;
        ProcessResult result = RunProcess(
            "wsl.exe",
            new[] { "-d", distro, "--", "sh", "-lc", "cat ~/.claude/.credentials.json" },
            TimeSpan.FromSeconds(WslProbeTimeoutSeconds),
            captureOutput: true,
            purpose: $"read WSL Claude credentials ({distro})");

        if (!result.Succeeded)
        {
            DiagnosticLog.Warn($"Claude WSL credential read failed for {distro}: {ProcessSummary(result)}");
            error = Strings.Claude_CredNotFound;
            return null;
        }

        return result.Output;
    }

    private static ClaudeCredential? ParseCredential(string json, CredentialSource source, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauth)
                || !oauth.TryGetProperty("accessToken", out JsonElement tok)
                || tok.ValueKind != JsonValueKind.String)
            {
                DiagnosticLog.Warn($"Claude credential from {SourceLabel(source)} is missing accessToken");
                error = Strings.Claude_CredMissingToken;
                return null;
            }

            string? token = tok.GetString();
            if (string.IsNullOrWhiteSpace(token))
            {
                DiagnosticLog.Warn($"Claude credential from {SourceLabel(source)} has an empty accessToken");
                error = Strings.Claude_CredMissingToken;
                return null;
            }

            long? expiresAt = null;
            if (oauth.TryGetProperty("expiresAt", out JsonElement exp))
                expiresAt = ReadInt64(exp);

            return new ClaudeCredential(token, expiresAt, source);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            DiagnosticLog.Error($"Claude credential parse failed from {SourceLabel(source)}: {ex.Message}");
            error = string.Format(Strings.Claude_ReadCredFailedFormat, ex.Message);
            return null;
        }
    }

    private static long? ReadInt64(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), out long parsed))
            return parsed;
        return null;
    }

    // Reads config.json, pulls oauth:tokenCache, and decrypts it with the OSCrypt key.
    private static string? ReadDesktopCredentialJson(string configPath, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!doc.RootElement.TryGetProperty("oauth:tokenCache", out JsonElement tc)
                || tc.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tc.GetString()))
            {
                DiagnosticLog.Warn("Claude Desktop config has no oauth:tokenCache");
                error = Strings.Claude_CredNotFound;
                return null;
            }

            byte[]? key = LoadOsCryptKey(configPath, out error);
            if (key is null)
                return null;

            return DecryptOsCrypt(tc.GetString()!, key, out error);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error($"Claude Desktop credential read failed: {ex.Message}");
            error = string.Format(Strings.Claude_ReadCredFailedFormat, ex.Message);
            return null;
        }
    }

    // Loads the AES-256 key from the sibling "Local State": base64 -> strip "DPAPI" -> DPAPI unprotect.
    private static byte[]? LoadOsCryptKey(string configPath, out string? error)
    {
        error = null;
        string localStatePath = Path.Combine(Path.GetDirectoryName(configPath)!, "Local State");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(localStatePath));
            if (!doc.RootElement.TryGetProperty("os_crypt", out JsonElement osc)
                || !osc.TryGetProperty("encrypted_key", out JsonElement ek)
                || ek.ValueKind != JsonValueKind.String)
            {
                DiagnosticLog.Warn("Claude Desktop Local State missing os_crypt.encrypted_key");
                error = Strings.Claude_CredNotFound;
                return null;
            }

            byte[] raw = Convert.FromBase64String(ek.GetString()!);
            ReadOnlySpan<byte> dpapiPrefix = "DPAPI"u8;
            byte[] protectedKey = raw.AsSpan().StartsWith(dpapiPrefix) ? raw[dpapiPrefix.Length..] : raw;
            return ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error($"Claude Desktop OSCrypt key load failed: {ex.Message}");
            error = string.Format(Strings.Claude_ReadCredFailedFormat, ex.Message);
            return null;
        }
    }

    // Chromium OSCrypt v10/v11 layout: 3-byte version prefix + 12-byte nonce + ciphertext + 16-byte GCM tag.
    private static string? DecryptOsCrypt(string base64, byte[] key, out string? error)
    {
        error = null;
        try
        {
            byte[] data = Convert.FromBase64String(base64);
            const int prefixLen = 3, nonceLen = 12, tagLen = 16;
            if (data.Length < prefixLen + nonceLen + tagLen)
            {
                error = Strings.Claude_CredMissingToken;
                return null;
            }

            ReadOnlySpan<byte> span = data;
            ReadOnlySpan<byte> nonce = span.Slice(prefixLen, nonceLen);
            ReadOnlySpan<byte> tag = span[^tagLen..];
            ReadOnlySpan<byte> cipher = span[(prefixLen + nonceLen)..^tagLen];

            byte[] plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, tagLen);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error($"Claude Desktop OSCrypt decrypt failed: {ex.Message}");
            error = string.Format(Strings.Claude_ReadCredFailedFormat, ex.Message);
            return null;
        }
    }

    // Desktop's tokenCache maps "accountId:orgId:audience:scopes" -> { token, refreshToken, expiresAt, ... }.
    // Prefer the Claude Code-scoped entry (the OAuth usage endpoint is Claude Code's); within a tier the
    // latest expiry (most recently refreshed) wins.
    private static ClaudeCredential? ParseDesktopCredential(string json, CredentialSource source, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = Strings.Claude_CredMissingToken;
                return null;
            }

            JsonElement best = default;
            bool found = false;
            long bestExpiry = long.MinValue;
            bool bestIsClaudeCode = false;

            foreach (JsonProperty entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || !entry.Value.TryGetProperty("token", out JsonElement tok)
                    || tok.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(tok.GetString()))
                    continue;

                bool isClaudeCode = entry.Name.Contains("claude_code", StringComparison.OrdinalIgnoreCase);
                long expiry = entry.Value.TryGetProperty("expiresAt", out JsonElement e) ? ReadInt64(e) ?? 0 : 0;

                bool better = !found
                    || (isClaudeCode && !bestIsClaudeCode)
                    || (isClaudeCode == bestIsClaudeCode && expiry > bestExpiry);
                if (better)
                {
                    best = entry.Value;
                    bestExpiry = expiry;
                    bestIsClaudeCode = isClaudeCode;
                    found = true;
                }
            }

            if (!found)
            {
                DiagnosticLog.Warn("Claude Desktop tokenCache has no usable token entry");
                error = Strings.Claude_CredMissingToken;
                return null;
            }

            string token = best.GetProperty("token").GetString()!;
            long? expiresAt = best.TryGetProperty("expiresAt", out JsonElement exp) ? ReadInt64(exp) : null;
            // The oauth/usage and messages endpoints are Claude Code-scoped; a token
            // from a non-code Desktop entry may be rejected (401) even when valid.
            // Logging the chosen scope makes such a rejection attributable.
            DiagnosticLog.Info($"Claude Desktop selected token scope: {(bestIsClaudeCode ? "claude_code" : "non-code")}");
            return new ClaudeCredential(token, expiresAt, source);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException)
        {
            DiagnosticLog.Error($"Claude Desktop credential parse failed: {ex.Message}");
            error = string.Format(Strings.Claude_ReadCredFailedFormat, ex.Message);
            return null;
        }
    }

    private static bool IsExpired(ClaudeCredential credential)
    {
        if (credential.ExpiresAtUnixMs is null)
            return false;

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return nowMs >= credential.ExpiresAtUnixMs.Value;
    }

    private static void RefreshToken(CredentialSource source)
    {
        switch (source)
        {
            case WindowsCredentialSource:
                RefreshWindowsToken();
                break;
            case WslCredentialSource wsl:
                RefreshWslToken(wsl.Distro);
                break;
            case DesktopCredentialSource:
                // No CLI to drive; Claude Desktop refreshes its own token while running.
                DiagnosticLog.Info("Claude Desktop has no refresh path; relying on the app's own token rotation");
                break;
        }
    }

    private static void RefreshWindowsToken()
    {
        string claudePath = ResolveWindowsClaudePath();
        string[] args = claudePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new[] { "/c", $"\"{claudePath}\" -p ." }
            : new[] { "-p", "." };
        string fileName = claudePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? "cmd.exe"
            : claudePath;

        DiagnosticLog.Info($"Attempting Claude Windows token refresh via {Path.GetFileName(claudePath)}");
        ProcessResult result = RunProcess(
            fileName,
            args,
            TimeSpan.FromSeconds(CliRefreshTimeoutSeconds),
            captureOutput: false,
            removeClaudeEnv: true,
            purpose: "Claude Windows token refresh",
            logSuccess: true);
        if (!result.Succeeded)
            DiagnosticLog.Warn($"Claude Windows token refresh failed: {ProcessSummary(result)}");
    }

    private static void RefreshWslToken(string distro)
    {
        const string command =
            "if command -v claude >/dev/null 2>&1; then claude -p .; " +
            "elif [ -x \"$HOME/.local/bin/claude\" ]; then \"$HOME/.local/bin/claude\" -p .; " +
            "else exit 127; fi";

        DiagnosticLog.Info($"Attempting Claude WSL token refresh in {distro}");
        ProcessResult result = RunProcess(
            "wsl.exe",
            new[] { "-d", distro, "--", "bash", "-lic", command },
            TimeSpan.FromSeconds(CliRefreshTimeoutSeconds),
            captureOutput: false,
            removeClaudeEnv: true,
            purpose: $"Claude WSL token refresh ({distro})",
            logSuccess: true);
        if (!result.Succeeded)
            DiagnosticLog.Warn($"Claude WSL token refresh failed for {distro}: {ProcessSummary(result)}");
    }

    private static string ResolveWindowsClaudePath()
    {
        foreach (string name in new[] { "claude.cmd", "claude.exe", "claude" })
        {
            ProcessResult found = RunProcess(
                "where.exe",
                new[] { name },
                TimeSpan.FromSeconds(WslProbeTimeoutSeconds),
                captureOutput: true,
                purpose: $"resolve {name}",
                logFailure: false);
            if (found.Succeeded)
            {
                string? first = found.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first))
                {
                    DiagnosticLog.Info($"Resolved Claude CLI path for Windows refresh: {Path.GetFileName(first.Trim())}");
                    return first.Trim();
                }
            }
        }

        DiagnosticLog.Warn("Claude CLI path not found with where.exe; falling back to claude.cmd");
        return "claude.cmd";
    }

    private static bool _loggedWslListFailure;

    private static IReadOnlyList<string> ListWslDistros()
    {
        ProcessBytesResult result = RunProcessBytes(
            "wsl.exe",
            new[] { "-l", "-q", "--running" },
            TimeSpan.FromSeconds(WslProbeTimeoutSeconds),
            purpose: "list running WSL distros",
            logFailure: false);
        if (!result.Succeeded || result.Output.Length == 0)
        {
            if (!result.Succeeded && !_loggedWslListFailure)
            {
                _loggedWslListFailure = true;
                DiagnosticLog.Warn($"Unable to list running WSL distros: {ProcessSummary(result)}");
            }
            return Array.Empty<string>();
        }

        string output = DecodeWslText(result.Output);
        return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim().TrimEnd('\0'))
            .Where(static line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool ReadWslCredentialsExists(string distro)
    {
        return ReadWslCredentialJson(distro, out _) is not null;
    }

    private static string WindowsCredentialWatchSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return $"win:{path}|missing";

            return $"win:{path}|present|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"win:{path}|unavailable";
        }
    }

    private static string DesktopCredentialWatchSignature(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return $"desktop:{path}|missing";

            return $"desktop:{path}|present|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return $"desktop:{path}|unavailable";
        }
    }

    private static string WslCredentialWatchSignature(string distro)
    {
        ProcessResult result = RunProcess(
            "wsl.exe",
            new[]
            {
                "-d",
                distro,
                "--",
                "sh",
                "-lc",
                "if [ -f ~/.claude/.credentials.json ]; then stat -c 'present|%s|%Y' ~/.claude/.credentials.json; else echo missing; fi",
            },
            TimeSpan.FromSeconds(WslProbeTimeoutSeconds),
            captureOutput: true,
            purpose: $"WSL credential watch ({distro})");

        string state = result.Succeeded ? result.Output.Trim() : "unavailable";
        return $"wsl:{distro}|{state}";
    }

    private static string DecodeWslText(byte[] bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        if (TryDecodeUtf16Le(bytes, out string? decoded))
            return decoded;

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool TryDecodeUtf16Le(byte[] bytes, out string decoded)
    {
        decoded = string.Empty;
        if (bytes.Length < 2)
            return false;

        ReadOnlySpan<byte> body = bytes;
        if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            body = bytes.AsSpan(2);
        else if (!LooksLikeUtf16Le(bytes))
            return false;

        if (body.Length % 2 != 0)
            body = body[..^1];

        decoded = Encoding.Unicode.GetString(body);
        return true;
    }

    private static bool LooksLikeUtf16Le(byte[] bytes)
    {
        int sampleLen = Math.Min(bytes.Length, 128);
        int pairs = sampleLen / 2;
        if (pairs == 0)
            return false;

        int zeroHighBytes = 0;
        for (int i = 1; i < pairs * 2; i += 2)
        {
            if (bytes[i] == 0)
                zeroHighBytes++;
        }

        return zeroHighBytes * 2 >= pairs;
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
            {
                DiagnosticLog.Warn($"Claude oauth/usage auth error HTTP {(int)resp.StatusCode}");
                return ProviderResult.Auth();
            }

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = ReadRetryAfter(resp);
                string suffix = retryAfter is null ? string.Empty : $" retry-after {retryAfter.Value.TotalSeconds:0.#}s";
                DiagnosticLog.Warn($"Claude oauth/usage HTTP 429{suffix}");
                return ProviderResult.RateLimit("oauth/usage HTTP 429", retryAfter);
            }

            if (!resp.IsSuccessStatusCode)
            {
                DiagnosticLog.Warn($"Claude oauth/usage HTTP {(int)resp.StatusCode}");
                return ProviderResult.Failed($"oauth/usage HTTP {(int)resp.StatusCode}");
            }

            // Body read kept inside the same try: a socket abort during the
            // body read (e.g. app shutdown, network reset) raises SocketException
            // 995 here, not from SendAsync.
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // OperationCanceledException without a canceled token = HttpClient timeout,
            // which we want to treat as a transient network failure, not a crash.
            DiagnosticLog.Warn($"Claude oauth/usage network error: {ex.Message}");
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
            DiagnosticLog.Error($"Claude oauth/usage parse failed: {ex.Message}");
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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // OperationCanceledException without a canceled token = HttpClient timeout.
                DiagnosticLog.Warn($"Claude messages fallback network error for {model}: {ex.Message}");
                lastError = string.Format(Strings.Error_NetworkFormat, ex.Message);
                continue;
            }

            using (resp)
            {
                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    DiagnosticLog.Warn($"Claude messages fallback auth error for {model}: HTTP {(int)resp.StatusCode}");
                    return ProviderResult.Auth();
                }

                // Rate-limit headers are present on error responses too (including 429),
                // so we attempt to read them regardless of status code.
                UsageSnapshot? fromHeaders = ReadRateLimitHeaders(resp);
                if (fromHeaders is not null)
                    return ProviderResult.Ok(fromHeaders);

                DiagnosticLog.Warn($"Claude messages fallback no rate-limit headers for {model}: HTTP {(int)resp.StatusCode}");
                lastError = string.Format(Strings.Claude_MessagesNoHeaderFormat, model, (int)resp.StatusCode);
            }
        }

        DiagnosticLog.Warn("Claude messages fallback failed for all models");
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

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter is not { } retryAfter)
            return null;

        TimeSpan delay = retryAfter.Delta
            ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);

        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
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

    private static ProcessResult RunProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        bool captureOutput,
        bool removeClaudeEnv = false,
        string purpose = "process",
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
                RedirectStandardOutput = captureOutput,
                RedirectStandardError = true,
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            if (removeClaudeEnv)
            {
                startInfo.Environment.Remove("CLAUDECODE");
                startInfo.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                var result = new ProcessResult(false, string.Empty, Error: "Process.Start returned null");
                if (logFailure)
                    DiagnosticLog.Warn($"{purpose} failed: {ProcessSummary(result)}");
                return result;
            }

            Task<string>? outputTask = captureOutput ? process.StandardOutput.ReadToEndAsync() : null;
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                TryKill(process);
                var result = new ProcessResult(false, string.Empty, TimedOut: true);
                if (logFailure)
                    DiagnosticLog.Warn($"{purpose} timed out after {timeout.TotalSeconds:0.#} seconds");
                return result;
            }

            string output = outputTask is null ? string.Empty : outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            var completed = new ProcessResult(process.ExitCode == 0, output, ExitCode: process.ExitCode);
            if (completed.Succeeded)
            {
                if (logSuccess)
                    DiagnosticLog.Info($"{purpose} succeeded");
            }
            else if (logFailure)
            {
                DiagnosticLog.Warn($"{purpose} failed: {ProcessSummary(completed)}");
            }

            return completed;
        }
        catch (Exception ex)
        {
            var result = new ProcessResult(false, string.Empty, Error: ex.Message);
            if (logFailure)
                DiagnosticLog.Warn($"{purpose} failed: {ProcessSummary(result)}");
            return result;
        }
    }

    private static ProcessBytesResult RunProcessBytes(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string purpose = "process",
        bool logFailure = true)
    {
        try
        {
            var startInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                var result = new ProcessBytesResult(false, Array.Empty<byte>(), Error: "Process.Start returned null");
                if (logFailure)
                    DiagnosticLog.Warn($"{purpose} failed: {ProcessSummary(result)}");
                return result;
            }

            using var output = new MemoryStream();
            Task copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                TryKill(process);
                var result = new ProcessBytesResult(false, Array.Empty<byte>(), TimedOut: true);
                if (logFailure)
                    DiagnosticLog.Warn($"{purpose} timed out after {timeout.TotalSeconds:0.#} seconds");
                return result;
            }

            copyTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            var completed = new ProcessBytesResult(process.ExitCode == 0, output.ToArray(), ExitCode: process.ExitCode);
            if (!completed.Succeeded && logFailure)
                DiagnosticLog.Warn($"{purpose} failed: {ProcessSummary(completed)}");
            return completed;
        }
        catch (Exception ex)
        {
            var result = new ProcessBytesResult(false, Array.Empty<byte>(), Error: ex.Message);
            if (logFailure)
                DiagnosticLog.Warn($"{purpose} failed: {ProcessSummary(result)}");
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

    private static string ProcessSummary(ProcessBytesResult result)
    {
        if (result.TimedOut)
            return "timeout";
        if (result.ExitCode is int exitCode)
            return $"exit {exitCode}";
        return result.Error ?? "unknown";
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup after timeout.
        }
    }

    private abstract record CredentialSource;
    private sealed record WindowsCredentialSource(string Path) : CredentialSource;
    private sealed record DesktopCredentialSource(string ConfigPath) : CredentialSource;
    private sealed record WslCredentialSource(string Distro) : CredentialSource;
    private sealed record ClaudeCredential(string AccessToken, long? ExpiresAtUnixMs, CredentialSource Source);
    private readonly record struct CredentialResolution(ClaudeCredential? Credential, string? Error);
    private readonly record struct ProcessResult(
        bool Succeeded,
        string Output,
        int? ExitCode = null,
        bool TimedOut = false,
        string? Error = null);
    private readonly record struct ProcessBytesResult(
        bool Succeeded,
        byte[] Output,
        int? ExitCode = null,
        bool TimedOut = false,
        string? Error = null);

    private readonly record struct ProviderResult(
        UsageSnapshot? Snapshot,
        bool AuthFailed,
        bool RateLimited,
        string? Error,
        TimeSpan? RetryAfter)
    {
        public static ProviderResult Ok(UsageSnapshot snap) => new(snap, false, false, null, null);
        public static ProviderResult Auth() => new(null, true, false, null, null);
        public static ProviderResult RateLimit(string error, TimeSpan? retryAfter = null) => new(null, false, true, error, retryAfter);
        public static ProviderResult Failed(string error) => new(null, false, false, error, null);
        // Used when we deliberately did not attempt this path (e.g. OAuth in cooldown).
        // Distinguished from Failed so the "both endpoints failed" error doesn't surface
        // a misleading "skipped" message when the fallback is what actually failed.
        public static ProviderResult Skipped() => new(null, false, false, null, null);
    }
}
