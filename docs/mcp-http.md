# MCP HTTP 接口

实现：[McpHttpServer.cs](../JeekTokenPlanUsage/McpHttpServer.cs)，由 [TrayApplicationContext.cs](../JeekTokenPlanUsage/TrayApplicationContext.cs) 在主程序启动时自动启动。

## 监听地址

服务只监听本机回环地址，默认从下面端口开始尝试：

```text
http://127.0.0.1:39271/mcp
```

如果端口被占用，会顺延尝试 `39272` 到 `39280`。实际 endpoint 和鉴权头会写到：

```text
%LocalAppData%\JeekTokenPlanUsage\Config\mcp-http.json
```

鉴权 token 单独保存在：

```text
%LocalAppData%\JeekTokenPlanUsage\Config\mcp-token.txt
```

客户端调用时需要带：

```text
Authorization: Bearer <token>
```

## 安全边界

- 只绑定 `127.0.0.1`，不监听局域网地址。
- 校验 `Host`，只接受 `localhost`、`127.0.0.1`、`::1`。
- 如果请求带 `Origin`，只接受回环来源，避免浏览器 DNS rebinding 风险。
- MCP 工具不会返回 access token、凭据文件内容或 provider 原始响应。

## MCP 工具

### `get_usage`

返回当前缓存的 Claude、Codex、Cursor 用量快照。

参数：

- `provider`：可选，`claude`、`codex`、`cursor`。
- `refresh`：可选，`true` 时先刷新再返回。

### `refresh_usage`

刷新一个 provider 或全部 provider，然后返回最新快照。

参数：

- `provider`：可选，`claude`、`codex`、`cursor`。

如果程序处于暂停状态，刷新入口会沿用主程序行为，不主动执行 provider 请求；响应里的 `paused` 会是 `true`。

## 示例

```powershell
$config = Get-Content "$env:LOCALAPPDATA\JeekTokenPlanUsage\Config\mcp-http.json" | ConvertFrom-Json
$body = @{
  jsonrpc = "2.0"
  id = 1
  method = "tools/call"
  params = @{
    name = "get_usage"
    arguments = @{}
  }
} | ConvertTo-Json -Depth 8

Invoke-RestMethod `
  -Uri $config.endpoint `
  -Method Post `
  -Headers @{ Authorization = $config.authorizationValue } `
  -ContentType "application/json" `
  -Body $body
```
