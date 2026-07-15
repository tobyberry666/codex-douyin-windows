# Changelog

本文件遵循团队重构记录惯例（标题含版本与日期）。

## Design Rationale（设计取舍）

- **单文件 C# + `Add-Type` 编译，而非 `dotnet build`**：目标机器为纯 .NET Framework 4.x、无 NuGet、无 `dotnet` SDK。`Add-Type` 可直接从单文件源码编译，零脚手架依赖。代价是代码锁在 **C# 5 语法**、引用只能是 **GAC 程序集**（见 `CLAUDE.md` 约束 A/B）。若未来要上现代 C#，应整套迁移到 `csproj` + NuGet。
- **JSON 解析用 `JavaScriptSerializer`（废弃 API，技术债）**：`System.Text.Json` 在该环境无法被 `Add-Type` 解析。前者由 GAC 自带、零依赖可编译。已加 `#pragma warning disable 0618` + 迁移注释，一旦迁现代 .NET 即换回。
- **窗口匹配「标题 + 进程名」双路**：产品名从 Codex 改为 ChatGPT（合体），仅按标题匹配会失效；进程名 `ChatGPT` 不依赖标题文字，作为兜底。
- **焦点切换（绕过 Windows 前台锁）**：后台托盘程序调 `SetForegroundWindow` 常被 Windows 拒绝（只闪任务栏），根因是前台锁。当前方案用 `AttachThreadInput` 把前台线程输入队列桥接到本线程（DllImport 须从 kernel32 取 `GetCurrentThreadId`、方向为 `AttachThreadInput(foreThread, selfThread, true)`），再 `SetForegroundWindow`+`SetActiveWindow`+`SetFocus`+`SetWindowPos(HWND_TOP)`，最后 `SwitchToThisWindow` 兜底——对浏览器/其他应用/桌面任意前台窗口都生效。曾误弃 `AttachThreadInput`：早期因 `GetCurrentThreadId` 错挂 user32 报「入口点缺失」+ 桥接方向反 + 套了 `SynchronizationContext` 过度工程而整体失效，误判其「不可靠」；现已纠正。**早期「模拟 Alt 键」三步兜底实测仅对浏览器前台有效，对浏览器外的应用/桌面无效，故弃用。**
- **单一事实来源（SSOT）**：Python/PowerShell 版归档于 `legacy/`，C# 为唯一维护实现。

## [1.1.3] - 2026-07-15

### 独占全屏限制与闪烁兜底（承认 OS 硬约束，不再强行抢焦点）

- **现象确认**：当 ChatGPT 完成、用户焦点停在**独占全屏（exclusive fullscreen）**应用（全屏游戏、全屏视频）时，无论如何都拉不回 ChatGPT。这不是代码 bug——Windows 不允许任何进程从独占全屏应用抢走前台（其独占显示表面，比前台锁更深一层），`SetForegroundWindow` / `AttachThreadInput` / `SwitchToThisWindow` 在此场景全部被无视。浏览器全屏视频（F11/全屏 API）只是无边框普通窗口，不受影响，故此前「浏览器能拉回、其他不行」的现象正源于此。
- **策略**：`RecallToCodex` 仍**先尝试** `FocusWindow`（普通窗口、无边框全屏照常成功）；仅当抢焦点返回失败时，调用 `FlashWindow` 让 ChatGPT 任务栏图标闪烁（`FLASHWINFO` + `FlashWindowEx`，`FLASHW_TIMERNOFG` 闪到再次成为前台），并在日志打印 `focus fallback: flashed taskbar ...` 作为判据。既不误伤可抢回的普通窗口，又给独占全屏场景一个可见提示。
- **已知局限**：独占全屏下任务栏通常被隐藏，闪烁需待用户切出/退出全屏后才可见；纯靠自动抢焦点在该场景**无解**。可靠的人工手段是注册全局热键（真实按键释放前台锁），可作为后续增强，本次未加入以免扩大改动面。
- ⚠️ **务必重启进程**：改完 `helper.cs` 必须退出旧托盘进程再双击 `run.bat`。本环境无法编译，需你本机 `powershell -ExecutionPolicy Bypass -File build.ps1` 后自测。

### 回退过度工程：`FocusWindow` 简化为「模拟 Alt 键释放前台锁」三步兜底

