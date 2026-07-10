# Grok 用量获取

实现：[GrokUsageProvider.cs](../JeekTokenPlanUsage/GrokUsageProvider.cs)

## 数据来源

两个端点**并行**调用（CLI chat-proxy 计费接口）：

### 1. SuperGrok 周池（主窗口）

```
GET https://cli-chat-proxy.grok.com/v1/billing?format=credits
Authorization: Bearer <access_token>
x-userid: <user_id>
x-grok-client-version: 0.2.93
```

响应关心 `config`：

```json
{
  "config": {
    "creditUsagePercent": 12.5,
    "currentPeriod": {
      "type": "USAGE_PERIOD_TYPE_WEEKLY",
      "start": "2026-07-09T06:53:44.448502+00:00",
      "end": "2026-07-16T06:53:44.448502+00:00"
    },
    "onDemandCap": { "val": 0 },
    "onDemandUsed": { "val": 0 },
    "prepaidBalance": { "val": 0 },
    "isUnifiedBillingUser": true,
    "billingPeriodStart": "...",
    "billingPeriodEnd": "..."
  }
}
```

- `creditUsagePercent` → 0..100 百分比（服务端可能在用量为 0 时省略，按 0 处理）
- `currentPeriod.end` → 周池重置时间

### 2. 月度 included credits（次窗口）

```
GET https://cli-chat-proxy.grok.com/v1/billing
Authorization: Bearer <access_token>
x-userid: <user_id>
x-grok-client-version: 0.2.93
```

```json
{
  "config": {
    "monthlyLimit": { "val": 150000 },
    "used": { "val": 171 },
    "billingPeriodStart": "2026-07-01T00:00:00+00:00",
    "billingPeriodEnd": "2026-08-01T00:00:00+00:00",
    "history": [ ... ]
  }
}
```

- 利用率 = `used.val / monthlyLimit.val * 100`
- 重置时间 = `billingPeriodEnd`
- 数值字段多为 `{"val": N}` 包装，解析时兼容裸 number

### 为什么不用 grok.com/rest/rate-limits

该端点面向网页会话 cookie，对 Grok CLI 的 OAuth2 token 返回：

```
Action cannot be performed by OAuth2 token users
```

CLI 自己走的是 `cli-chat-proxy.grok.com/v1/billing`，与日志里的 `billing: fetched credits config` 一致。

## 凭据来源

路径：`%USERPROFILE%\.grok\auth.json`（Grok CLI / `grok login` 写入）

结构（scope key → 会话）：

```json
{
  "https://auth.x.ai::<client_id>": {
    "key": "<access_token>",
    "refresh_token": "...",
    "expires_at": "2026-07-10T08:08:50.159070900Z",
    "user_id": "...",
    "oidc_client_id": "...",
    "auth_mode": "oidc"
  }
}
```

选取规则：取 `key` 非空且 `expires_at` 最晚的一条（最近刷新的会话）。

## Token 刷新

优先级：

1. 若本地 `expires_at` 已过期（提前 1 分钟），先刷新再请求
2. 用量端点 401 / 403 后再刷新并重试一次

刷新路径：

1. **OIDC silent refresh**（优先）  
   `POST https://auth.x.ai/oauth2/token`  
   `grant_type=refresh_token&refresh_token=...&client_id=...`  
   成功后写回 `auth.json` 的 `key` / `expires_at`（以及轮换后的 `refresh_token`）
2. **CLI fallback**  
   后台运行 `grok models`（优先 `~/.grok/bin/grok.exe`），让 CLI 自己轮换 token，再重新读 `auth.json`

若刷新后 token 未变或仍 401，返回重新登录错误并暂停真实轮询。

## 响应映射

| 程序内字段 | 来源 | 显示 |
| ---------- | ---- | ---- |
| `FiveHour` | `format=credits` 周池 | 主图标，标签 `7d` |
| `Weekly`   | 默认 billing 月度 used/limit | 次图标，标签 `Mo` |

变量名 `FiveHour` / `Weekly` 是历史遗留（最初为 Claude 设计），Grok 实际是周池 + 月度 credits。

## 错误处理

| 状态 | 行为 |
| ---- | ---- |
| 401 / 403 | OIDC / CLI 刷新后重试一次；仍失败才提示 `grok login` 并暂停 |
| 429 | "接口限流 (429)" |
| 其他非 2xx | "HTTP \<code\>" |
| 网络错误 | "网络错误: \<msg\>" |
| 两端都缺 config | "响应缺少 billing config" |
| auth.json 缺失 / 损坏 | 明确错误 |

认证错误进入暂停态后会弹出一次重新登录通知；普通定时器只检查 `auth.json` 签名（存在/大小/mtime），签名变化后恢复真实轮询；菜单「立即刷新」会强制尝试一次。

## 轮询节奏

基础间隔由托盘菜单「刷新间隔」统一控制，各家共用：1 / 5 / 10 / 30 / 60 分钟（默认 5）。

- 计费端点不消耗模型配额，随基础间隔走即可
- 错误时几何退避（与 Codex 相同），成功后回到基础间隔
- 空闲/锁屏暂停（与 Claude/Codex 相同）：用户 5 分钟无输入或工作站锁定时定时轮询暂停；菜单「立即刷新」仍强制查询
- 上层 `_grokBusy` 互斥防止重叠

## 诊断日志

写入 `%TEMP%\JeekTokenPlanUsage.log`，只记录 HTTP 状态、OIDC/CLI 刷新结果、auth 路径状态，不记录 access token 或 refresh token。
