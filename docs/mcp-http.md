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

### `get_ui_state`

返回托盘程序当前界面和设置状态，供自动化测试断言使用。结构化结果包含：

- `detailsVisible`：详情弹窗是否可见。
- `anchorVisible`：锚点托盘图标是否可见。
- `logPath`：当前日志路径。
- `settings`：托盘菜单对应的当前设置。
- `allowedValues`：`ui_action` 可用枚举值。

### `ui_action`

执行托盘菜单等价操作。所有动作都在 UI 线程执行，适合测试程序直接调用，不需要模拟鼠标。

通用参数：

- `action`：必填，动作名。
- `provider`：`claude`、`codex`、`cursor`，用于 provider 相关动作。
- `enabled` / `paused` / `visible`：布尔开关。
- `mode`：图标模式、代理模式或存储模式。
- `minutes`：刷新间隔。
- `language`：`""`、`zh-CN`、`en`。
- `offset`：任务栏组件偏移。
- `protocol` / `host` / `port`：自定义代理参数。
- `customRoot`：自定义设置存储根目录。
- `allowUpdateLaunch`：`check_update` 可选，默认 `false`，避免测试时自动启动更新。

支持的 `action`：

| action | 说明 |
|---|---|
| `refresh` | 执行“立即刷新”，可选 `provider` |
| `set_paused` | 设置暂停状态，参数 `paused` |
| `set_provider_enabled` | 显示/隐藏 provider，参数 `provider`、`enabled` |
| `set_icon_display` | 设置托盘图标模式，`mode`: `none` / `single` / `double` |
| `set_poll_interval` | 设置刷新间隔，`minutes`: `1` / `2` / `3` / `5` / `10` |
| `set_language` | 设置界面语言，`language`: `""` / `zh-CN` / `en` |
| `set_threshold_notifications` | 设置阈值通知，参数 `enabled` |
| `set_taskbar_widget` | 设置任务栏组件显示和偏移，参数 `visible`、`offset` |
| `set_startup` | 设置开机启动，参数 `enabled` |
| `set_auto_update` | 设置自动更新，参数 `enabled` |
| `set_proxy` | 设置代理，`mode`: `direct` / `system` / `custom`，可带 `protocol`、`host`、`port` |
| `set_storage` | 设置存储位置，`mode`: `appData` / `portable` / `custom`，custom 可带 `customRoot` |
| `show_details` | 显示详情弹窗 |
| `hide_details` | 隐藏详情弹窗 |
| `toggle_details` | 切换详情弹窗 |
| `open_log` | 执行“打开日志” |
| `check_update` | 检查更新，默认不启动更新 |
| `show_about` | 显示 About 窗口 |
| `exit_app` | 安排程序退出，响应返回后执行 |

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

执行界面动作示例：

```powershell
$body = @{
  jsonrpc = "2.0"
  id = 2
  method = "tools/call"
  params = @{
    name = "ui_action"
    arguments = @{
      action = "set_icon_display"
      mode = "single"
    }
  }
} | ConvertTo-Json -Depth 8

Invoke-RestMethod `
  -Uri $config.endpoint `
  -Method Post `
  -Headers @{ Authorization = $config.authorizationValue } `
  -ContentType "application/json" `
  -Body $body
```
