# AGENTS.md

## Rules

- After finishing a feature or fixing a bug
  - Add any interface it needs for testing to the debug MCP interface.
  - Automatically build and launch the program.
    - If the program from the current worktree is already running, kill only the process whose executable path matches this worktree, then run it again. Leave Debug instances from other worktrees running.
  - Use the current worktree's Debug MCP (`bin\JeekTokenPlanUsageMcp.exe --surface debug`, which forwards stdio to this worktree's named pipe) to test the feature or bug, if anything wrong, try to fix it and test again, until all done.
- When reading code, logs and the Debug MCP are not enough to locate a problem, use a debugger:
  - Use netcoredbg on the Debug build to set breakpoints, step, and inspect variables; feed it a command script via stdin, and drive the program to the breakpoint through the Debug MCP.
  - Use dotnet-dump to analyze hangs and crashes.
  - Only attach to the current worktree's process, run the session with a timeout, and always detach when done.
- Always use rebase and fast-forward for Git, never merge.
- Use English for commit messages, keeping them to a brief sentence or two stating the purpose without elaborating on implementation details.
- Commit and push a submodule before the parent commit that moves its pointer.
- Do not copy runtime files from the source directory; keep and version-control them directly under the bin directory.

## MCP

Agents talk to a running instance over a Windows named pipe, never a TCP port. `bin\JeekTokenPlanUsageMcp.exe` is the stdio adapter they launch; it derives the pipe name from its own folder, so a worktree's copy only ever reaches that worktree's app, and it reconnects on its own when the app restarts.

- **Two surfaces, never merged.** `--surface debug` exposes the object graph, visual tree, and probes, and only listens in Debug builds. `--surface product` exposes the app's own features to a user's agent and ships in Release. The debug `invoke` tool can call anything in the process, so it must never be reachable from a user's agent.
- **Register a tool in two places**: the handler on the host, and its schema in the surface's contract class. A tool missing from the contract is invisible to clients.
- **Secrets are write-only.** No tool returns a password, a passphrase, or an encrypted blob — only `hasPassword`-style booleans. Build responses from an explicit field whitelist, never by serializing a model, so a field added later cannot leak.
- **Anything that needs the user happens in the GUI.** Secrets the user must type are entered there, never as tool arguments; destructive actions are confirmed there. Activate the window and return a status the agent can poll (`awaiting_user`) rather than blocking a tool call indefinitely.
- Tool work that touches UI state runs on the UI thread through the host's invoker.

See [bin/MCP.md](bin/MCP.md) for the tool list and usage.

## 参考

每个 provider 的实现说明见 [docs/](docs/)：

- [Claude 用量获取](docs/claude-usage.md)
- [Codex 用量获取](docs/codex-usage.md)
- [Cursor 用量获取](docs/cursor-usage.md)
- [Grok 用量获取](docs/grok-usage.md)
- [自动更新](docs/auto-update.md)
- [MCP 接口](docs/mcp.md)
- [托盘图标实现](docs/tray-icon.md)
- [任务栏组件实现](docs/taskbar-widget.md)
