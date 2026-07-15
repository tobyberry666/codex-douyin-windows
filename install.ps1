$dir = Split-Path $PSCommandPath -Parent
$exe = Join-Path $dir "DouyinForCodex.exe"
$src = Join-Path $dir "helper.cs"

# .NET Framework 4.x 自带 System.Web.Extensions（GAC），Add-Type 可直接按名解析
$refs = "System.Windows.Forms", "System.Drawing", "System.Web.Extensions"

# 1. Compile
Write-Host "Compiling..." -ForegroundColor Cyan
$cs = Get-Content $src -Raw
Add-Type -TypeDefinition $cs -ReferencedAssemblies $refs -OutputAssembly $exe -OutputType WindowsApplication -ErrorAction Stop
Write-Host "  Done ($( (Get-Item $exe).Length ) bytes)" -ForegroundColor Green

# 2. Self-test
Write-Host "Testing..."
$test = Join-Path $dir "_t.exe"
Add-Type -TypeDefinition $cs -ReferencedAssemblies $refs -OutputAssembly $test -OutputType ConsoleApplication -ErrorAction Stop
& $test --self-test
Remove-Item $test -Force

# 3. Start Menu
$sm = [Environment]::GetFolderPath("StartMenu") + "\Programs\Codex 抖音助手"
New-Item -ItemType Directory -Path $sm -Force | Out-Null
$sc = Join-Path $sm "Codex 抖音助手.lnk"
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($sc)
$lnk.TargetPath = $exe; $lnk.WorkingDirectory = $dir; $lnk.Description = "Codex 抖音助手"
$lnk.Save()
Write-Host "  Start Menu: $sc" -ForegroundColor Green

# 4. Startup
$choice = Read-Host "开机自动启动？(y/n)"
if ($choice -eq 'y') {
    Set-ItemProperty -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name "CodexDouyinHelper" -Value $exe
    Write-Host "  已设置开机自启" -ForegroundColor Green
} else {
    Remove-ItemProperty -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name "CodexDouyinHelper" -ErrorAction SilentlyContinue
    Write-Host "  跳过开机自启（重跑此脚本可重新设置）" -ForegroundColor Yellow
}

Write-Host "`nDone." -ForegroundColor Green
