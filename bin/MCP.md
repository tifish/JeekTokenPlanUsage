# JeekTokenPlanUsage MCP HTTP Interface

This release exposes a local MCP HTTP endpoint for AI clients.

## How to Discover the Endpoint

Start `JeekTokenPlanUsage.exe`, then read:

```text
%LocalAppData%\JeekTokenPlanUsage\Config\mcp-http.json
```

The file contains:

```json
{
  "endpoint": "http://127.0.0.1:39271/mcp",
  "authorizationHeader": "Authorization",
  "authorizationValue": "Bearer <token>",
  "protocolVersion": "2025-06-18"
}
```

The port starts at `39271` and may move up to `39280` if earlier ports are in use.
Always use the `endpoint` value from `mcp-http.json`.

## Transport

- Protocol: MCP over Streamable HTTP
- Endpoint path: `/mcp`
- Method: `POST`
- Body format: JSON-RPC 2.0
- Required header: `Authorization: Bearer <token>`

The server only listens on `127.0.0.1`. It rejects non-loopback `Host` headers and non-loopback browser `Origin` headers.

## Tools

### `get_usage`

Returns the current cached usage snapshot.

Arguments:

```json
{
  "provider": "claude | codex | cursor",
  "refresh": false
}
```

Both arguments are optional. Omit `provider` to return all providers. Set `refresh` to `true` to refresh before returning.

### `refresh_usage`

Refreshes one provider or all providers, then returns the updated snapshot.

Arguments:

```json
{
  "provider": "claude | codex | cursor"
}
```

The argument is optional. Omit `provider` to refresh all providers.

## Example JSON-RPC Call

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "tools/call",
  "params": {
    "name": "get_usage",
    "arguments": {}
  }
}
```

## Response Shape

The tool result includes `structuredContent`:

```json
{
  "generatedAt": "2026-06-29T00:00:00.0000000+00:00",
  "paused": false,
  "providers": [
    {
      "id": "claude",
      "name": "Claude",
      "enabled": true,
      "lastPollAt": "2026-06-29T00:00:00.0000000+00:00",
      "timestamp": "2026-06-29T00:00:00.0000000+00:00",
      "error": null,
      "errorKind": null,
      "windows": [
        {
          "id": "five_hour",
          "label": "5h",
          "utilization": 12.3,
          "resetsAt": "2026-06-29T05:00:00.0000000+00:00"
        },
        {
          "id": "weekly",
          "label": "Weekly",
          "utilization": 45.6,
          "resetsAt": "2026-07-01T00:00:00.0000000+00:00"
        }
      ]
    }
  ]
}
```

If the app is paused, `paused` is `true` and refresh requests follow the app's paused behavior.

The MCP interface does not expose access tokens, credential file contents, or raw provider responses.
