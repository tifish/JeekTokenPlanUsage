# 任务栏组件实现

实现：[TaskbarWidget.cs](../TaskbarWidget.cs)，使用方：[TrayApplicationContext.cs](../TrayApplicationContext.cs)。可在右键菜单「在任务栏显示组件 / Show widget on taskbar」开关，默认关闭。

参考 [CodeZeno/Claude-Code-Usage-Monitor](https://github.com/CodeZeno/Claude-Code-Usage-Monitor)：在任务栏上（时钟/托盘左侧）显示每个 provider 两个时间窗口的进度条 + 百分比 + 重置剩余时间。每格形如 `CL 5h ▓░ 24%   3h`（剩余时间只显示最大单位以省空间）。剩余时间用 1 秒定时器在分钟变化时重绘以保持实时（见 [`OnTick`](../TaskbarWidget.cs)）。

组件内所有文字（窗口标签、百分比、剩余时间、错误占位）都使用对应 provider 的主色，便于快速区分 Claude / Codex / Cursor。

## 为什么不用 Windows AppBar

AppBar（`SHAppBarMessage` + `ABM_NEW/SETPOS`）会在屏幕某条边上**预留一整条**空间，对一个小组件来说太占地方，而且与本就是 AppBar 的任务栏会互相挤位置。参考项目和本实现都不用 AppBar，而是把窗口**嵌进任务栏本体**。

## 关键设计

### 嵌入任务栏（`SetParent` 进 `Shell_TrayWnd`）

[TaskbarWidget.cs `EnsureEmbedded`](../TaskbarWidget.cs)：

- 找到任务栏窗口 `Shell_TrayWnd`，把我们的窗口样式从 `WS_POPUP` 改成 `WS_CHILD | WS_CLIPSIBLINGS`，再 `SetParent` 成它的子窗口。
- 这样组件随任务栏一起行为：全屏时隐藏、自动隐藏时跟着滑出、z-order 正确。
- 跨进程 `SetParent`（任务栏属于 explorer.exe）是允许的；窗口消息仍在我们自己的线程上处理。

### 相对托盘区定位

[`Position`](../TaskbarWidget.cs)：取任务栏矩形，再用 `FindWindowEx` 找到 `TrayNotifyWnd`（托盘通知区）的左边界，把组件放在它左侧，留一个可拖动调整的 `offset` 间距。嵌入时坐标相对父窗口（任务栏），回退模式下用屏幕坐标。

一个 1 秒的 `Timer` 周期性重定位，跟随任务栏移动 / 托盘宽度变化（拖动期间跳过，避免和用户抢）。

### 分层窗口渲染（`UpdateLayeredWindow`）

窗口带 `WS_EX_LAYERED`，从不走 `WM_PAINT`。每次刷新用 GDI+ 画到 32bpp ARGB 透明位图，再拷进自建的 `CreateDIBSection`（逐像素预乘 alpha）并 `UpdateLayeredWindow(ULW_ALPHA)` 推上去 —— `GetHbitmap` 会丢掉 alpha 通道导致整窗透明，所以必须自己持有像素。背景填不透明的实色（贴近任务栏底色：深 `#202020` / 浅 `#F3F3F3`）。`pptDst` 传 `null`，位置由 `MoveWindow` 单独控制。

配色跟随**系统（Windows）主题**而非应用主题：组件画在任务栏上，任务栏底色由 `SystemUsesLightTheme`（注册表 `…\Themes\Personalize`）决定，可能与应用主题不同，所以读它而不是 `Application.IsDarkModeEnabled`。

主题 / DPI 切换**即时响应**：[TrayApplicationContext](../TrayApplicationContext.cs) 里的 `SystemChangeListener`（一个顶层隐藏窗口）监听系统广播 `WM_SETTINGCHANGE("ImmersiveColorSet")` / `WM_THEMECHANGED` / `WM_DISPLAYCHANGE` / `WM_DPICHANGED`，收到后立刻通知组件重绘（组件是任务栏的子窗口、可能收不到广播，必须由顶层窗口代收）。每秒的 `OnTick` 再检测任务栏高度和主题作为兜底。

### 拖动调整位置

`WS_EX_NOACTIVATE` 子窗口仍能收到鼠标消息。左键按下 `SetCapture`，移动超过阈值才算拖动并实时更新 `offset` 重定位；松开时若发生过拖动就持久化 `offset`，否则当作左键点击（弹出详情）。右键经隐藏的顶层 owner 窗口 `SetForegroundWindow` 后弹出共享的右键菜单（子/不激活窗口无法自己取得前台，这点和托盘图标同理）。

### Explorer 重启重嵌

监听 `TaskbarCreated` 广播（[`OnTaskbarCreated`](../TaskbarWidget.cs)），explorer 崩溃重启后重新嵌入并重绘。

### 回退：顶层置顶窗口

找不到 `Shell_TrayWnd` 时不嵌入，改为 `WS_EX_TOPMOST` 顶层窗口贴在托盘区原位（[`EnsureEmbedded`](../TaskbarWidget.cs) 的 else 分支）。

## 与托盘图标的关系

组件与托盘图标相互独立，可同时开启，也可把「图标显示」设为「不显示」只留组件。数据来源同一套 provider 快照，由 [TrayApplicationContext.UpdateWidget](../TrayApplicationContext.cs) 在每次刷新后推送。
