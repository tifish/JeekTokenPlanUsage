# 自动更新实现

实现：[AutoUpdate.cs](../AutoUpdate.cs)、[GitHubMirrors.cs](../GitHubMirrors.cs)、[bin/AutoUpdate.ps1](../bin/AutoUpdate.ps1)；调度方：[TrayApplicationContext.cs](../TrayApplicationContext.cs)；CI：[.github/workflows/dotnet-desktop.yml](../.github/workflows/dotnet-desktop.yml)。

托盘程序默认启用，可在右键菜单「自动更新 / Auto-update」开关。启动 5 秒后做一次检查，之后每小时一次；菜单「检查更新 / Check for updates」可手动触发。发现新版本会弹一次托盘通知，然后退出当前进程、由 PowerShell 脚本完成替换与重启。

## 版本身份：git 提交数

唯一身份是 `git rev-list --count HEAD`——main 分支总提交数（一个单调递增的整数，如 234），人眼可读、天然可排序、零手工维护。

**注入路径**完全发生在 CI 内：

- workflow 算出 `count`，**同时**写两个地方
  - `version.txt`（独立 release asset，约 4 字节）作远端真理
  - `dotnet publish /p:Version=<count>`，让 SDK 把它补成 `AssemblyVersion=<count>.0.0.0`（[`/p:Version` step](../.github/workflows/dotnet-desktop.yml)）

- 运行时反射读 `Assembly.GetExecutingAssembly().GetName().Version.Major`（[AutoUpdate.cs `ReadLocalCommitCount`](../AutoUpdate.cs)）

为什么把 count 放在 Major 而不是 Minor/Build/Revision：单数字 `<Version>234</Version>` 让 .NET 自动补全为 `234.0.0.0`，无 `1.0.0.` 前缀，调试时看到的版本号就是干净的 count。

**比较语义**严格 `remote > local`。force-push 让远端 count 倒退时也不会让客户端"降级回退"。

### 哨兵保护本地 dev 构建

csproj 默认 `<Version>0</Version>`（[JeekTokenPlanUsage.csproj](../JeekTokenPlanUsage.csproj)）。本地 `dotnet build` 不带 `/p:Version=...` → `AssemblyVersion=0.0.0.0` → `Major=0`，AutoUpdate 检测到后返回 `Failed("local version unavailable")`，**绝不**会用 release 把开发者未提交的工作树覆盖掉。CI 用 `/p:Version=<count>` 覆盖这个 0。

## 三态结果

[`UpdateCheckOutcome`](../AutoUpdate.cs) 区分三种结局，避免把网络失败误报成"已是最新版本"：

| 状态 | 含义 | 用户看到（auto） | 用户看到（manual） |
|---|---|---|---|
| `Available` | 远端 > 本地 | toast「发现新版本」+ 自动重启更新 | 同左 |
| `UpToDate` | 本地与远端都读到且本地 ≥ 远端 | 静默 | toast「已是最新版本」 |
| `Failed` | 任何环节没完成 | 静默（仅记日志） | toast「更新失败：&lt;reason&gt;」 |

`Failed` 的具体原因写在 [`AutoUpdate.FailureReason`](../AutoUpdate.cs) 属性，由 `Fail(reason)` 辅助统一设置 + 写 [DiagnosticLog](../DiagnosticLog.cs)。常见值：

- `no reachable mirror` — 三个镜像并行探测都失败
- `empty version.txt from <url>` — 远端拿到的内容是空
- `version.txt did not contain a positive integer: '<text>'` — 远端不是数字
- `local version unavailable (dev build?)` — 本地 dev 构建命中哨兵
- `exception: <message>` — 其他未预期异常

auto 模式所有非 `Available` 都静默：托盘程序长期运行，不应该因为偶发网络问题持续打扰用户。

## 端到端流程

