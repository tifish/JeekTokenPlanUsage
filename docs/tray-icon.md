# 托盘图标实现

实现：[TrayIcon.cs](../TrayIcon.cs)，使用方：[TrayApplicationContext.cs](../TrayApplicationContext.cs)

## 为什么不用 WinForms `NotifyIcon`

本程序最多会在系统托盘里同时显示 7 个图标（1 个 anchor + 3 个 provider × 2 个时间窗口）。WinForms 的 `NotifyIcon` 在多图标场景下有一个致命问题：**Windows 11 的拖动重排（drag-to-reorder）会失效甚至错乱**。

根因：

- Shell 持久化图标位置时使用 `(可执行文件路径 + 在数据结构里的位置索引)` 作为 key。
- 同一个 exe 拉起多个 `NotifyIcon` 时，shell 无法稳定区分它们 —— 重排时要么没反应，要么把图标互相调换。
- 解决方案是给每个图标传入 `NIF_GUID + guidItem`，让每个图标拥有独立、稳定、跨进程重启都不变的身份标识。`NotifyIcon` 没有暴露这个字段，必须直接走 P/Invoke。

## 关键设计

### 每个图标一个固定 GUID

[TrayApplicationContext.cs:29-35](../TrayApplicationContext.cs#L29-L35) 集中声明了 7 个硬编码 GUID。一旦发布就**不能再改** —— 改了之后老用户托盘里的图标位置和 "始终显示" 设置会全部丢失，因为 shell 把这些状态绑在 GUID 上。

### `TaskbarCreated` 重注册

Explorer 崩溃重启或注销重登后，原先注册的图标全部失效。Shell 会向所有顶层窗口广播 `TaskbarCreated` 消息，我们监听后重新 `NIM_ADD`（[TrayIcon.cs:208-213](../TrayIcon.cs#L208-L213)）。

### `NIM_ADD` 失败时的恢复路径

如果 GUID 仍被旧的、已死掉的进程实例占用（例如崩溃后没走 `Dispose`），`NIM_ADD` 会失败。此时先用同一 GUID 发一次 `NIM_DELETE` 强制释放，再重试 `NIM_ADD`（[TrayIcon.cs:145-157](../TrayIcon.cs#L145-L157)）。这是微软文档给出的标准恢复方式。

### Version 4 回调协议

注册时调用 `NIM_SETVERSION` 切到 `NOTIFYICON_VERSION_4`（[TrayIcon.cs:160-162](../TrayIcon.cs#L160-L162)）。区别于老协议：

- 鼠标/键盘事件类型放在 `lParam` 的低 16 位（不是 `wParam`）。
- 提供 `WM_CONTEXTMENU`，键盘触发的菜单（Shift+F10 / 菜单键）也能正常工作。

### 右键菜单的 `SetForegroundWindow`

弹出 `ContextMenuStrip` 前必须先 `SetForegroundWindow` 自己的隐藏消息窗口（[TrayIcon.cs:197-206](../TrayIcon.cs#L197-L206)），否则用户点菜单外部时菜单不会消失，会卡在屏幕上。这是 shell 托盘菜单的历史遗留问题。

## 隐藏消息窗口

P/Invoke 版的 `Shell_NotifyIcon` 需要一个 `HWND` 来接收回调。我们用 `NativeWindow` 创建一个不可见的消息窗口（`MessageWindow`，[TrayIcon.cs:223-241](../TrayIcon.cs#L223-L241)），只用来收 `WM_TRAYMESSAGE` 和 `WM_TASKBARCREATED`。
