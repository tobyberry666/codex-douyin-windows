# Codex 抖音助手 Windows 版 — 编译脚本
# 目标框架：.NET Framework 4.x。System.Web.Extensions 由 GAC 提供，无需 NuGet / dotnet。

$ErrorActionPreference = 'Stop'

$src = Join-Path $PSScriptRoot "helper.cs"
$out = Join-Path $PSScriptRoot "DouyinForCodex.exe"

# .NET Framework 4.x 自带 System.Web.Extensions（GAC），Add-Type 可直接按名解析
$refs = "System.Windows.Forms", "System.Drawing", "System.Web.Extensions"

Write-Host "Compiling C# source..." -ForegroundColor Cyan
$cs = Get-Content $src -Raw
try {
    Add-Type -TypeDefinition $cs -ReferencedAssemblies $refs -OutputAssembly $out -OutputType WindowsApplication
} catch {
    Write-Host "BUILD FAILED: $_" -ForegroundColor Red
    exit 1
}
Write-Host "Compiled: $out ($( (Get-Item $out).Length ) bytes)" -ForegroundColor Green

Write-Host ""
Write-Host "Running self-tests + diagnose (console build)..." -ForegroundColor Cyan
$test = Join-Path $PSScriptRoot "_t.exe"
try {
    Add-Type -TypeDefinition $cs -ReferencedAssemblies $refs -OutputAssembly $test -OutputType ConsoleApplication
} catch {
    Write-Host "TEST BUILD FAILED: $_" -ForegroundColor Red
    exit 1
}
& $test --self-test
$rc = $LASTEXITCODE
if ($rc -eq 0) { & $test --diagnose }
Remove-Item $test -Force
if ($rc -ne 0) { Write-Host "ERROR: Self-tests failed" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "Build complete. Run: run.bat" -ForegroundColor Green
