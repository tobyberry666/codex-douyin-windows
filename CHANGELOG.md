# Changelog

本文件遵循团队重构记录惯例（标题含版本与日期）。

## Design Rationale（设计取舍）

- **单文件 C# + `Add-Type` 编译，而非 `dotnet build`**：目标机器为纯 .NET Framework 4.x、无 NuGet、无 `dotnet` SDK。`Add-Type` 可直接从单文件源码编译，零脚手架依赖。代价是代码锁在 **C# 5 语法**、引用只能是 **GAC 程序集**（见 `CLAUDE.md` 约束 A/B）。若未来要上现代 C#，应整套迁移到 `csproj` + NuGet。
- **JSON 解析用 `JavaScriptSerializer`（废弃 API，技术债）**：`System.Text.Json` 在该环境无法被 `Add-Type` 解析。前者由 GAC 自带、零依赖可编译。已加 `#pragma warning disable 0618` + 迁移注释，一旦迁现代 .NET 即换回。
- **窗口匹配「标题 + 进程名」双路**：产品名从 Codex 改为 ChatGPT（合体），仅按标题匹配会失效；进程名 `ChatGPT` 不依赖标题文字，作为兜底。
- **焦点切换三重保险**：后台托盘程序调 `SetForegroundWindow` 常被 Windows 拒绝，故用 `SwitchToThisWindow` + `AttachThreadInput` + `SetWindowPos` 兜底。
- **单一事实来源（SSOT）**：Python/PowerShell 版归档于 `legacy/`，C# 为唯一维护实现。

## [1.1.2] - 2026-07-15

### 焦点逻辑改进：不在抖音时只拉回焦点、不打扰

- **新增「当前焦点是否为抖音」判断（`IsForegroundDouyin`）**：通过前台窗口标题含「抖音」且类名为 `Chrome_WidgetWin` / `MozillaWindowClass` 判定。
- **`BeginDouyinSession`（ChatGPT 干活阶段）保持无条件自动切到抖音**：ChatGPT 一开始干活就把抖音窗口切到前台（老程序行为，满足「干活时帮你刷抖音」）。此处**不**加 `IsForegroundDouyin` 门槛——加门槛会导致你不在抖音时直接跳过、不再自动跳转（v1.1.2 初版曾误加该门槛，已回退）。
- **`RecallToCodex`（ChatGPT 完成阶段）拆分暂停与拉回**：
  - 拉回 ChatGPT 焦点**始终执行**（无论你当时在哪）。
  - 暂停抖音**仅在「本次由本工具接管过抖音 且 你此刻仍在抖音」时**才做（`shouldPause = forcePause || (_managedSessionActive && onDouyin)`）。
  - 由此同时修掉一个隐患：旧逻辑即使焦点不在抖音也会发全局空格，可能误敲到别的窗口；新逻辑只在抖音前台时才暂停。
- **`--diagnose` 输出新增 `is_douyin` 标记**，便于在真机验证分支是否走对。

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
