# Codex 用量获取

实现：[CodexUsageProvider.cs](../CodexUsageProvider.cs)

## 数据来源

```
GET https://chatgpt.com/backend-api/codex/usage
Authorization: Bearer <access_token>
chatgpt-account-id: <account_id>     (可选)
originator: codex_cli_rs
User-Agent: codex_cli_rs/0.121.0
```

响应 JSON：

```json
{
  "rate_limit": {
    "primary_window": { "used_percent": 35.2, "reset_at": 1717000000 },
    "secondary_window": { "used_percent": 12.7, "reset_at": 1717500000 }
  }
}
```

- `used_percent` 已经是 0..100 百分比，直接用
- `reset_at` 是 unix 秒
- `primary_window` ≈ 5 小时窗口，`secondary_window` ≈ 周窗口

## 凭据来源

`%USERPROFILE%\.codex\auth.json` → `tokens.access_token` 和 `tokens.account_id`

```json
{
  "tokens": {
    "access_token": "...",
    "account_id": "..."
  }
}
```

## 为什么 shell out 到 curl

[CodexUsageProvider.cs:7-11](../CodexUsageProvider.cs#L7-L11)

`chatgpt.com` 走 Cloudflare bot 保护，会基于 TLS 指纹拒掉 .NET 的 `HttpClient`。系统自带的 `curl.exe` 能通过，所以这里用 `Process.Start` 调起 curl。

- 用 `--config -`，从 stdin 传配置文件（URL、headers、超时、写出指令）
- Token 不出现在命令行参数里，避免在 `ps`/任务管理器中泄露
- 配置末尾的 `write-out = "\nHTTPSTATUS:%{http_code}"` 让 HTTP 状态码追加在 stdout 末尾，供后续解析

[CodexUsageProvider.cs:50-64](../CodexUsageProvider.cs#L50-L64) 是 config 生成；[CodexUsageProvider.cs:107-116](../CodexUsageProvider.cs#L107-L116) 是状态码切分。

## curl 解析

stdout 形如：

```
{"rate_limit": {...}}
HTTPSTATUS:200
```

切分后：body 部分进 `JsonDocument.Parse`，状态码部分单独读出。

## 取消处理

[CodexUsageProvider.cs:94-102](../CodexUsageProvider.cs#L94-L102)

`WaitForExitAsync(ct)` 被取消时 `proc.Kill(entireProcessTree: true)`，确保 curl 子进程不会成为孤儿。

## curl 查找

[CodexUsageProvider.cs:181-185](../CodexUsageProvider.cs#L181-L185)

优先用 `C:\Windows\System32\curl.exe`（Win10+ 自带），找不到就回退到 PATH 里的 `curl.exe`。两者都没有 → 返回"未找到 curl.exe"错误。

## 错误处理

| 状态                 | 行为                                     |
| -------------------- | ---------------------------------------- |
| 401                  | "Token 失效 (401)，请重新登录 Codex"     |
| 429                  | "接口限流 (429)"                         |
| 其他非 2xx           | "HTTP <code>"                            |
| curl 进程失败        | "curl 失败(<exit>): <stderr 前 80 字符>" |
| JSON 缺 `rate_limit` | "响应缺少 rate_limit"                    |
| 凭据缺失或损坏       | 明确错误，提示登录 Codex                 |

## 轮询节奏

基础间隔由托盘菜单"刷新间隔"统一控制，三家共用：1 / 5 / 10 / 30 / 60 分钟（默认 5）。

- 端点便宜（不消耗任何配额），随基础间隔走即可
- 没有 Claude 那种 fallback 烧配额的顾虑
- 没有阶梯退避，只有上层 `_codexBusy` 互斥防止重叠
