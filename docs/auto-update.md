# 自动更新实现

实现：[AutoUpdate.cs](../JeekTokenPlanUsage/AutoUpdate.cs)（[JeekTools.NET](../JeekTools.NET) `AutoUpdater` 的薄封装）、[bin/AutoUpdate.ps1](../bin/AutoUpdate.ps1)；调度方：[TrayApplicationContext.cs](../JeekTokenPlanUsage/TrayApplicationContext.cs)；CI：[.github/workflows/build-and-release.yml](../.github/workflows/build-and-release.yml)。

托盘程序默认启用，可在右键菜单「自动更新 / Auto-update」开关。启动 5 秒后做一次检查，之后每小时一次；菜单「检查更新 / Check for updates」可手动触发。发现新版本会弹一次托盘通知，**应用内**完成下载与解压暂存，再退出进程、由 PowerShell 脚本完成文件替换与重启。

## 版本身份：git 提交数

唯一身份是 `git rev-list --count HEAD`——main 分支总提交数（一个单调递增的整数，如 234），人眼可读、天然可排序、零手工维护。

**注入路径**完全发生在 CI 内：

- workflow 算出 `count`，**同时**写两个地方
  - `version.txt`（独立 release asset，约 4 字节）作远端真理
  - `dotnet publish /p:Version=<count>`，让 SDK 把它补成 `AssemblyVersion=<count>.0.0.0`
- 运行时反射读 `Assembly.Version.Major`（`AutoUpdater` 默认的 `GetLocalVersion`）

**比较语义**严格 `remote > local`。force-push 让远端 count 倒退时也不会让客户端"降级回退"。

### 哨兵保护本地 dev 构建

csproj 默认 `<Version>0.0.0.0</Version>`。本地构建 `Major=0`，低于 `MinimumValidLocalVersion`，`AutoUpdater` 返回 `Failed("local version unavailable (dev build?)")`，**绝不**会用 release 覆盖开发者的工作树。另外 Debug 构建直接传 `Disabled = true`，检查与安装全部禁用。

## 组件分工

`JeekTools.AutoUpdater` 负责（见 [JeekTools.NET/AutoUpdater.cs](../JeekTools.NET/AutoUpdater.cs)）：

- `HasUpdateAsync()`：并发竞速全部镜像的 version.txt（每请求 5 秒超时），第一个成功解析的镜像胜出并成为首选下载源；返回 `Available / UpToDate / Failed` 三态，失败原因在 `FailureReason`。
- `DownloadAndStageAsync()`：逐镜像下载 zip（30 秒无数据或 10 秒窗口平均低于 0.5 MB/s 时换下一个镜像，最后一个镜像不限速），解压到 `%TEMP%\JeekTokenPlanUsage-update\package` 并校验主 exe 存在。失败不影响正在运行的程序。
- `LaunchInstall(stagedDir)`：启动 exe 同目录的 `AutoUpdate.ps1` 并传入暂存目录。

应用侧只做（[AutoUpdate.cs](../JeekTokenPlanUsage/AutoUpdate.cs)）：

- 构造 `AutoUpdaterOptions`（AppExeName、ReleaseZipUrl、VersionTxtUrl、UserAgent、Debug 下 Disabled）。
- 决定检查时机（启动 + 每小时 + 手动），弹 toast，`DownloadAndInstallAsync` 成功后 `Application.Exit()`。
- `DisableMirrorDownload` 设置为 true 时，下载 URL 列表只传 github.com 直连。

## 端到端流程

```
┌─ 启动后 5 秒 + 每小时定时 / 手动菜单
└─ CheckForUpdatesAsync(manual)
   ├─ AutoUpdate.HasUpdateAsync()            → Available / UpToDate / Failed
   ├─ Available:
   │  ├─ ShowUpdateToast + Task.Delay(800)
   │  └─ AutoUpdate.DownloadAndInstallAsync(disableMirror)
   │     ├─ AutoUpdater.DownloadAndStageAsync()   // 应用内下载+解压+校验
   │     ├─ AutoUpdater.LaunchInstall(stagedDir)  // 启动 AutoUpdate.ps1
   │     └─ Application.Exit()
   └─ PowerShell 脚本（AutoUpdate.ps1 <stagedDir>）
      ├─ WaitForExit 旧进程（释放文件锁）
      ├─ 清空安装目录（保留 Config 与脚本自身），再从暂存目录整包复制
      ├─ 清理暂存目录
      └─ Start-Process 新 exe
```

### 替换自身必须经过外部脚本

.NET 进程持有自己的 exe/dll 文件锁，运行期间无法替换。脚本归属 `bin/`（直接入库，不放源码目录）：`.gitignore` 用 `bin/*` + `!bin/AutoUpdate.ps1` 等单文件例外；`Publish.cmd` 清理 bin 时用 `$keep` 列表跳过这些已入库脚本。

## CI 工作流

[build-and-release.yml](../.github/workflows/build-and-release.yml) 要点：

- `actions/checkout` 必须配 `fetch-depth: 0`，否则 `git rev-list --count` 永远是 1。
- `version.txt` 只在 runner 临时工作区生成并上传，不进仓库历史；本地 `.gitignore` 兜底。
- pdb 在打包前移除；release tag 固定 `latest_release`，每次发布先 delete 再 create。
- 发布类步骤带 `if: github.event_name == 'push'` 门控，PR 只构建不发布。

## 配置项

[AppSettings.cs](../JeekTokenPlanUsage/AppSettings.cs)：`AutoUpdate`（默认 true）、`DisableMirrorDownload`（默认 false，海外用户直连逃生开关）。均为可漫游设置。

## 日志

所有检查与更新动作写到 `%LocalAppData%\JeekTokenPlanUsage\Logs\`（LogManager/ZLogger，滚动保留 7 天）。`AutoUpdater` 自身也通过 `LogManager` 记录镜像切换与下载进度摘要。
