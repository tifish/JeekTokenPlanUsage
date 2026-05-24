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

{ "model": "claude-3-haiku-20240307", "max_tokens": 1, "messages": [{"role":"user","content":"."}] }
```

不读 body，只读响应头：

- `anthropic-ratelimit-unified-5h-utilization` / `-5h-reset`（unix 秒）
- `anthropic-ratelimit-unified-7d-utilization` / `-7d-reset`

无论响应 2xx 还是 429，这组头都会返回，所以即使被限流也能拿到数据。模型列表 [ClaudeUsageProvider.cs:21-25](../ClaudeUsageProvider.cs#L21-L25) 是回退链，第一个能拿到 rate-limit 头就停。

## 凭据来源

`%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth.accessToken`

## 调用流程

[ClaudeUsageProvider.cs:62-103](../ClaudeUsageProvider.cs#L62-L103)

1. 不在冷却期 → 调 OAuth 端点；冷却期内 → 跳过，标记为 `Skipped`
2. OAuth 返回 429 → 标记 `RateLimited`，按阶梯设置下次冷却
3. OAuth 完全成功（两个窗口的 utilization+reset 都有）→ 直接返回，不调 fallback
4. 否则调 messages fallback，合并数据：utilization 以 OAuth 为准，缺失的 reset 用 fallback 填

## OAuth 冷却阶梯

仅针对 OAuth 端点，不影响主轮询节奏。

| 连续 429 次数 | 冷却时长 |
| ------------ | ------- |
| 1            | 5 分钟  |
| 2            | 15 分钟 |
| 3            | 30 分钟 |
| 4+           | 60 分钟（封顶） |

OAuth 一旦返回正常数据 → streak 清零、冷却清除。

## 错误处理

| 状态 | 行为 |
| ---- | ---- |
| 401 / 403（任一端点） | "Token 失效，请在 Claude Code 中重新登录" |
| OAuth 429 | 进入冷却，本次走 fallback |
| fallback 也失败 | 返回上一次错误描述 |
| 凭据文件缺失或解析失败 | 返回明确错误 |

## 轮询节奏

[TrayApplicationContext.cs](../TrayApplicationContext.cs) 控制：

- 允许的间隔：1 / 5 / 10 / 30 / 60 分钟（默认 5）
- 错误退避：snapshot 整体失败时 2^(retry-1) 指数退避，封顶 1 小时
- 重置后快速回探：如果返回的 reset 时间已过去，下一次 60 秒后再查（API 还没翻新窗口）

## 成本估算

正常情况：**0 tokens**（只走 OAuth）

最坏情况（OAuth 持续故障、每次都 fallback）：

| 间隔 | 5h 内调用数 | 估算 token 消耗 |
| ---- | --------- | -------------- |
| 1 分钟 | 300 | ~2k-3k |
| 5 分钟 | 60 | ~400-600 |
| 10 分钟 | 30 | ~200-300 |

Haiku 单次约 5-10 input + 1 output token。即便是最坏情况，相对 Pro/Max 5h 窗口（数十万 token 量级）的占用也远小于 1%。