```
┌─ 启动后 5 秒（避开启动期网络抖动）+ 每小时定时
└─ CheckForUpdatesAsync(manual=false)
   │
   ├─ AutoUpdate.HasUpdateAsync(disableMirror)
   │  │
   │  ├─ 探测镜像  GitHubMirrors.GetFastestMirrorAsync(zipUrl)
   │  │           直连 / ghfast.top / gh-proxy.com 并行 GET 0..100KB Range
   │  │           最快返回 200/206 胜出，索引缓存在静态字段
   │  │
   │  ├─ 下载 version.txt（同一镜像，索引缓存 O(1)）
   │  │           GitHubMirrors.DownloadTextAsync(versionUrl)
   │  │
   │  ├─ int.TryParse(text) → RemoteCommitCount
   │  ├─ Assembly.Version.Major → LocalCommitCount
   │  └─ return remote > local ? Available : UpToDate （任一步出错 → Failed）
   │
   ├─ Available
   │  ├─ ShowUpdateToast("发现新版本", "即将重启以安装版本 235…")
   │  ├─ Task.Delay(800)  // 让 toast 真正显示出来
   │  └─ AutoUpdate.LaunchUpdate()
   │     ├─ Process.Start powershell -File AutoUpdate.ps1 <zipUrl>（隐藏窗口）
   │     └─ Application.Exit()  // 释放 mutex、托盘图标
   │
   └─ PowerShell 脚本（Application.Exit 后接管）
      ├─ WaitForExit 旧 exe（释放文件锁）
      ├─ WebClient.DownloadFile zip → %TEMP%
      ├─ 删旧 *.dll *.pdb *.deps.json *.runtimeconfig.json 与 culture 子目录
      ├─ Expand-Archive zip → $PSScriptRoot（覆盖）
      └─ Start-Process 新 exe
```

## 关键设计

### 替换自身必须经过外部脚本

.NET 进程持有自己的 exe/dll 文件锁，运行期间无法替换。所以真正替换文件的执行体是 [bin/AutoUpdate.ps1](../bin/AutoUpdate.ps1)：

- `Application.Exit()` 走完正常生命周期 → `Program.Main` 用 `using var mutex` 自动释放（单实例 mutex）→ 文件锁释放
- 脚本启动后立刻 `Get-Process | WaitForExit` 兜底等待残留进程
- 删旧文件、解压、`Start-Process` 拉起新版

脚本归属 `bin/`（直接入库，不放源码目录），配套：

- `.gitignore` 把 `bin/` 改成 `bin/*` 让单文件例外生效，再加 `!bin/AutoUpdate.ps1`（[.gitignore](../.gitignore)）
- `Publish.cmd` 清理 bin 时用 PowerShell `Where-Object Name -ine 'AutoUpdate.ps1'` 跳过该脚本（[Publish.cmd](../Publish.cmd)）
- csproj 不需要 `<None Update>` 拷贝——脚本天然就在 bin 里

### 镜像选择

[GitHubMirrors.cs](../GitHubMirrors.cs) 内置三个候选：

```
直连              https://github.com/...
ghfast.top        https://ghfast.top/https://github.com/...
gh-proxy.com      https://gh-proxy.com/github.com/...
```

首次调用 `GetFastestMirrorAsync` 时三路并行 `GET` 0..100KB Range，最快返回 200/206 的胜出，**索引缓存在静态字段**；后续 zip 下载与 version.txt 下载复用同一索引，第二次调用 O(1)。`Reset()` 在 `DisableMirrorDownload` 设置切换时清缓存。

`DisableMirrorDownload`（[AppSettings.cs](../AppSettings.cs)）默认关闭，海外用户若觉得镜像反而更慢可打开走直连。

### 时间戳曾经的坑（已抛弃）

早期方案用「远端 HTTP Last-Modified vs 本地 exe `LastWriteTime`」比对。`.zip` 通过 `Compress-Archive` 打包时存的是 DOS 时间（本地时区、无时区信息），`Expand-Archive` 按**用户机器本地时区**解释回 UTC，结果在中国（UTC+8）用户机器上本地 exe 的 `LastWriteTimeUtc` 比真实发布时间小 8 小时，**每次启动都误报"有更新"**。改用 commit count 后版本身份和文件时间彻底解耦。