- **问题**：`[1.1.2]` 为绕过前台锁引入 `SynchronizationContext`（`UiSync`）机制，把 `FocusWindow` 封回 UI 线程、配 `AttachThreadInput`。这套线程编组**比它要修的问题更脆弱**——`UiSync.Send` 在后台线程同步阻塞、又依赖构造期正确捕获 UI 线程上下文，任一环节不对就整体失效，结果把最基础的「切抖音」「切回 ChatGPT」两个方向都搞挂（用户反馈「发消息拉不到抖音、也拉不回 ChatGPT」）。
- **修复**：**彻底移除 `UiSync` / `FocusWindowCore` 线程编组**，改回从后台线程直接调用的简洁 `FocusWindow`，用经典「模拟 Alt 键释放前台锁」三步兜底：`SetForegroundWindow` 直接置前 → `keybd_event(VK_MENU)` 释放前台锁后再置前 → `AllowSetForegroundWindow(ASFW_ANY)` + `SetWindowPos(HWND_TOP)` + `SwitchToThisWindow` 强兜底。后台线程无消息泵，`AttachThreadInput` 本就不可靠，故一并弃用。
- ⚠️ **务必重启进程**：仅 `build.ps1` 覆盖 exe 不够，必须退出旧托盘进程（托盘右键退出 / 任务管理器结束 `DouyinForCodex.exe`）再双击 `run.bat`，否则内存里仍是旧逻辑。
- **判据**：`helper.log` 现在打印 `focus ok (direct)` / `focus ok (alt-unlock)` / `focus result=...`，据此可确认拉焦是否成功。

### 焦点修复：改用 `AttachThreadInput` 桥接，从任意前台窗口（含桌面/其他应用）稳定拉回 ChatGPT

- **现象**：上一版的「模拟 Alt 键」三步兜底，实测**仅当焦点停留在浏览器网页时**能拉回 ChatGPT；一旦焦点在浏览器外的应用或桌面，拉不回（日志表现为 `focus result=False`）。根因是前台锁对非浏览器前台窗口的 `SetForegroundWindow` 硬拒，Alt 模拟释放锁对跨进程/桌面前台不可靠。
- **修复**：`FocusWindow` 改为标准正解——调 `AttachThreadInput(foreThread, selfThread, true)` 把前台线程输入队列桥接到本线程，使 `SetForegroundWindow` 被系统视作来自「有资格置前」的线程；随后 `SetForegroundWindow`+`SetActiveWindow`+`SetFocus`+`SetWindowPos(HWND_TOP)`，最后 `SwitchToThisWindow` 兜底（任务管理器同款强制置前）。新增强健性：`GetCurrentThreadId` 改从 kernel32 导入（此前错挂 user32 报 `GetCurrentThreadId` 入口点缺失），桥接在 `try/finally` 中一定解绑，失败被吞不影响兜底。
- **C#5 合规**：无 `out _` 弃元、无 `$` 插值、无 `?.`，仅用字符串拼接与 `out uint` 变量，确保 `Add-Type` 编译通过。
- ⚠️ **务必重启进程**：改完 `helper.cs` 必须退出旧托盘进程再双击 `run.bat`（内存里仍是旧逻辑）。本环境无法编译，需你本机 `powershell -ExecutionPolicy Bypass -File build.ps1` 后自测。
- **判据**：`helper.log` 现打印 `focus ok (attach)`（桥接成功）/ `focus ok (switch)`（兜底成功）/ `focus result=...`（仍失败）。

## [1.1.2] - 2026-07-15

### 焦点逻辑改进：不在抖音时只拉回焦点、不打扰

- **新增「当前焦点是否为抖音」判断（`IsForegroundDouyin`）**：通过前台窗口标题含「抖音」且类名为 `Chrome_WidgetWin` / `MozillaWindowClass` 判定。
- **`BeginDouyinSession`（ChatGPT 干活阶段）保持无条件自动切到抖音**：ChatGPT 一开始干活就把抖音窗口切到前台（老程序行为，满足「干活时帮你刷抖音」）。此处**不**加 `IsForegroundDouyin` 门槛——加门槛会导致你不在抖音时直接跳过、不再自动跳转（v1.1.2 初版曾误加该门槛，已回退）。
- **`RecallToCodex`（ChatGPT 完成阶段）拆分暂停与拉回**：
  - 拉回 ChatGPT 焦点**始终执行**（无论你当时在哪）。
  - 暂停抖音**仅在「本次由本工具接管过抖音 且 你此刻仍在抖音」时**才做（`shouldPause = forcePause || (_managedSessionActive && onDouyin)`）。
  - 由此同时修掉一个隐患：旧逻辑即使焦点不在抖音也会发全局空格，可能误敲到别的窗口；新逻辑只在抖音前台时才暂停。
