using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace JeekTokenPlanUsage;

internal interface IMcpUsageSource
{
    Task<McpUsageState> GetUsageAsync(string? provider, bool refresh, CancellationToken ct);
}

internal sealed record McpUsageState(
    DateTimeOffset GeneratedAt,
    bool Paused,
    IReadOnlyList<McpProviderState> Providers);

internal sealed record McpProviderState(
    string Id,
    string Name,
    bool Enabled,
    DateTimeOffset? LastPollAt,
    DateTimeOffset? Timestamp,
    string? Error,
    string? ErrorKind,
    IReadOnlyList<McpUsageWindow> Windows);

internal sealed record McpUsageWindow(
    string Id,
    string Label,
    double? Utilization,
    DateTimeOffset? ResetsAt);

internal sealed class McpHttpServer : IDisposable
{
    private const int FirstPort = 39271;
    private const int PortCount = 10;
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private const string EndpointPath = "/mcp";
    private const string ProtocolVersion = "2025-06-18";
    private const string ServerName = "JeekTokenPlanUsage";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonNode GetUsageInputSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "provider": {
              "type": "string",
              "enum": ["claude", "codex", "cursor"],
              "description": "Optional provider id. Omit it to return all providers."
            },
            "refresh": {
              "type": "boolean",
              "default": false,
              "description": "When true, refresh usage before returning the snapshot."
            }
          },
          "additionalProperties": false
        }
        """)!;

    private static readonly JsonNode RefreshUsageInputSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "provider": {
              "type": "string",
              "enum": ["claude", "codex", "cursor"],
              "description": "Optional provider id. Omit it to refresh all providers."
            }
          },
          "additionalProperties": false
        }
        """)!;

    private readonly IMcpUsageSource _usageSource;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private string _token = "";
    private int _port;
    private bool _disposed;

    public McpHttpServer(IMcpUsageSource usageSource)
    {
        _usageSource = usageSource;
    }

    public string? Endpoint => _port > 0 ? $"http://127.0.0.1:{_port}{EndpointPath}" : null;

    private static string LocalConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JeekTokenPlanUsage",
        "Config");

    private static string TokenPath => Path.Combine(LocalConfigDirectory, "mcp-token.txt");

    private static string ClientConfigPath => Path.Combine(LocalConfigDirectory, "mcp-http.json");

    public void Start()
    {
        if (_listener is not null || _disposed)
            return;

        _token = LoadOrCreateToken();

        for (int port = FirstPort; port < FirstPort + PortCount; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                _listener = listener;
                _port = port;
                WriteClientConfig();
                _acceptLoop = Task.Run(AcceptLoopAsync);
                Log.Info($"MCP HTTP server listening on {Endpoint}; client config: {ClientConfigPath}");
                return;
            }
            catch (SocketException ex)
            {
                Log.Warn($"MCP HTTP server could not bind 127.0.0.1:{port}: {ex.SocketErrorCode}");
            }
            catch (Exception ex)
            {
                Log.Warn($"MCP HTTP server could not bind 127.0.0.1:{port}: {ex.Message}");
            }
        }

        Log.Warn("MCP HTTP server did not start: no local port was available");
    }

    private async Task AcceptLoopAsync()
    {
        TcpListener? listener = _listener;
        if (listener is null)
            return;

        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    Log.Warn($"MCP HTTP accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using NetworkStream stream = client.GetStream();
        using (client)
        {
            HttpRequest? request;
            try
            {
                request = await ReadRequestAsync(stream, ct);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            {
                if (!_cts.IsCancellationRequested)
                    Log.Warn($"MCP HTTP request read failed: {ex.Message}");
                return;
            }

            if (request is null)
                return;

            string? corsOrigin = GetAllowedCorsOrigin(request);

            if (!IsAllowedHost(request.Headers.TryGetValue("Host", out string? host) ? host : null))
            {
                await WriteTextResponseAsync(stream, 403, "Forbidden", "Invalid Host header", corsOrigin, ct);
                return;
            }

            if (request.Headers.TryGetValue("Origin", out string? origin) && !IsAllowedOrigin(origin))
            {
                await WriteTextResponseAsync(stream, 403, "Forbidden", "Invalid Origin header", null, ct);
                return;
            }

            if (!IsMcpPath(request.Target))
            {
                await WriteTextResponseAsync(stream, 404, "Not Found", "Not found", corsOrigin, ct);
                return;
            }

            if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(
                    stream,
                    204,
                    "No Content",
                    "text/plain; charset=utf-8",
                    Array.Empty<byte>(),
                    corsOrigin,
                    new[]
                    {
                        ("Access-Control-Allow-Methods", "POST, OPTIONS"),
                        ("Access-Control-Allow-Headers", "authorization, content-type, mcp-protocol-version"),
                        ("Access-Control-Max-Age", "600"),
                    },
                    ct);
                return;
            }

            if (!request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextResponseAsync(
                    stream,
                    405,
                    "Method Not Allowed",
                    "Only POST is supported for this MCP endpoint.",
                    corsOrigin,
                    ct,
                    ("Allow", "POST, OPTIONS"));
                return;
            }

            if (!IsAuthorized(request.Headers.TryGetValue("Authorization", out string? authorization) ? authorization : null))
            {
                await WriteTextResponseAsync(
                    stream,
                    401,
                    "Unauthorized",
                    "Missing or invalid bearer token.",
                    corsOrigin,
                    ct,
                    ("WWW-Authenticate", "Bearer"));
                return;
            }

            await HandleMcpPostAsync(stream, request.Body, corsOrigin, ct);
        }
    }

    private async Task HandleMcpPostAsync(NetworkStream stream, byte[] body, string? corsOrigin, CancellationToken ct)
    {
        JsonNode? requestNode;
        try
        {
            requestNode = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            await WriteJsonResponseAsync(stream, BuildError(null, -32700, "Parse error"), corsOrigin, ct);
            return;
        }

        JsonNode? response;
        if (requestNode is JsonArray batch)
        {
            if (batch.Count == 0)
            {
                response = BuildError(null, -32600, "Invalid Request");
            }
            else
            {
                var responses = new JsonArray();
                foreach (JsonNode? item in batch)
                {
                    JsonObject? itemResponse = await HandleJsonRpcMessageAsync(item, ct);
                    if (itemResponse is not null)
                        responses.Add(itemResponse);
                }

                response = responses.Count > 0 ? responses : null;
            }
        }
        else
        {
            response = await HandleJsonRpcMessageAsync(requestNode, ct);
        }

        if (response is null)
        {
            await WriteResponseAsync(
                stream,
                202,
                "Accepted",
                "text/plain; charset=utf-8",
                Array.Empty<byte>(),
                corsOrigin,
                null,
                ct);
            return;
        }

        await WriteJsonResponseAsync(stream, response, corsOrigin, ct);
    }

    private async Task<JsonObject?> HandleJsonRpcMessageAsync(JsonNode? node, CancellationToken ct)
    {
        if (node is not JsonObject request)
            return BuildError(null, -32600, "Invalid Request");

        JsonNode? id = request["id"]?.DeepClone();
        bool notification = !request.ContainsKey("id");

        if (request["jsonrpc"]?.GetValue<string>() != "2.0")
            return notification ? null : BuildError(id, -32600, "Invalid Request");

        string? method = request["method"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(method))
            return notification ? null : BuildError(id, -32600, "Invalid Request");

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => BuildInitializeResult(),
                "ping" => new JsonObject(),
                "tools/list" => BuildToolsListResult(),
                "tools/call" => await HandleToolCallAsync(request["params"] as JsonObject, ct),
                "notifications/initialized" => null,
                _ => throw new JsonRpcException(-32601, "Method not found"),
            };

            return notification || result is null ? null : BuildResult(id, result);
        }
        catch (JsonRpcException ex)
        {
            return notification ? null : BuildError(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return notification ? null : BuildError(id, -32603, ex.Message);
        }
    }

    private static JsonObject BuildInitializeResult() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject(),
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = ServerName,
            ["version"] = typeof(McpHttpServer).Assembly.GetName().Version?.ToString() ?? "0",
        },
    };

    private static JsonObject BuildToolsListResult() => new()
    {
        ["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "get_usage",
                ["description"] = "Return current Claude, Codex, and Cursor plan usage from the tray app.",
                ["inputSchema"] = GetUsageInputSchema.DeepClone(),
            },
            new JsonObject
            {
                ["name"] = "refresh_usage",
                ["description"] = "Refresh one provider or all providers, then return the updated usage snapshot.",
                ["inputSchema"] = RefreshUsageInputSchema.DeepClone(),
            },
        },
    };

    private async Task<JsonObject> HandleToolCallAsync(JsonObject? parameters, CancellationToken ct)
    {
        string? name = parameters?["name"]?.GetValue<string>();
        JsonObject? arguments = parameters?["arguments"] as JsonObject;

        if (string.IsNullOrWhiteSpace(name))
            throw new JsonRpcException(-32602, "Missing tool name");

        return name switch
        {
            "get_usage" => await BuildUsageToolResultAsync(arguments, refreshDefault: false, ct),
            "refresh_usage" => await BuildUsageToolResultAsync(arguments, refreshDefault: true, ct),
            _ => throw new JsonRpcException(-32602, $"Unknown tool: {name}"),
        };
    }

    private async Task<JsonObject> BuildUsageToolResultAsync(JsonObject? arguments, bool refreshDefault, CancellationToken ct)
    {
        try
        {
            string? provider = arguments?["provider"]?.GetValue<string>();
            bool refresh = refreshDefault || (arguments?["refresh"]?.GetValue<bool>() ?? false);
            McpUsageState state = await _usageSource.GetUsageAsync(provider, refresh, ct);
            JsonNode structured = JsonSerializer.SerializeToNode(state, JsonOptions)!;
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = FormatUsageText(state),
                    },
                },
                ["structuredContent"] = structured,
            };
        }
        catch (ArgumentException ex)
        {
            return ToolError(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolError(ex.Message);
        }
    }

    private static JsonObject ToolError(string message) => new()
    {
        ["isError"] = true,
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = message,
            },
        },
    };

    private static string FormatUsageText(McpUsageState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"generatedAt: {state.GeneratedAt:O}");
        sb.AppendLine($"paused: {state.Paused.ToString().ToLowerInvariant()}");
        foreach (McpProviderState provider in state.Providers)
        {
            string status = provider.Error is null ? "ok" : $"error: {provider.Error}";
            sb.AppendLine($"{provider.Name} ({provider.Id}, enabled={provider.Enabled.ToString().ToLowerInvariant()}): {status}");
            foreach (McpUsageWindow window in provider.Windows)
            {
                string usage = window.Utilization is null ? "n/a" : $"{window.Utilization:0.#}%";
                string reset = window.ResetsAt is null ? "n/a" : window.ResetsAt.Value.ToString("O");
                sb.AppendLine($"  - {window.Label}: {usage}, resetsAt={reset}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static JsonObject BuildResult(JsonNode? id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject BuildError(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var headerBytes = new MemoryStream();
        var one = new byte[1];
        int matched = 0;
        byte[] marker = "\r\n\r\n"u8.ToArray();

        while (headerBytes.Length < MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(one, ct);
            if (read == 0)
                return null;

            byte b = one[0];
            headerBytes.WriteByte(b);
            matched = b == marker[matched] ? matched + 1 : b == marker[0] ? 1 : 0;
            if (matched == marker.Length)
                break;
        }

        if (matched != marker.Length)
            throw new InvalidOperationException("HTTP headers exceeded the maximum size");

        string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
            throw new InvalidOperationException("Invalid HTTP request line");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
                break;

            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            headers[key] = value;
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? lengthText)
            && (!int.TryParse(lengthText, out contentLength) || contentLength < 0))
            throw new InvalidOperationException("Invalid Content-Length");

        if (contentLength > MaxBodyBytes)
            throw new InvalidOperationException("HTTP body exceeded the maximum size");

        byte[] body = new byte[contentLength];
        if (contentLength > 0)
            await stream.ReadExactlyAsync(body.AsMemory(), ct);

        return new HttpRequest(requestLine[0], requestLine[1], headers, body);
    }

    private async Task WriteJsonResponseAsync(NetworkStream stream, JsonNode body, string? corsOrigin, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        await WriteResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", bytes, corsOrigin, null, ct);
    }

    private static async Task WriteTextResponseAsync(
        NetworkStream stream,
        int status,
        string reason,
        string text,
        string? corsOrigin,
        CancellationToken ct,
        params (string Name, string Value)[] extraHeaders)
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        await WriteResponseAsync(
            stream,
            status,
            reason,
            "text/plain; charset=utf-8",
            body,
            corsOrigin,
            extraHeaders,
            ct);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string reason,
        string contentType,
        byte[] body,
        string? corsOrigin,
        IEnumerable<(string Name, string Value)>? extraHeaders,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("MCP-Protocol-Version: ").Append(ProtocolVersion).Append("\r\n");

        if (corsOrigin is not null)
        {
            sb.Append("Access-Control-Allow-Origin: ").Append(corsOrigin).Append("\r\n");
            sb.Append("Vary: Origin\r\n");
        }

        if (extraHeaders is not null)
        {
            foreach ((string name, string value) in extraHeaders)
                sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        sb.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct);
    }

    private bool IsAuthorized(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string supplied = authorization[prefix.Length..].Trim();
        byte[] left = Encoding.UTF8.GetBytes(supplied);
        byte[] right = Encoding.UTF8.GetBytes(_token);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static bool IsMcpPath(string target)
    {
        string path = target.Split('?', 2)[0];
        return path.Equals(EndpointPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(EndpointPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetAllowedCorsOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out string? origin))
            return null;
        return IsAllowedOrigin(origin) ? origin : null;
    }

    private static bool IsAllowedOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) && IsLoopbackHost(uri.Host);

    private static bool IsAllowedHost(string? hostHeader)
    {
        if (string.IsNullOrWhiteSpace(hostHeader))
            return false;

        string host = hostHeader.Trim();
        if (host.StartsWith('['))
        {
            int end = host.IndexOf(']');
            if (end >= 0)
                host = host[1..end];
        }
        else
        {
            int colon = host.IndexOf(':');
            if (colon >= 0)
                host = host[..colon];
        }

        return IsLoopbackHost(host);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static string LoadOrCreateToken()
    {
        try
        {
            Directory.CreateDirectory(LocalConfigDirectory);
            if (File.Exists(TokenPath))
            {
                string existing = File.ReadAllText(TokenPath).Trim();
                if (existing.Length >= 32)
                    return existing;
            }

            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            string token = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            File.WriteAllText(TokenPath, token);
            return token;
        }
        catch (Exception ex)
        {
            Log.Warn($"MCP token file unavailable; using an in-memory token: {ex.Message}");
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }

    private void WriteClientConfig()
    {
        try
        {
            Directory.CreateDirectory(LocalConfigDirectory);
            var config = new JsonObject
            {
                ["endpoint"] = Endpoint,
                ["authorizationHeader"] = "Authorization",
                ["authorizationValue"] = $"Bearer {_token}",
                ["protocolVersion"] = ProtocolVersion,
            };
            File.WriteAllText(ClientConfigPath, JsonSerializer.Serialize(config, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn($"MCP client config write failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts.Cancel();
        _listener?.Stop();
        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    private sealed record HttpRequest(
        string Method,
        string Target,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    private sealed class JsonRpcException : Exception
    {
        public JsonRpcException(int code, string message) : base(message)
        {
            Code = code;
        }

        public int Code { get; }
    }
}
