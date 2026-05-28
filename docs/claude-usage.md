# Claude 用量获取

实现：[ClaudeUsageProvider.cs](../ClaudeUsageProvider.cs)

## 数据来源

两条路径，按需组合：

### 1. OAuth usage 端点（主路径，零配额消耗）

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <accessToken>
anthropic-beta: oauth-2025-04-20
```

响应 JSON：

```json
{
  "five_hour": { "utilization": 0.42, "resets_at": "2026-05-24T15:00:00Z" },
  "seven_day": { "utilization": 0.18, "resets_at": "2026-05-30T00:00:00Z" }
}
```

- `utilization` 是 0..1 小数；程序内统一转成 0..100 百分比
- 这是 Claude Code 客户端用的未公开端点，只读，不消耗任何配额

### 2. Messages API fallback（兜底，少量配额）

```
POST https://api.anthropic.com/v1/messages
Authorization: Bearer <accessToken>
anthropic-version: 2023-06-01
anthropic-beta: oauth-2025-04-20

{ "model": "claude-haiku-4-5", "max_tokens": 1, "messages": [{"role":"user","content":"."}] }
```

不读 body，只读响应头：

- `anthropic-ratelimit-unified-5h-utilization` / `-5h-reset`（unix 秒）
- `anthropic-ratelimit-unified-7d-utilization` / `-7d-reset`

无论响应 2xx 还是 429，这组头都会返回，所以即使被限流也能拿到数据。模型列表 [ClaudeUsageProvider.cs:21-25](../ClaudeUsageProvider.cs#L21-L25) 是回退链，第一个能拿到 rate-limit 头就停。

## 凭据来源与刷新

优先级：

1. Windows: `%USERPROFILE%\.claude\.credentials.json`
2. WSL fallback: 只枚举 `wsl.exe -l -q --running` 返回的运行中 distro，读取 `~/.claude/.credentials.json`

读取字段：

- `claudeAiOauth.accessToken`
- `claudeAiOauth.expiresAt`（unix 毫秒，可缺省）

如果 `expiresAt` 已过期，程序不直接改凭据文件，而是让 Claude CLI 自己刷新：

- Windows: `claude -p .`
- WSL: `wsl.exe -d <distro> -- bash -lic "<claude refresh command>"`

刷新命令隐藏运行，最多等待 30 秒。刷新后重新读取凭据；如果当前来源仍不可用，会继续尝试下一个来源。未运行的 WSL distro 不会被探测或启动。

## 调用流程

[ClaudeUsageProvider.cs](../ClaudeUsageProvider.cs)

1. 选择第一个可用且未过期的凭据；过期时先调用 Claude CLI 刷新
2. 不在冷却期 → 调 OAuth 端点；冷却期内 → 跳过，标记为 `Skipped`
3. OAuth 返回 429 → 标记 `RateLimited`，按阶梯设置下次冷却
4. OAuth 完全成功（两个窗口的 utilization+reset 都有）→ 直接返回，不调 fallback
5. 否则调 messages fallback，合并数据：utilization 以 OAuth 为准，缺失的 reset 用 fallback 填
6. 任一 HTTP 路径返回 401 / 403 → 调 Claude CLI 刷新当前来源，重新读取凭据并完整重试一次
7. 如果刷新后 token 没变，跳过这个来源，避免用同一个失效 token 立刻重试

## OAuth 冷却阶梯

仅针对 OAuth 端点，不影响主轮询节奏。

| 连续 429 次数 | 冷却时长        |
| ------------- | --------------- |
| 1             | 5 分钟          |
| 2             | 15 分钟         |
| 3             | 30 分钟         |
| 4+            | 60 分钟（封顶） |

OAuth 返回正常数据 → 冷却立即清除，streak 衰减 1 级（不清零）。这样在 429/成功交替的场景下 streak 会停在能让端点真正承受的档位，探测节奏自然收敛，WARN 日志也随之变稀直至消失。

## 错误处理

| 状态                   | 行为                                      |
| ---------------------- | ----------------------------------------- |
| 凭据过期               | 后台运行 Claude CLI 刷新并重新读取凭据    |
| 401 / 403（任一端点）  | 刷新当前凭据后完整重试一次；仍失败才提示重新登录 |
| OAuth 429              | 进入冷却，本次走 fallback                 |
| fallback 也失败        | 返回上一次错误描述                        |
| 凭据文件缺失或解析失败 | 返回明确错误                              |

代码注释保持英文，并尽量只用一两句话说明目的。

认证错误进入暂停态后会弹出一次重新登录通知。普通定时器只检查凭据签名（Windows 文件存在/大小/mtime，运行中 WSL 的 `stat` 结果）。签名变化后才恢复真实 API 轮询；菜单"立即刷新"会强制尝试一次。

## 诊断日志

Claude 刷新链路会把关键状态写入 `%TEMP%\JeekTokenPlanUsage.log`，文件超过 512KB 时轮转为 `.old`。日志只记录端点状态码、进程退出码/超时、凭据来源类型、WSL distro 名和解析错误，不记录 access token、请求 payload 或凭据文件内容。

## 轮询节奏

[TrayApplicationContext.cs](../TrayApplicationContext.cs) 控制：

- 基础间隔由托盘菜单"刷新间隔"统一决定，三家共用：1 / 5 / 10 / 30 / 60 分钟（默认 5）
- 错误退避（**仅 Claude**）：snapshot 整体失败时 2^(retry-1) 指数退避，封顶 1 小时
- 活跃使用加速（**仅 Claude**）：如果用量相比上次成功轮询增长，后续短时间按 2 分钟间隔轮询
- 空闲/锁屏暂停（**仅 Claude**）：用户 5 分钟无输入或工作站锁定时，定时轮询暂停；菜单"立即刷新"仍会强制查询
- 重置对齐（**仅 Claude**）：如果 reset 即将在当前轮询间隔内发生，下次轮询对齐到 reset 后约 5 秒；如果返回的 reset 时间已过去，每 10 秒再查一次，直到 API 翻到新窗口
- OAuth 429 冷却（**仅 Claude**）：优先使用服务端 `Retry-After`，没有该响应头时使用 5 / 15 / 30 / 60 分钟阶梯

## 成本估算

正常情况：**0 tokens**（只走 OAuth）

最坏情况（OAuth 持续故障、每次都 fallback）：

| 间隔    | 5h 内调用数 | 估算 token 消耗 |
| ------- | ----------- | --------------- |
| 1 分钟  | 300         | ~2k-3k          |
| 5 分钟  | 60          | ~400-600        |
| 10 分钟 | 30          | ~200-300        |

Haiku 单次约 5-10 input + 1 output token。即便是最坏情况，相对 Pro/Max 5h 窗口（数十万 token 量级）的占用也远小于 1%。
