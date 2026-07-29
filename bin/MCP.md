# JeekTokenPlanUsage MCP Interface

The app exposes MCP over a Windows named pipe, not a TCP port: nothing to
allocate, no firewall prompts, and access control comes from the pipe ACL
(current user + SYSTEM only). `JeekTokenPlanUsageMcp.exe` in this directory is
a stdio adapter an agent launches like any stdio MCP server; it forwards
JSON-RPC to the running app's pipe.

## Client Configuration

```json
{
    "mcpServers": {
        "jeek-token-plan-usage": {
            "type": "stdio",
            "command": "C:\\path\\to\\bin\\JeekTokenPlanUsageMcp.exe"
        }
    }
}
```

With a relative path, wrap it in `cmd /c` (Windows resolves relative
executables against the parent's directory, not the configured cwd):

```json
{
    "mcpServers": {
        "jtpu-debug": {
            "type": "stdio",
            "command": "cmd",
            "args": ["/c", ".\\bin\\JeekTokenPlanUsageMcp.exe", "--surface", "debug"],
            "cwd": "."
        }
    }
}
```

Adapter options:

| Option | Meaning |
|---|---|
| `--surface product\|debug` | Which endpoint to reach. Default `product`. |
| `--pipe <name>` | Explicit pipe name override. |
| `--instance <id>` | Explicit instance id override. |
| `--app <path>` | Path to `JeekTokenPlanUsage.exe` for auto-launch. |
| `--launch` / `--no-launch` | Auto-start the app on a tool call. Default: on for `product`, off for `debug`. |

The adapter derives the pipe name from its own folder, so a copy only ever
reaches the app in the same folder. It answers `initialize`/`ping` locally
while the app is closed (the session stays usable) and reconnects on its own
after an app restart.

## Surfaces

Two independent endpoints share the transport but never share tools:

- **Product** (`JeekTokenPlanUsage.Mcp`): the app's features for a user's
  agent. Listens in every build.
- **Debug** (`JeekTokenPlanUsage.Mcp.Debug`): object-graph access for
  development. Only listens in Debug builds. Debug builds suffix both pipe
  names with a 12-hex hash of the executable directory so parallel worktrees
  stay isolated.

## Product Tools

### `get_usage`

Returns the current cached usage snapshot.

```json
{ "provider": "claude | codex | cursor | grok", "refresh": false }
```

Both arguments are optional. Omit `provider` to return all providers.

### `refresh_usage`

Refreshes one provider (optional `provider`) or all providers, then returns
the updated snapshot.

### `get_ui_state`

Returns the current tray UI and settings state: `detailsVisible`,
`anchorVisible`, `logPath`, `settings`, `allowedValues`.

### `ui_action`

Invokes a tray-menu-equivalent action on the UI thread.

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
| `show_details` / `hide_details` / `toggle_details` | none |
| `open_log` | none |
| `check_update` | optional `allowUpdateLaunch`, default `false` |
| `show_about` | none |
| `exit_app` | none |

`check_update` does not launch the updater unless `allowUpdateLaunch` is
`true`, so automated tests can inspect update status safely.

## Debug Tools (Debug builds only)

`describe`, `get_value`, `set_value`, `invoke`, `list_members`, `read_logs` —
the standard object-graph tools. Paths start at the root `Context`
(the `TrayApplicationContext`); e.g. `Context._settings.PollMinutes`.
`#Name` segments look up WinForms child controls by name.

## Response Shape

Product tool results include `structuredContent`:

```json
{
  "generatedAt": "2026-06-29T00:00:00.0000000+00:00",
  "paused": false,
  "providers": [
    {
      "id": "claude",
      "name": "Claude",
      "enabled": true,
      "error": null,
      "windows": [
        { "id": "five_hour", "label": "5h", "utilization": 12.3, "resetsAt": "2026-06-29T05:00:00.0000000+00:00" }
      ]
    }
  ]
}
```

The MCP interface does not expose access tokens, credential file contents, or
raw provider responses.
