# JeekTokenPlanUsage

A Windows tray app that shows your AI subscription plan usage at a glance.
It polls the local credentials of the CLI/IDE tools you already use — no extra
login — and renders each usage window as a tiny percentage icon in the tray.

Supported providers:

- **Claude** (Claude Code) — 5-hour and weekly windows
- **Codex** (OpenAI) — 5-hour and weekly windows
- **Cursor** — plan and API usage
- **Grok** (xAI) — CLI billing windows

Features: per-provider tray icons with tooltips, a details popup, an optional
taskbar widget, threshold toast notifications (80% / 95%), light/dark theme,
English / 简体中文, proxy modes (system / direct / custom), portable or roaming
settings storage, auto-update, and an MCP interface for AI agents
([bin/MCP.md](bin/MCP.md)).

## Installation

Run in PowerShell:

```powershell
irm https://raw.githubusercontent.com/tifish/JeekTokenPlanUsage/main/install.ps1 | iex
```

If GitHub is hard to reach (e.g. in mainland China), use the mirror:

```powershell
irm https://ghfast.top/https://raw.githubusercontent.com/tifish/JeekTokenPlanUsage/main/install.ps1 | iex
```

The app is installed to `%LOCALAPPDATA%\Programs\JeekTokenPlanUsage` with a
Start Menu shortcut. No registry entries are written; to uninstall, quit the
app and delete the install directory and the shortcut. If the .NET 10 Desktop
runtime is missing, the bundled `Setup.cmd` installs it automatically.

## Building from source

```powershell
git clone --recursive https://github.com/tifish/JeekTokenPlanUsage.git
cd JeekTokenPlanUsage
.\Run.cmd
```

Requires the .NET 10 SDK. `Build.cmd` is the Release ship script into `bin\`;
`Run.cmd` builds Debug and launches.

## License

See [JeekTools.NET](https://github.com/tifish/JeekTools.NET) for the shared
library. Provider docs live in [docs/](docs/).