- **`--diagnose` 输出新增 `is_douyin` 标记**，便于在真机验证分支是否走对。
- **`FocusWindow` 增强：绕过 Windows 前台锁**：当 ChatGPT 完成时你正停留在别的窗口（前台锁活跃），后台托盘程序调 `SetForegroundWindow` 会被系统拒绝（只闪任务栏）。**根因是 `AttachThreadInput` 的线程方向用错**——必须把**调用线程(helper)**挂到**前台线程**，系统才会把这次 `SetForegroundWindow` 当成前台线程发起而放行；旧版错误地挂到目标线程，所以无效。本次修正：① 改成 `AttachThreadInput(foreThread, curThread, …)`；② 仍失败则 `AllowSetForegroundWindow(ASFW_ANY)` + `LockSetForegroundWindow(LSFW_UNLOCK)` 释放锁后重试；③ 模拟一次 **Alt 键(`VK_MENU`)** 释放前台锁兜底；④ 用 `HWND_TOP` 把 ChatGPT 提到普通 z-order 最前（`SetWindowPos`）。确保从任意窗口都能把焦点拉回 ChatGPT。
- ⚠️ **已知坑（v1.1.2 第二次修正）**：上一笔「Alt 模拟」单独不足以稳定绕过前台锁，本提交补上正确的 `AttachThreadInput` 线程方向后才真正生效。诊断日志中 `focus attempt:` 现在打印 `curThread=` / `sameAsFore=`（旧版是 `targetThread=` / `sameThread=`，可作为是否跑对新构建的判据）。

## [1.1.1] - 2026-07-14

### 构建可行性修复（回退 JSON 解析器）

- **回退 `System.Text.Json` → `System.Web.Script.Serialization.JavaScriptSerializer`**：`[1.1.0]` 中将 JSON 解析换为 `System.Text.Json`，但该程序集在 **.NET Framework 4.x + 无 NuGet/dotnet 还原能力** 的环境下无法被 `Add-Type` 解析（裸名 `System.Text.Json` 与 `System.Memory` 均报「未能找到元数据文件」；本机也无 `dotnet` 可执行 `dotnet restore`；直接下 NuGet 包的方案亦不稳定）。`JavaScriptSerializer` 由 GAC 自带、零依赖、零下载，可稳定编译运行，故回退。代码内以 `#pragma warning disable 0618` + 注释说明，并标注迁移路径：迁移到现代 .NET 时改回 `System.Text.Json`。
- **清理构建脚本**：删除 `resolve_stj.ps1`（下载/还原 STJ 的临时方案），`build.ps1` 与 `install.ps1` 的 `-ReferencedAssemblies` 改回 `"System.Windows.Forms","System.Drawing","System.Web.Extensions"`，移除所有外部 DLL 复制逻辑。
- **保留的修复不变**：`RecallToCodex` 找窗口逻辑、方法拆分、UTC 时间解析、异常处理收敛等改进均保留。

## [1.1.0] - 2026-07-14

### 实现合并与工程化

- **合并三套实现为 C# 单一实现**：将 `douyin_helper.py`（Python）与 `douyin_helper.ps1`（PowerShell）归档至 `legacy/`，确立 `helper.cs`（`DouyinForCodex.exe`）为唯一维护实现，消除多实现行为漂移。
- ⚠️ **「替换废弃 API」条目已修订**：`[1.1.0]` 曾将 JSON 解析换为 `System.Text.Json`，但因其在本项目目标环境（.NET Framework 4.x 无 NuGet/dotnet）无法编译，已于 `[1.1.1]` 回退为框架内置的 `JavaScriptSerializer`（带迁移说明）。详见 `[1.1.1]`。
- **修复 `RecallToCodex` 找窗口写死 "ChatGPT" 的 bug**：改为按实际 Codex 进程名定位主窗口，不再硬编码。
- **拆分解读方法提升可读性**：将会话元数据解析与事件相位解码从大函数中拆出独立方法（`DecodeMetadata` / `DecodeStateEvent`），便于测试与维护。
- **补全异常处理与 UTC 时间解析**：收紧异常捕获范围、避免裸 `catch`，时间戳按 UTC（`Z`）解析。
- **补齐工程化文档与 CI**：新增根 `README.md`、`LICENSE`（MIT）、本 `CHANGELOG.md`，以及 `.github/workflows/ci.yml`（Windows 下编译并运行 `--self-test` / `--diagnose`，自测失败以非零退出码中断流水线）。

## Known Caveats（已知限制）

- 抖音仅支持**浏览器网页**（匹配 `Chrome_WidgetWin` / `MozillaWindowClass` 类名）；桌面客户端（Electron/UWP）暂不识别。
- 会话监控目录硬编码 `%USERPROFILE%\.codex\sessions`；若 ChatGPT 合体后挪到 `.chatgpt`，自动触发会静默失效（手动托盘点击仍可用）。
- UIPI 限制：若 ChatGPT 以管理员运行而本工具未提权，跨权限抢前台可能失败。
- `JavaScriptSerializer` 为废弃 API，属环境受限下的技术债（详见 `CLAUDE.md` 第 3、6 节）。
- **Release 预编译 exe 与源码版本不同步**：GitHub Releases 上的 `DouyinForCodex.exe` 目前是 **v1.1.1**（旧焦点逻辑）。`[1.1.2]` 的源码已包含新的「不在抖音只拉回、不打扰」逻辑，需**本地重新运行 `build.ps1`** 生成新 exe 才能生效；Release 资产将在本地重建后更新。
