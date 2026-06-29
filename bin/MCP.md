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

### `get_ui_state`

Returns the current tray UI and settings state for automation.

The structured result includes:

- `detailsVisible`
- `anchorVisible`
- `logPath`
- `settings`
- `allowedValues`

### `ui_action`

Invokes a tray-menu-equivalent action on the UI thread.

Arguments:

```json
{
  "action": "set_icon_display",
  "mode": "single"
}
```

Supported actions:

| action | Required or useful arguments |
|---|---|
| `refresh` | optional `provider` |
| `set_paused` | `paused` |
| `set_provider_enabled` | `provider`, `enabled` |
| `set_icon_display` | `mode`: `none`, `single`, `double` |
| `set_poll_interval` | `minutes`: `1`, `2`, `3`, `5`, `10` |
| `set_language` | `language`: `""`, `zh-CN`, `en` |
| `set_threshold_notifications` | `enabled` |
| `set_taskbar_widget` | optional `visible`, optional `offset` |
| `set_startup` | `enabled` |
| `set_auto_update` | `enabled` |
| `set_proxy` | `mode`: `direct`, `system`, `custom`; optional `protocol`, `host`, `port` |
| `set_storage` | `mode`: `appData`, `portable`, `custom`; optional `customRoot` |
| `show_details` | none |
| `hide_details` | none |
| `toggle_details` | none |
| `open_log` | none |
| `check_update` | optional `allowUpdateLaunch`, default `false` |
| `show_about` | none |
| `exit_app` | none |

`check_update` does not launch the updater unless `allowUpdateLaunch` is `true`, so automated tests can inspect update status safely.

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
