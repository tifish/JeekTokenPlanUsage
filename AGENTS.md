# AGENTS.md

## Rules

- After finishing a feature or fixing a bug, automatically build and launch the program for me to test. If the program is already running, kill the process and run it again.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## MCP Testing

- Use the MCP interface for testing and AI automation. See [bin/MCP.md](bin/MCP.md) and [docs/mcp-http.md](docs/mcp-http.md).

## 参考

每个 provider 的实现说明见 [docs/](docs/)：

- [Claude 用量获取](docs/claude-usage.md)
- [Codex 用量获取](docs/codex-usage.md)
- [Cursor 用量获取](docs/cursor-usage.md)
- [Grok 用量获取](docs/grok-usage.md)
- [自动更新](docs/auto-update.md)
- [托盘图标实现](docs/tray-icon.md)
- [任务栏组件实现](docs/taskbar-widget.md)
