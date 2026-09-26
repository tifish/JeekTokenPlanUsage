# JeekTokenPlanUsage MCP Interface

The app exposes MCP over a Windows named pipe, not a TCP port: nothing to
allocate, no firewall prompts, and access control comes from the pipe ACL
(current user + SYSTEM only). `JeekTokenPlanUsageMcp.exe` in this directory is
the build copy. On startup a background thread installs it to
`%LocalAppData%\JeekTokenPlanUsage\Mcp`. Agents launch that stable copy; it forwards
JSON-RPC to the running app's pipe.

## Client Configuration

```json
{
    "mcpServers": {
        "jeek-token-plan-usage": {
            "type": "stdio",
            "command": "C:\\Users\\<user>\\AppData\\Local\\JeekTokenPlanUsage\\Mcp\\JeekTokenPlanUsageMcp.exe"
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
            "args": ["/c", ".\\JeekTokenPlanUsageDebugMcp.cmd"],
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
| `--app <path>` | Path to `JeekTokenPlanUsage.exe` for instance routing and auto-launch. |
| `--launch` / `--no-launch` | Auto-start the app on a tool call. Default: on for `product`, off for `debug`. |

The Debug launcher passes this worktree executable via `--app`; its directory
determines the instance pipe. Debug never falls back to another instance.
Product auto-launch defaults to `%LocalAppData%\Programs\JeekTokenPlanUsage`. It answers `initialize`/`ping` locally
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
`anchorVisible`, `logPath`, `settings`, `allowedValues`, and the latest `operation`
(id, action, status, message). Poll the operation until completed, cancelled, postponed or failed.
`awaiting_user` means the active GUI confirmation needs the user; tools cannot approve it.

### `ui_action`

Invokes a tray-menu-equivalent action on the UI thread.

| action | Required or useful arguments |
|---|---|
| `refresh` | optional `provider` |
| `set_paused` | `paused` |
| `set_provider_enabled` | `provider`, `enabled` |
| `set_icon_display` | `mode`: `none`, `single`, `double` |
| `set_poll_interval` | `minutes`: `1`, `2`, `3`, `5`, `10` |
| `set_theme` | `mode`: `system`, `light`, `dark` |
| `set_language` | `language`: `""`, `zh-CN`, `en` |
| `set_threshold_notifications` | `enabled` |
| `set_taskbar_widget` | optional `visible`, optional `offset` |
| `set_startup` | `enabled` |
| `set_auto_update` | `enabled` |
| `set_proxy` | `mode`: `direct`, `system`, `custom`; optional `protocol`, `host`, `port` |
| `set_storage` | `mode`: `appData`, `portable`, `custom`; optional `customRoot` |
| `show_details` / `hide_details` / `toggle_details` | none |
| `open_log` | none |
| `check_update` | prepare the update, then request GUI confirmation |
| `show_about` | none |
| `exit_app` | none |

`check_update` always requires GUI confirmation. The legacy `allowUpdateLaunch`
argument is ignored and cannot authorize installation.

## Debug Tools (Debug builds only)

`probe_dependencies` takes no arguments and checks the deployed SQLite libraries
with a parameterized Unicode read/write in a private in-memory database. It
returns `sqliteVersion`, `managedSqliteVersion`, and `roundTrip`. It also emits
a diagnostic log entry; no provider databases or credentials are accessed.

`describe`, `get_value`, `set_value`, `invoke`, `list_members`, `read_logs` —
the standard object-graph tools. Paths start at the root `Context`
(the `TrayApplicationContext`); e.g. `Context._settings.PollMinutes`.
`#Name` segments look up WinForms child controls by name.

`probe_threshold_notifications` evaluates `samples` in order against a fresh,
isolated window state using the same policy as the tray. Each sample has
`utilization` (number), optional `resetsAt` (ISO 8601 string), and optional
`enabled` (boolean, default true). Returns `results` with `shouldNotify` and
`lastNotifiedThreshold`, and the stable `cycleReset` anchor. It does not send
notifications or change live state.
Usage at or above 100% is silent and consumes the current cycle's thresholds;
80% / 95% alerts are re-armed only when the reset time advances by more than
one minute from the cycle anchor. Jitter, missing resets, and older timestamps
do not re-arm alerts. Run `python Tools/TestThresholdNotifications.py`
against the running Debug build to verify this policy.

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

Storage changes and update checks return `status` and `operationId` immediately.
The legacy `allowUpdateLaunch` field is ignored; it never bypasses GUI confirmation.
