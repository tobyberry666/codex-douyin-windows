# Codex 抖音助手（Windows 版）

监听 [Codex](https://github.com/openai/codex)（现已并入 **ChatGPT**）的本地会话状态，在抖音与 ChatGPT 窗口之间自动切换焦点：ChatGPT 在干活时帮你刷抖音，ChatGPT 等你反馈时自动暂停抖音并切回 ChatGPT。

> 注：原产品名为 Codex，后与 ChatGPT 合体，桌面应用窗口名即为 **ChatGPT**。本工具窗口匹配同时按标题与进程名（`"ChatGPT"`, `"Codex"` 兜底）查找。

## 架构

- **权威实现（单一事实来源 / SSOT）**：`helper.cs` → 编译产物 `DouyinForCodex.exe`（仓库根目录，真正发布的程序）。
- **早期 / 替代实现**：`douyin_helper.py` 与 `douyin_helper.ps1` 已归档于 [`legacy/`](legacy/)，不再随主程序更新。如需修改行为，请从 C# 版入手。

## 构建

需要 Windows + .NET Framework（随 Windows 自带）。在仓库根目录执行：

```powershell
# 编译并运行自测 + 诊断
powershell -ExecutionPolicy Bypass -File build.ps1

# 或：编译并创建开始菜单快捷方式 / 可选开机自启
powershell -ExecutionPolicy Bypass -File install.ps1
```

`build.ps1` 使用 `Add-Type` 直接从 `helper.cs` 编译 `DouyinForCodex.exe`，随后运行 `--self-test` 与 `--diagnose`。自测失败会以退出码 `1` 结束（CI 可据此判定失败）。

## 运行

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

## 命令行参数

- `--self-test`：运行内置单元测试（会话元数据解析、事件相位解码、UTC 时间戳），全部通过打印 `Self-tests passed.` 并以退出码 `0` 结束；失败则 `Debug.Assert` 中断。
- `--diagnose`：打印环境诊断信息——抖音窗口、ChatGPT 窗口（按**标题**与**进程名**分别报告）、当前前台窗口标题、`%USERPROFILE%\.codex\sessions` 目录是否存在、最近 48 小时会话文件数。

## 行为说明

- **ChatGPT 干活（`working`，如 `task_started`）** → 自动定位 / 打开抖音窗口并切到前台，若此前被本工具暂停则按空格恢复播放。
- **ChatGPT 等反馈（`attention`，如 `task_complete` / `turn_aborted` / `request_user_input`）** → 暂停抖音播放（按空格），稍作延迟后切回 ChatGPT 窗口。
- 仅在“用户线程”（非 subagent）会话上触发，避免子代理噪声干扰。
- 启动时会读取最近 48 小时的 ChatGPT 会话文件做状态引导；状态通过托盘图标实时反映。

## 会话数据来源

监控 `%USERPROFILE%\.codex\sessions` 下的 `*.jsonl` 会话日志，增量解析 `session_meta`（区分用户/子代理线程）与事件消息（`event_msg` / `response_item`）以判定相位。

## 测试

- 核心 service 层（会话元数据解析、事件相位解码、UTC 时间）由 `--self-test` 内置断言覆盖。
- 持续集成见 `.github/workflows/ci.yml`（Windows runner 编译 + 自测，失败即红）。

## 已知限制

- 抖音仅支持**浏览器网页**（匹配 `Chrome_WidgetWin` / `MozillaWindowClass` 类名）；桌面客户端暂不识别。
- 会话监控目录硬编码 `%USERPROFILE%\.codex\sessions`；若 ChatGPT 合体后挪到 `.chatgpt`，自动触发会静默失效（手动托盘点击仍可用）。
- 后台程序抢前台受 Windows UIPI 限制；若 ChatGPT 以管理员运行而本工具未提权，跨权限切前台可能失败。
- JSON 解析使用 `System.Web.Script.Serialization.JavaScriptSerializer`（废弃 API，环境受限下的技术债）。

详见 [`CLAUDE.md`](CLAUDE.md)（踩坑纪实与设计取舍）与 [`AGENTS.md`](AGENTS.md)（代理操作约定）。

---

本仓库遵循团队代码规范：**无裸 catch、时间使用 UTC、单一事实来源（SSOT）**。

> **关于「废弃 API」的务实例外**：JSON 解析当前使用 `System.Web.Script.Serialization.JavaScriptSerializer`。本工具目标框架为 **.NET Framework 4.x**，运行环境**无 NuGet / dotnet 还原能力**，`System.Text.Json` 无法作为引用程序集被 `Add-Type` 解析（裸名或路径均失败）。`JavaScriptSerializer` 由 GAC 自带、零依赖、零下载，可稳定编译运行，故选用。代码内已用 `#pragma warning disable 0618` + 注释说明，并标注迁移路径：**一旦改用现代 .NET（dotnet build / csproj + NuGet），应改回 `System.Text.Json`**。
