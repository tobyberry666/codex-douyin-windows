# Codex 抖音助手 Windows 版 — 安装脚本
# 纯 PowerShell，零外部依赖，此脚本仅做环境验证

Write-Host "=== Codex 抖音助手 Windows 版 ===" -ForegroundColor Cyan
Write-Host ""

Write-Host "Running self-tests (compile C# + --self-test)..."
$result = powershell.exe -ExecutionPolicy Bypass -File "$PSScriptRoot\build.ps1" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host $result
    Write-Host "ERROR: Self-tests failed" -ForegroundColor Red
    exit 1
}
Write-Host $result

Write-Host ""
Write-Host "Checking Codex sessions directory..."
if (Test-Path "$env:USERPROFILE\.codex\sessions") {
    $count = (Get-ChildItem "$env:USERPROFILE\.codex\sessions" -Recurse -Filter "*.jsonl" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge (Get-Date).AddHours(-48) }).Count
    Write-Host "  Recent session files: $count" -ForegroundColor Green
} else {
    Write-Host "  Sessions directory not found (Codex may not have been used yet)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Setup complete. Run: run.bat" -ForegroundColor Green
Write-Host "Or double-click run.bat in File Explorer." -ForegroundColor Green
