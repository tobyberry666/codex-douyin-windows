# legacy/ — 归档的早期实现

本目录存放 `Codex 抖音助手` 的**早期 / 替代实现**，仅作历史参考，已不再维护。

| 文件 | 说明 |
| --- | --- |
| `douyin_helper.py` | Python 实现（早期原型） |
| `douyin_helper.ps1` | PowerShell 实现（早期原型） |

## 为什么归档

仓库原本存在三套等价实现：`helper.cs`（C#，发布为 `DouyinForCodex.exe`）、`douyin_helper.py`、`douyin_helper.ps1`。
按团队规范 **单一事实来源（SSOT）**，C# 版是唯一权威实现，Python / PowerShell 版会随维护者精力导致行为漂移，
因此统一降级归档，避免多份实现互相不一致。

## 现状

- 当前唯一维护实现：`helper.cs` → 编译产物 `DouyinForCodex.exe`（位于仓库根目录）。
- 这两个文件**不再随主程序更新**，也不会被 `build.ps1` / `install.ps1` 引用。
- 它们的内容已冻结为归档快照，请勿在此修改业务逻辑。

## 如需修改行为

请从 C# 版入手：编辑根目录 `helper.cs`，然后通过

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

重新编译。修改 Python / PowerShell 版本无法生效，且会造成新的行为差异。
