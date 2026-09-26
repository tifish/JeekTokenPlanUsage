# 自动更新

版本号使用 git commit 数量；本地开发版本为 `0.0.0.0`，Debug 禁止检查和安装。CI 使用 Release build，MCP 适配器单独做单文件 publish。

应用复用 JeekTools.NET `AutoUpdater`：显式将 `UpdateRoot` 设为 `%LocalAppData%\JeekTokenPlanUsage\Update`，缓存包含 `version.txt` 和 `package`。库负责镜像选择、慢速切换、下载校验和按版本复用缓存。

启动后 5 秒、每小时、手动菜单及产品 MCP 共用一套流程：

1. 检查版本，直接后台下载或复用仍有效的缓存。
2. 包就绪后显示一次非模态确认，选择立即重启安装或稍后。
3. 选择稍后或关闭窗口，保留缓存；下次检查重新验证版本后再询问。
4. 确认后先保存设置；保存失败则取消安装。启动更新脚本成功后退出。
5. 脚本只等待安装路径匹配的主程序，使用 robocopy 清理旧文件并保留 Config、Logs 和脚本自身。成功后清理整个 Update 根目录并重启；失败时尽量启动现有程序。

产品 MCP 的 `check_update` 立即返回操作 id 和状态，通过 `get_ui_state.operation` 轮询。需要决定时返回 `awaiting_user`，只能在 GUI 确认；旧参数 `allowUpdateLaunch` 保留兼容但不能绕过确认。

Debug 可通过对象图调用 `Context.PreviewUpdateConfirmation` 检查真实确认窗口，再读取 `Context._operation`；Debug 更新器始终拒绝实际安装。
