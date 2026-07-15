# AGENTS.md — 代理操作约定

本仓库供 AI 编程代理协作。在动手前请先读 `CLAUDE.md`（项目大脑 + 踩坑纪实）。以下是操作本仓库的硬性约定：

## 构建与验证

- **唯一构建方式**：`powershell -ExecutionPolicy Bypass -File build.ps1`（从 `helper.cs` 用 `Add-Type` 编译，随后跑 `--self-test` + `--diagnose`）。
- 不要尝试 `dotnet build` / `msbuild`——本仓库没有 `.csproj`，且无现代 .NET SDK。
- 每次改完 `helper.cs`，**必须**本地跑 `build.ps1` 确认编译过 + `Self-tests passed.` 再提交。

## 语言与语法红线（C# 5 only）

`Add-Type` 的旧编译器只认 C# 5。改 `helper.cs` 时：
- 方法体用 `{ return ...; }`，**禁止** `=> 表达式体`。
- **禁止** `out var`、`is T x` 模式匹配、`?.`、`$".."`、nameof、元组。
- `Dictionary<string,object>.TryGetValue` 的 out 变量必须声明为 `object`。

## 引用程序集

`-ReferencedAssemblies` 只用 GAC 内的：`System.Windows.Forms`, `System.Drawing`, `System.Web.Extensions`。**禁止**引入 `System.Text.Json` / `System.Memory` 等 NuGet 包（本环境解析不到）。

## 文件边界

- `helper.cs`：唯一权威源码。改它要懂 Win32 P/Invoke + C# 5 约束。
- `legacy/`：只读归档，**不要改、不要删**。
- `build.ps1` / `install.ps1` / `setup.ps1` / `run.bat`：构建与交付脚本，改动需保持三处引用一致。
- 不要新增临时诊断脚本（如 `diag_*.ps1`）到仓库根——那是一次性排查工具，排查完即删。

## 提交纪律

- 编译产物 `DouyinForCodex.exe`、运行时日志（`logs/`、`*.log`）、缓存（`stj_cache/`）已被 `.gitignore` 排除，**不要** `git add -f` 强制入库。
- 提交信息用中文或英文均可，但应说明改了哪类问题（参考 `CHANGELOG.md` 的条目粒度）。
- 安全相关改动（如窗口匹配、焦点切换）要在 PR 描述里说明影响范围。