## CI 工作流

[dotnet-desktop.yml](../.github/workflows/dotnet-desktop.yml) 关键步骤：

```yaml
- name: Compute commit count
  id: ver
  shell: pwsh
  run: |
      $count = (git rev-list --count HEAD).Trim()
      "count=$count" | Out-File -FilePath $env:GITHUB_OUTPUT -Append
      Set-Content -Path version.txt -Value $count -NoNewline -Encoding ascii

- name: Build
  run: dotnet publish --configuration Release ... /p:Version=${{ steps.ver.outputs.count }}

- name: Pack files
  shell: pwsh
  run: |
      Get-ChildItem -Path bin -Filter *.pdb -Recurse | Remove-Item -Force
      Compress-Archive -Path bin\* -DestinationPath JeekTokenPlanUsage.zip -Force

- name: Upload artifacts
  run: |
      gh release upload latest_release JeekTokenPlanUsage.zip --clobber
      gh release upload latest_release version.txt --clobber
```

要点：

- `actions/checkout@v4` 必须配 `fetch-depth: 0`（已有）；否则 `git rev-list` 只看到最近一个 commit，count 永远是 1
- `version.txt` 只在 runner 临时工作区生成，`gh release upload` 后随 runner 销毁；workflow 没有 `git add/commit`，仓库历史不受影响。本地 `.gitignore` 加了 `version.txt` 兜底，防止开发者手工跑相关命令造成未跟踪文件
- pdb 在 pack 前移除，发行包不含调试符号
- release tag 固定为 `latest_release`，每次发布先 `gh release delete --cleanup-tag` 再 `gh release create`，所以 tag 始终指向最新 commit

## 配置项

[AppSettings.cs](../AppSettings.cs) 三个相关字段：

```csharp
public bool AutoUpdate { get; set; } = true;
public bool DisableMirrorDownload { get; set; } = false;
public DateTimeOffset? LastUpdateCheck { get; set; }
```

`AppData\Roaming\JeekTokenPlanUsage\settings.json` 持久化。

托盘右键菜单只暴露「自动更新」开关；镜像设置目前需要直接改 settings.json（属于不常用的逃生开关，没放进 UI 以保持菜单精简）。

## 日志

所有检查与更新动作写到 [DiagnosticLog](../DiagnosticLog.cs)：`%TEMP%\JeekTokenPlanUsage.log`（512KB 自动轮转）。典型条目：

```
2026-05-28 22:45:01.234 +08:00 [INFO] AutoUpdate check started (manual=False)
2026-05-28 22:45:02.012 +08:00 [INFO] AutoUpdate: local=234 remote=235 — update available
2026-05-28 22:45:02.821 +08:00 [INFO] AutoUpdate: launched updater; exiting
```

失败示例：

```
2026-05-28 22:45:01.234 +08:00 [INFO] AutoUpdate check started (manual=True)
2026-05-28 22:45:06.512 +08:00 [WARN] AutoUpdate: no reachable mirror
```

托盘菜单「打开日志文件 / Open log file」直接 ShellExecute 这个路径。

## 不做的事

- **不显示下载进度条**：下载由 PowerShell 在应用退出后完成，没法在原进程显示进度。要做的话只能改成 AutoUpdate.cs 里先 GET 下载完再退出，复杂度上升、还占用 UI 进程内存
- **不拉 release notes**：当前 release body 是 `gh release create --generate-notes` 自动生成，托盘 toast 也只显示 commit count。需要的话可以通过 GitHub Releases API `GET /repos/.../releases/tags/latest_release` 取 `body` 字段
- **不维护 SemVer**：版本身份就是 commit count，足以判断「是否需要更新」。如果将来引入 SDK/library 给第三方消费，再叠一层正式 SemVer 也不影响本机制
- **不在 csproj 调 git**：早期方案考虑过 MSBuild `Target` 调 `git rev-list`，但本地无 git 时会让构建变奇怪，CI 上又是冗余。彻底交给 workflow 注入更干净
