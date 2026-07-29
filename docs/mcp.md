# MCP 接口实现

实现：[ProductMcpServer.cs](../JeekTokenPlanUsage/ProductMcpServer.cs) / [ProductMcpContract.cs](../JeekTokenPlanUsage/ProductMcpContract.cs)、[DebugMcpServer.cs](../JeekTokenPlanUsage/DebugMcpServer.cs) / [DebugMcpContract.cs](../JeekTokenPlanUsage/DebugMcpContract.cs)、[McpPipeNames.cs](../JeekTokenPlanUsage/McpPipeNames.cs)、[AppInstance.cs](../JeekTokenPlanUsage/AppInstance.cs)；宿主与传输来自 [JeekTools.NET](../JeekTools.NET) 的 `McpHost` / `McpPipeServer`；stdio 适配器：[Tools/JeekTokenPlanUsageMcp](../Tools/JeekTokenPlanUsageMcp)。使用文档见 [bin/MCP.md](../bin/MCP.md)。

## 传输：命名管道

用 Windows 命名管道，不用 TCP 端口（`DefaultPort = 0`）：无端口分配、不触发防火墙、名字固定可写死在客户端配置里；访问控制交给管道 ACL（仅当前用户 + SYSTEM），不需要 URL token。帧格式是每行一条 JSON-RPC 消息，与 stdio 适配器同格式，转发无需重组。

管道名由 [McpPipeNames.cs](../JeekTokenPlanUsage/McpPipeNames.cs) 统一拼装——这个文件不依赖任何其他代码，适配器工程直接 `Compile Include` 共享，两端不可能对不上：

- Release：裸名 `JeekTokenPlanUsage.Mcp` / `JeekTokenPlanUsage.Mcp.Debug`（单实例）。
- Debug：追加 `.<实例id>`，实例 id = 可执行目录规范化后 SHA256 前 12 位十六进制。多 worktree 天然隔离。

## 两个独立 endpoint

| | 产品接口 | 调试接口 |
|---|---|---|
| 管道基名 | `JeekTokenPlanUsage.Mcp` | `JeekTokenPlanUsage.Mcp.Debug` |
| 何时监听 | 所有构建 | 仅 Debug（运行时门控 `Enabled`，代码所有配置都编译） |
| 工具 | get_usage / refresh_usage / get_ui_state / ui_action | describe / get_value / set_value / invoke / list_members / read_logs |
| 对象图 | 无（`ResolveRoot` 直接抛异常） | 根 `Context`（TrayApplicationContext） |

绝不合并：调试接口的 `invoke` 能调用进程内任意方法，等同于进程内 RCE，不能让最终用户的 agent 看到。

每个工具在两处注册：handler 挂 `AddTool`，schema 放契约类并经 `ToolListProvider` 注入宿主。漏了契约那一处，客户端就看不见这个工具。

碰 UI 状态的工具经 `UiInvoker`（WinForms `SynchronizationContext.Post` + 15 秒超时）走 UI 线程；产品接口的 `IMcpUsageSource` 实现自行在内部编排 UI 线程，无需 UiInvoker。

## stdio 适配器

[Tools/JeekTokenPlanUsageMcp](../Tools/JeekTokenPlanUsageMcp) 随主程序发布到 `bin`：

- 命名用完整程序名 + Mcp（`JeekTokenPlanUsageMcp.exe`），进程列表里一眼可辨。
- 与主程序同目录，从**自身目录**推导实例 id，某个副本只可能连到同目录的程序。
- **单文件发布**（`dotnet publish`，不要 `dotnet build` 进 bin）：主程序用 NetBeauty，会给 bin 里每个 `*.runtimeconfig.json` 打 libloader 启动钩子，被打过的适配器在 agent 会话期间占着 `libloader.dll`，主程序下次构建就会失败；单文件把 runtimeconfig 收进 exe，NetBeauty 扫不到。
- 程序未运行时本地应答 initialize / ping（握手不失败），tools/call 返回可读软报错；产品接口按需拉起主程序（`--launch` 默认仅 product 开）。
- 每次调用前检查连接，断管重连一次——程序重启后 agent 会话不用重开。
- 转发时跳过没有 `id` 的服务端消息，避免把通知误当响应。
- Windows 上 `CreateProcess` 按父进程当前目录解析相对路径，配置里用相对路径要套 `cmd /c`（见根目录 [.mcp.json](../.mcp.json)）。

## 产品接口的安全边界

- 不暴露 access token、凭据文件内容或 provider 原始响应；返回体是显式字段白名单（`McpModels.cs` 里的 record）。
- 需要用户参与的操作只在 GUI 完成；`check_update` 默认不启动更新器（`allowUpdateLaunch` 显式开）。
