# Codex 抖音助手（Windows 版）

监听 [Codex](https://github.com/openai/codex)（现已并入 **ChatGPT**）的本地会话状态，在抖音与 ChatGPT 窗口之间自动切换焦点：ChatGPT 在干活时**若你正在看抖音**就帮你接着刷，ChatGPT 等你反馈时**一定把焦点拉回 ChatGPT**——但你不在抖音时，只拉回焦点、不打扰、不擅自暂停抖音。

> 注：原产品名为 Codex，后与 ChatGPT 合体，桌面应用窗口名即为 **ChatGPT**。本工具窗口匹配同时按标题与进程名（`"ChatGPT"`, `"Codex"` 兜底）查找。
>又注：拉回chatgpt时您须“没有开启全屏”状态

---

## 🚀 快速开始

### 前置条件（Windows 10/11 自带，基本不用装）

| 需求 | 说明 |
|------|------|
| Windows 10 / 11 | 必须 |
| .NET Framework 4.x | Windows 自带，一般无需手动装 |
| PowerShell 5.1 | Windows 自带（`powershell` 命令可用） |
| Git（可选） | **只有「方式一」需要**；方式二用 Release 下载 exe 或朋友直发，均不用装 Git |

> 本工具构建**完全离线**：所有依赖（`System.Web.Extensions` 等）都由系统 GAC 提供，**不需要联网、不需要 NuGet、不需要 `dotnet`**。所以断网也能编译。

### 方式一：有 Git 的开发者

```powershell
# 1) 把仓库拉到本地（仓库已公开，任何人可 clone）
git clone https://github.com/tobyberry666/codex-douyin-windows.git
cd codex-douyin-windows

# 2) 编译（自动从 helper.cs 生成 DouyinForCodex.exe，并跑自测）
powershell -ExecutionPolicy Bypass -File build.ps1

# 3) 运行
run.bat
```

### 方式二：不懂命令的朋友（最简单）

任选一种拿到可运行的程序：

- **A. 下载预编译 exe（零门槛，推荐）**：到 [GitHub Releases](https://github.com/tobyberry666/codex-douyin-windows/releases) 下载 `DouyinForCodex.exe`，和仓库里的 `run.bat` 放在同一目录，双击 `run.bat` 即可。
- **B. 自己编译（离线，无需联网）**：把仓库（或朋友发的整个文件夹）解压后，运行 `powershell -ExecutionPolicy Bypass -File build.ps1` 生成 `DouyinForCodex.exe`，再双击 `run.bat`。注意：GitHub 的 **Download ZIP 不含 exe**，所以这种方式必须先走编译这一步。
- **C. 朋友直接发**：让朋友把 `DouyinForCodex.exe` + `run.bat` 两个文件发你，双击 `run.bat`。

> 所有脚本都用 `$PSScriptRoot` 自动定位自己所在目录，**不依赖你在哪个盘、叫什么路径**，只要文件在同一文件夹里就能跑。

### 验证是否跑起来了

- 右下角系统托盘（通知区域）出现图标「ChatGPT 抖音助手」：
  - **▶（play）**：ChatGPT 工作中，抖音在播放。
  - **⏸（pause）**：监听中 / ChatGPT 在等你反馈。
- 想看诊断信息：`powershell -ExecutionPolicy Bypass -File build.ps1` 结尾会打印窗口与环境状态；也可单独跑 `DouyinForCodex.exe --diagnose`。


## 🏗️ 架构

- **权威实现（单一事实来源 / SSOT）**：`helper.cs` → 编译产物 `DouyinForCodex.exe`（真正发布的程序）。
- **早期 / 替代实现**：`douyin_helper.py` 与 `douyin_helper.ps1` 已归档于 [`legacy/`](legacy/)，不再随主程序更新。如需修改行为，请从 C# 版入手。

## 🔧 构建（详细说明）

需要 Windows + .NET Framework（随 Windows 自带）。**在包含 `helper.cs` 的目录里**执行：

```powershell
# 编译并运行自测 + 诊断
powershell -ExecutionPolicy Bypass -File build.ps1

# 或：编译 + 创建开始菜单快捷方式 / 可选开机自启
powershell -ExecutionPolicy Bypass -File install.ps1
```

`build.ps1` 使用 `Add-Type` 直接从 `helper.cs` 编译 `DouyinForCodex.exe`，随后运行 `--self-test` 与 `--diagnose`。自测失败会以退出码 `1` 结束（CI 可据此判定失败）。

- 构建**离线进行**，不访问网络，不调用 `dotnet` / NuGet。
- 引用的程序集 `System.Windows.Forms`、`System.Drawing`、`System.Web.Extensions` 全部来自系统 GAC。
- 脚本靠 `$PSScriptRoot` 定位同级文件，目录路径随意、移动文件夹也不影响。

## ▶️ 运行

```powershell
# 方式一：run.bat 启动（双击也可）
run.bat

# 方式二：直接双击 DouyinForCodex.exe
```

启动后它常驻**系统托盘**（通知区域），托盘图标名为“ChatGPT 抖音助手”：

- 图标为 **▶（play）**：表示 ChatGPT 工作中，抖音正在播放。
- 图标为 **⏸（pause）**：表示正在监听 ChatGPT，或 ChatGPT 在等你反馈。
- 右键托盘图标菜单：
  - 状态行（监听中 / ChatGPT 工作中 / ChatGPT 等反馈）
  - **✓ 启用自动刷**（开关，关闭后不再自动操作抖音）
  - **打开抖音**
  - **暂停并回到 ChatGPT**（手动切回，强制暂停抖音）
  - **退出**

## 🧩 命令行参数

- `--self-test`：运行内置单元测试（会话元数据解析、事件相位解码、UTC 时间戳），全部通过打印 `Self-tests passed.` 并以退出码 `0` 结束；失败则 `Debug.Assert` 中断。
- `--diagnose`：打印环境诊断信息——抖音窗口、ChatGPT 窗口（按**标题**与**进程名**分别报告）、当前前台窗口标题与 `is_douyin` 标记（当前焦点是否为抖音，用于验证上面的分支逻辑）、`%USERPROFILE%\.codex\sessions` 目录是否存在、最近 48 小时会话文件数。

## 🎯 行为说明

行为按「**你当前焦点是不是抖音**」分两种：

- **你正在看抖音时（老程序全跑）**
  - **ChatGPT 干活（`working`，如 `task_started`）** → 自动定位 / 打开抖音窗口并切到前台，若此前被本工具暂停则按空格恢复播放。
  - **ChatGPT 等反馈（`attention`，如 `task_complete` / `turn_aborted` / `request_user_input`）** → 暂停抖音播放（按空格），稍作延迟后切回 ChatGPT 窗口。
- **你不在抖音时（不打扰）**
  - **ChatGPT 干活** → 本工具**不动你的焦点**，也不擅自打开抖音；你继续做自己的事。
  - **ChatGPT 等反馈** → **仍会把焦点拉回 ChatGPT**（这样你不会错过它干完），但**不暂停抖音**（你也没在看）、也不会往别的窗口误发空格。

> 判断依据是「当前前台窗口是否为抖音浏览器标签页」（标题含「抖音」且类名为 `Chrome_WidgetWin` / `MozillaWindowClass`）。
- 仅在“用户线程”（非 subagent）会话上触发，避免子代理噪声干扰。
- 启动时会读取最近 48 小时的 ChatGPT 会话文件做状态引导；状态通过托盘图标实时反映。

## 📂 会话数据来源

监控 `%USERPROFILE%\.codex\sessions` 下的 `*.jsonl` 会话日志，增量解析 `session_meta`（区分用户/子代理线程）与事件消息（`event_msg` / `response_item`）以判定相位。

## ✅ 测试

- 核心 service 层（会话元数据解析、事件相位解码、UTC 时间）由 `--self-test` 内置断言覆盖。
- 持续集成见 `.github/workflows/ci.yml`（Windows runner 编译 + 自测，失败即红）。

## ⚠️ 已知限制

- 抖音仅支持**浏览器网页**（匹配 `Chrome_WidgetWin` / `MozillaWindowClass` 类名）；桌面客户端暂不识别。
- 会话监控目录硬编码 `%USERPROFILE%\.codex\sessions`；若 ChatGPT 合体后挪到 `.chatgpt`，自动触发会静默失效（手动托盘点击仍可用）。
- 后台程序抢前台受 Windows UIPI 限制；若 ChatGPT 以管理员运行而本工具未提权，跨权限切前台可能失败。
- **其他应用全屏会锁定焦点**：其他应用在 Windows 上处于**独占全屏**（如全屏游戏、全屏视频）时会锁定焦点，本工具无法将其抢走——这是 Windows 的系统级限制，任何进程都不能从独占全屏应用抢走前台（比前台锁更深一层）。**使用本工具时，请勿让其他应用处于全屏状态**。若 ChatGPT 完成时焦点正停在独占全屏应用，本工具会改为让 ChatGPT 的任务栏图标闪烁提醒，你切出全屏后即可看到。
- JSON 解析使用 `System.Web.Script.Serialization.JavaScriptSerializer`（废弃 API，环境受限下的技术债）。

详见 [`CLAUDE.md`](CLAUDE.md)（踩坑纪实与设计取舍）与 [`AGENTS.md`](AGENTS.md)（代理操作约定）。

---

本仓库遵循团队代码规范：**无裸 catch、时间使用 UTC、单一事实来源（SSOT）**。

> **关于「废弃 API」的务实例外**：JSON 解析当前使用 `System.Web.Script.Serialization.JavaScriptSerializer`。本工具目标框架为 **.NET Framework 4.x**，运行环境**无 NuGet / dotnet 还原能力**，`System.Text.Json` 无法作为引用程序集被 `Add-Type` 解析（裸名或路径均失败）。`JavaScriptSerializer` 由 GAC 自带、零依赖、零下载，可稳定编译运行，故选用。代码内已用 `#pragma warning disable 0618` + 注释说明，并标注迁移路径：**一旦改用现代 .NET（dotnet build / csproj + NuGet），应改回 `System.Text.Json`**。
