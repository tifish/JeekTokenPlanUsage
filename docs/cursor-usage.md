# Cursor 用量获取

实现：[CursorUsageProvider.cs](../CursorUsageProvider.cs)

## 数据来源

两个端点**并行**调用：

### 1. 当前周期用量（主数据）

```
POST https://cursor.com/api/dashboard/get-current-period-usage
Cookie: WorkosCursorSessionToken=<userId>%3A%3A<jwt>
Origin: https://cursor.com
Referer: https://cursor.com/dashboard?tab=spending
Content-Type: application/json

{}
```

响应里关心 `planUsage` 对象：

```json
{
  "planUsage": {
    "autoPercentUsed": 28.5,
    "apiPercentUsed":  5.2,
    ...
  }
}
```

- `autoPercentUsed` → Auto + Composer 池（Cursor 2.0 定价的主要池）
- `apiPercentUsed` → API 池（API key 调用配额）
- 都是 0..100 百分比

### 2. 月度重置锚点（best effort）

```
GET https://cursor.com/api/usage?user=<userId>
Cookie: WorkosCursorSessionToken=<userId>%3A%3A<jwt>
```

只读 `startOfMonth` 字段（ISO 8601 字符串），是订阅锚定日期。

下次重置时间 = 严格大于当前时间的第一个月度周年日。[CursorUsageProvider.cs:111-115](../CursorUsageProvider.cs#L111-L115)

主端点 `get-current-period-usage` 本身不返回 reset 时间，所以单独取这个锚点来推算。

如果第二个调用失败（网络错误、解析失败等），reset 字段返回 null，不影响主用量数据展示。

### 为什么不用旧版 `/api/usage`

旧版只有 GPT-4 / API tokens 这种细粒度维度，没暴露 Cursor 2.0 引入的 Auto+Composer 池。dashboard 实际用的是 `get-current-period-usage`，所以我们也用它。

## 认证：WorkosCursorSessionToken cookie

格式：

```
WorkosCursorSessionToken=<userId>%3A%3A<jwt>
```

其中：
- `%3A%3A` 是 URL 编码的 `::`
- `<userId>` 从 JWT payload 的 `sub` 字段提取，格式 `auth0|USERID`，取 `|` 之后的部分
- `<jwt>` 整体来自 Cursor IDE 的本地数据库

[CursorUsageProvider.cs:155-164](../CursorUsageProvider.cs#L155-L164) 是组装逻辑。

## 凭据来源：Cursor 的 state.vscdb

路径：`%APPDATA%\Cursor\User\globalStorage\state.vscdb`（SQLite）

读取：

```sql
SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken'
```

返回的 `value` 直接是 JWT 字符串。

注意几点：
- 用 `SqliteOpenMode.ReadOnly` + `SqliteCacheMode.Shared` 打开，避免与正在运行的 Cursor IDE 抢锁
- 文件不存在 → "未找到 Cursor 数据库 (请先登录 Cursor)"
- value 为空 → 用户在 Cursor 里登录态丢了，提示重新登录

## JWT 解析

只解 payload（中间段），不验签：

1. 按 `.` 切分，至少要有 2 段
2. payload 段 Base64Url 解码（`-` → `+`, `_` → `/`, 补 `=`）
3. 取 `sub`，按 `|` 切出后半段作为 userId

[CursorUsageProvider.cs:197-241](../CursorUsageProvider.cs#L197-L241)

## 响应映射

[CursorUsageProvider.cs:123-143](../CursorUsageProvider.cs#L123-L143)

| 程序内字段 | 来源 | 显示 |
| --------- | ---- | ---- |
| `FiveHour` | `planUsage.autoPercentUsed` | 主图标，标签 `Auto` |
| `Weekly`   | `planUsage.apiPercentUsed`  | 次图标，标签 `API` |

注：Cursor 的两个池都按月度计费周期重置，所以两个 `UsageMetric` 共用同一个 reset 时间。变量名 `FiveHour` / `Weekly` 是历史遗留（最初为 Claude 设计），并非真的 5 小时/周窗口。

## 错误处理

| 状态 | 行为 |
| ---- | ---- |
| 401 / 403 | "Token 失效，请在 Cursor 中重新登录" |
| 429 | "接口限流 (429)" |
| 其他非 2xx | "HTTP <code>" |
| 网络错误 | "网络错误: <msg>" |
| 响应缺 `planUsage` | "响应缺少 planUsage" |
| SQLite 读取失败 | "读取 Cursor 凭据失败: <msg>" |
| JWT 格式异常 | 明确错误（缺 sub、解码失败等） |

## 轮询节奏

`CursorInterval = 2 分钟`，固定不变。

- 两个端点都便宜，无配额消耗
- dashboard 端点本身就是浏览器调的，限流阈值宽
- 没有阶梯退避，仅上层 `_cursorBusy` 互斥防止重叠

## User-Agent 伪装

构造 `HttpClient` 时使用 Chrome 桌面 UA，避免某些反爬策略对非浏览器 UA 的策略差异。[CursorUsageProvider.cs:32-33](../CursorUsageProvider.cs#L32-L33)
