# CLAUDE.md — 项目须知与踩坑纪实

> 本文件是给 AI 编程代理（及人类协作者）的"项目大脑"。它记录**真实发生过的坑**、设计权衡、以及操作红线。改这个仓库之前，先读完。

---

## 1. 这是什么

`codex-douyin-windows` 是一个 Windows 托盘小工具：监听 [Codex](https://github.com/openai/codex)（现已并入 ChatGPT）的本地会话状态文件，在**抖音**与 **ChatGPT** 两个窗口间自动切换焦点——ChatGPT 在干活时帮你刷抖音，ChatGPT 等你反馈时自动暂停抖音并切回 ChatGPT。

- **唯一权威实现（SSOT）**：`helper.cs` → 编译产物 `DouyinForCodex.exe`。
- **已归档**：`douyin_helper.py` / `douyin_helper.ps1` 在 `legacy/`，不再维护。

---

## 2. 架构与构建约束（⚠️ 最重要）

本仓库用 `Add-Type -TypeDefinition`（PowerShell 内联 C# 编译器）从**单个 `helper.cs`** 直接编译，而不是 `dotnet build` + `.csproj`。这个选择带来**硬约束**：

### 约束 A：代码必须锁在 C# 5 语法
`Add-Type` 底层是 .NET Framework 的 CodeDOM 旧编译器，**只认 C# 5**。以下写法会编译失败（报"应输入 ;"或类似）：
- ❌ 表达式体方法：`int Foo() => 1;`（C# 6）—— 必须写成 `{ return 1; }`
- ❌ `out var x`（C# 7）—— 必须 `int x; Foo(out x);`
- ❌ 模式匹配 `if (o is string s)`（C# 7）—— 必须 `string s = o as string;`
- ❌ 空传播 `?.`、字符串插值 `$"..."`、nameof、using static、元组、default 字面量（C# 6+）

✅ 允许：lambda `(s,e) => { ... }`（C# 3 就有）、`var`、`using`、`as`/`is` 老式转换、`out` 方法**签名参数**（`void F(out int x)` 合法，但方法**体内**不能 `out int x`）。

### 约束 B：引用程序集只能用 GAC 里存在的
`Add-Type` 的 `-ReferencedAssemblies` 只能用 .NET Framework **GAC 自带**的程序集：
- ✅ `System.Windows.Forms`、`System.Drawing`、`System.Web.Extensions`（含 `JavaScriptSerializer`）
- ❌ `System.Text.Json`、`System.Memory` 等 NuGet 包——在纯 .NET Framework 机器上 `Add-Type` 按裸名或路径都**解析不到**（报"未能找到元数据文件"）。

### 约束 C：`Dictionary<string,object>.TryGetValue` 的 out 变量类型必须严格匹配
字典是 `Dictionary<string,object>`，`TryGetValue("k", out v)` 的 `v` 必须声明为 `object`，不能图省事写成 `string v`（类型不匹配报错）。

---

## 3. 真实踩坑纪实（按时间线，每一条都是真金白银的教训）

### 坑 #1：构建脚本多带了 `System.Memory` 引用
`build.ps1` 的 `-ReferencedAssemblies` 多列了 `"System.Memory"`（NuGet 包，Add-Type 解析不到），编译报"未能找到元数据文件 System.Memory.dll"。删掉即与 `install.ps1` 对齐为三个 GAC 引用。

### 坑 #2：`System.Text.Json` 在该环境根本不可用
一度把 JSON 解析从 `JavaScriptSerializer` 换成 `System.Text.Json`（更现代、符合"禁废弃 API"规范）。但在本环境（.NET Framework 4.x、无 NuGet、无 `dotnet`）编译不过；尝试"自动下载 NuGet 包"兜底，又因为机器没装 `dotnet` 失败。**回退**为 GAC 自带的 `JavaScriptSerializer`，并在代码里 `#pragma warning disable 0618` + 注释说明，标注迁移路径：**一旦迁到现代 .NET（dotnet build + csproj），应改回 `System.Text.Json`**。**这是记录在案的技术债，不是无脑回退。**

### 坑 #3：编译器版本坑（C# 6/7 语法）
回退后编译又报"应输入 ;"——因为解码方法里用了 `=>` 表达式体 + `out var` + 模式匹配。`Add-Type` 的旧编译器不认。把所有解码/读取方法（含 csharp-lead 拆出的 `ReadNewLines` 系列）整体降到 C# 5 写法。

### 坑 #4：out 变量类型不匹配
C#5 化时把 `GetThreadId` 里的 `out` 变量错写成 `string`，与 `Dictionary<string,object>` 不符，编译报"重载方法具有一些无效参数"。改回 `object` 解决。

### 坑 #5：产品改名导致窗口匹配失效（最隐蔽的运行时 bug）
原 macOS 移植遗留写死 `"ChatGPT"` 找窗口（错误）；我们改成按标题找 `"Codex"`（以为对）。结果 **Codex 与 ChatGPT 合体，窗口真就叫 "ChatGPT"**，按 "Codex" 匹配不到 → "暂停并回到 ChatGPT" 死活不工作。修复：① `FindWindowByTitle` 改为大小写不敏感 + 多候选；② 新增 `FindWindowByProcessName` 按**进程名**匹配（ChatGPT 桌面应用进程名就是 `ChatGPT`，不依赖标题文字）；③ 标题找不到时回退进程名。

### 坑 #6：后台程序切前台被 Windows 拒绝
`SetForegroundWindow` + "假装按 Alt" 的 trick 在新版 Windows 上，对**后台托盘程序**直接被系统拒绝（找到窗口也切不过去）。修复：用 `SwitchToThisWindow` + `AttachThreadInput` 挂接前台线程 + `SetWindowPos(TOPMOST)` 兜底三重保险。

### 坑 #7：托盘手动点击被暂停锁死
`RecallToCodex` 里 `if (_managedSessionActive)` 把"暂停抖音"锁死——用户直接右键托盘点"暂停并回到 ChatGPT"（没经历过 working 阶段）时，连抖音都不暂停。加 `forcePause` 参数：手动点击强制暂停 + 切回，自动流程不受影响。

---

## 4. 测试入口

- `--self-test`：内置单元测试（会话元数据解析、事件相位解码、UTC 时间戳），全过打印 `Self-tests passed.`，失败 `Debug.Assert` 中断。这是核心 service 层的测试覆盖。
- `--diagnose`：环境诊断——抖音窗口、ChatGPT 窗口（按**标题**与**进程名**分别报告）、当前前台窗口、`%USERPROFILE%\.codex\sessions` 是否存在、最近 48h 会话文件数。
- CI：`.github/workflows/ci.yml` 在 Windows runner 上 `build.ps1`（编译 + self-test + diagnose），自测失败非零退出码中断流水线。

---

## 5. 操作红线（给代理）

1. **改 `helper.cs` 后，必须保持 C# 5 语法**（见约束 A）。提交前本地跑 `build.ps1` 确认编译过。
2. **不要重新引入 `System.Text.Json` / `System.Memory` 等 NuGet 包引用**——除非先把构建改成 `dotnet build`。
3. **不要删 `legacy/`**——那是历史实现证据，且 README 引用了它。
4. **窗口匹配字符串**（`"ChatGPT"`, `"Codex"`）已集中为 `Win32.CodexWindowTitles` / `Win32.CodexProcessNames` 常量（`RecallToCodex` 与 `Diagnose` 共用）。新增调用点一律引用常量，不要再硬编码。
5. **编译产物 `DouyinForCodex.exe` 不入库**（见 `.gitignore`）。

---

## 6. 已知 Caveats（诚实记录）

- 抖音若是**桌面客户端**（Electron/UWP）而非浏览器网页，`FindBrowserWindow` 的类名匹配（`Chrome_WidgetWin`/`MozillaWindowClass`）会失效——当前只支持浏览器里的抖音网页。
- 会话监控目录硬编码 `%USERPROFILE%\.codex\sessions`。若 ChatGPT 合体后把会话挪到 `.chatgpt`，自动触发会静默失效（手动托盘点击仍可用）。
- 后台程序抢前台受 Windows UIPI 限制；若 ChatGPT 以管理员权限运行而本工具未提权，跨权限切前台可能失败。
- `JavaScriptSerializer` 是废弃 API（技术债），仅在当前 .NET Framework 目标下为可行解。

---

## 7. 迁移到现代 .NET 时的 checklist（技术债清算）

当前为 .NET Framework 4.x + `Add-Type` 单文件编译（零 NuGet、零 dotnet）。一旦迁移到 `dotnet build` + `.csproj` + NuGet，**必须**同步清算以下技术债：

- [ ] **JSON 解析**：`System.Web.Script.Serialization.JavaScriptSerializer` → `System.Text.Json`。涉及 `SessionMonitor.DecodeMetadata` / `DecodeStateEvent`；去掉 `#pragma warning disable 0618` 与迁移注释。
- [ ] **C# 语法升级**：`helper.cs` 当前锁在 C# 5（约束 A）。迁现代 .NET 后可启用 C# 7+：表达式体、`out var`、模式匹配、`?.`、字符串插值等。
- [ ] **构建方式**：`build.ps1` 的 `Add-Type` → `dotnet build`；`-ReferencedAssemblies` 改为 `.csproj` 的 `<PackageReference>` / `<Reference>`。`AGENTS.md` 的"唯一构建方式"红线同步更新。
- [ ] **CI**：`.github/workflows/ci.yml` 的 `build.ps1` 调用改为 `dotnet build` + `dotnet test`（若有正式测试项目）。
- [ ] **会话目录**：硬编码 `%USERPROFILE%\.codex\sessions`，若 ChatGPT 合体后会话挪到 `.chatgpt`，需改为探测多候选目录。
- [ ] **窗口匹配**：`CodexWindowTitles` / `CodexProcessNames` 常量届时可改为配置项，适应未来窗口名再变更。
