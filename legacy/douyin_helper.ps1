# Codex 抖音助手 — Windows 版 (PowerShell)
# 零外部依赖，纯 .NET WinForms + P/Invoke
# 基于 yekwennnn/codex-douyin-helper macOS 版移植

param(
   [switch]$SelfTest,
   [switch]$Diagnose
)

# ============================================================
# P/Invoke — Win32 API declarations via C# Add-Type
# ============================================================

# Force STA thread — WinForms NotifyIcon requires STA apartment state
[System.Threading.Thread]::CurrentThread.SetApartmentState([System.Threading.ApartmentState]::STA)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Win32 {

    // --- Window enumeration ---

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    // --- Window focus ---

    [DllImport("user32.dll")]
    public static extern IntPtr SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;

    // --- Keyboard input ---

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_SPACE = 0x20;
    public const byte VK_MENU = 0x12;

    // --- Window finding helpers ---

    private static IntPtr _found;
    private static string _titleFilter;
    private static string _classFilter;

    private static bool EnumProc(IntPtr hWnd, IntPtr lParam) {
        if (!IsWindowVisible(hWnd)) return true;

        StringBuilder title = new StringBuilder(256);
        GetWindowText(hWnd, title, 256);
        string t = title.ToString();

        if (!string.IsNullOrEmpty(_titleFilter) && !t.Contains(_titleFilter))
            return true;

        if (!string.IsNullOrEmpty(_classFilter)) {
            StringBuilder cls = new StringBuilder(256);
            GetClassName(hWnd, cls, 256);
            string c = cls.ToString();
            if (!c.Contains(_classFilter))
                return true;
        }

        _found = hWnd;
        return false;
    }

    public static IntPtr FindWindowByTitle(string titleContains) {
        _found = IntPtr.Zero;
        _titleFilter = titleContains;
        _classFilter = null;
        EnumWindows(EnumProc, IntPtr.Zero);
        return _found;
    }

    public static IntPtr FindBrowserWindowByTitle(string titleContains) {
        _found = IntPtr.Zero;
        _titleFilter = titleContains;
        _classFilter = "Chrome_WidgetWin";
        IntPtr result = IntPtr.Zero;
        EnumWindows(EnumProc, IntPtr.Zero);
        result = _found;
        if (result != IntPtr.Zero) return result;
        // Try Firefox
        _classFilter = "MozillaWindowClass";
        EnumWindows(EnumProc, IntPtr.Zero);
        return _found;
    }

    public static bool IsWindowTitleValid(IntPtr hWnd, string titleContains) {
        if (!IsWindow(hWnd)) return false;
        StringBuilder title = new StringBuilder(256);
        GetWindowText(hWnd, title, 256);
        return title.ToString().Contains(titleContains);
    }

    public static void SendSpace() {
        keybd_event(VK_SPACE, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(50);
        keybd_event(VK_SPACE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static bool FocusWindow(IntPtr hWnd) {
        try {
            // Alt-key trick for foreground lock
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(20);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
            return true;
        } catch {
            return false;
        }
    }
}
"@ -ReferencedAssemblies "System.Drawing"

# ============================================================
# Configuration
# ============================================================

$script:SessionsDir = "$env:USERPROFILE\.codex\sessions"
$script:LogDir = Join-Path (Split-Path $PSCommandPath -Parent) "logs"
$script:DouyinUrl = "https://www.douyin.com"
$script:PollIntervalMs = 700
$script:FileWindowHours = 48
$script:FocusDelayMs = 450
$script:RecallDelayMs = 120
$script:BootstrapStaleSeconds = 21600  # 6 hours

# ============================================================
# Helpers
# ============================================================

function Write-Log {
    param([string]$Message)
    if (-not (Test-Path $script:LogDir)) {
        New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
    }
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    $line | Out-File -FilePath "$script:LogDir\helper.log" -Append -Encoding utf8
}

Write-Log "launch"

# ============================================================
# State
# ============================================================

$script:Files = @{}        # path -> {offset, carry(byte[]), metadata, pendingLines}
$script:Bootstrapping = $true
$script:Enabled = $true
$script:HasPhase = $false
$script:Phase = $null      # 'working' | 'attention'
$script:Generation = 0
$script:ManagedSessionActive = $false
$script:PausedByHelper = $false

# ============================================================
# Icon rendering
# ============================================================

function New-TrayIcon {
    param([string]$Symbol)  # 'play' | 'pause'
    $bmp = New-Object System.Drawing.Bitmap(32, 32)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::Black)
    if ($Symbol -eq 'play') {
        $pts = @(
            [System.Drawing.Point]::new(10, 6),
            [System.Drawing.Point]::new(10, 26),
            [System.Drawing.Point]::new(24, 16)
        )
        $g.FillPolygon($brush, $pts)
    } else {
        $g.FillRectangle($brush, 8, 6, 6, 20)
        $g.FillRectangle($brush, 18, 6, 6, 20)
    }
    $g.Dispose()
    $brush.Dispose()
    $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
    # Return both — bitmap must stay alive while icon is in use
    return @{ Icon = $icon; Bitmap = $bmp }
}

$script:PlayIcon = New-TrayIcon -Symbol 'play'
$script:PauseIcon = New-TrayIcon -Symbol 'pause'

# ============================================================
# Tray icon + menu
# ============================================================

$script:NotifyIcon = New-Object "System.Windows.Forms.NotifyIcon"
$script:NotifyIcon.Icon = $script:PauseIcon.Icon
$script:NotifyIcon.Text = "Codex 抖音助手"
$script:NotifyIcon.Visible = $true

$script:StatusItem = New-Object "System.Windows.Forms.ToolStripMenuItem"
$script:StatusItem.Text = "正在监听 Codex..."
$script:StatusItem.Enabled = $false

$script:ToggleItem = New-Object "System.Windows.Forms.ToolStripMenuItem"
$script:ToggleItem.Text = "✓ 启用自动刷"

$script:OpenDouyinItem = New-Object "System.Windows.Forms.ToolStripMenuItem"
$script:OpenDouyinItem.Text = "打开抖音"

$script:ReturnCodexItem = New-Object "System.Windows.Forms.ToolStripMenuItem"
$script:ReturnCodexItem.Text = "暂停并回到 Codex"

$script:QuitItem = New-Object "System.Windows.Forms.ToolStripMenuItem"
$script:QuitItem.Text = "退出"

$script:ContextMenu = New-Object "System.Windows.Forms.ContextMenuStrip"
$script:ContextMenu.Items.Add($script:StatusItem) | Out-Null
$script:ContextMenu.Items.Add((New-Object "System.Windows.Forms.ToolStripSeparator")) | Out-Null
$script:ContextMenu.Items.Add($script:ToggleItem) | Out-Null
$script:ContextMenu.Items.Add((New-Object "System.Windows.Forms.ToolStripSeparator")) | Out-Null
$script:ContextMenu.Items.Add($script:OpenDouyinItem) | Out-Null
$script:ContextMenu.Items.Add($script:ReturnCodexItem) | Out-Null
$script:ContextMenu.Items.Add((New-Object "System.Windows.Forms.ToolStripSeparator")) | Out-Null
$script:ContextMenu.Items.Add($script:QuitItem) | Out-Null
$script:NotifyIcon.ContextMenuStrip = $script:ContextMenu

# --- Menu events ---

$script:ToggleItem.Add_Click({
    $script:Enabled = -not $script:Enabled
    $script:Generation++
    $script:ToggleItem.Text = if ($script:Enabled) { "✓ 启用自动刷" } else { "启用自动刷" }
    Update-Status
    Write-Log "automation $(if ($script:Enabled) {'enabled'} else {'disabled'})"
    if ($script:Enabled -and $script:HasPhase -and $script:Phase -eq 'working') {
        $gen = $script:Generation
        Start-Sleep -Milliseconds 100
        Begin-DouyinSession -Generation $gen
    }
})

$script:OpenDouyinItem.Add_Click({
    Start-Process $script:DouyinUrl
    Write-Log "launched browser for douyin"
})

$script:ReturnCodexItem.Add_Click({
    $script:Generation++
    $gen = $script:Generation
    Recall-ToCodex -Generation $gen
})

$script:QuitItem.Add_Click({
    $script:NotifyIcon.Visible = $false
    $script:Timer.Stop()
    Write-Log "terminate"
    [System.Windows.Forms.Application]::Exit()
})

# ============================================================
# Status update
# ============================================================

function Update-Status {
    if (-not $script:HasPhase) {
        $script:StatusItem.Text = "正在监听 Codex..."
        $script:NotifyIcon.Icon = $script:PauseIcon.Icon
    } elseif ($script:Phase -eq 'working') {
        if ($script:Enabled) {
            $script:StatusItem.Text = "Codex 工作中"
        } else {
            $script:StatusItem.Text = "Codex 工作中 · 自动刷已关闭"
        }
        $script:NotifyIcon.Icon = $script:PlayIcon.Icon
    } else {
        $script:StatusItem.Text = "Codex 等你反馈"
        $script:NotifyIcon.Icon = $script:PauseIcon.Icon
    }
    $script:NotifyIcon.Text = $script:StatusItem.Text
}

# ============================================================
# Session monitoring
# ============================================================

function Get-RecentSessionFiles {
    if (-not (Test-Path $script:SessionsDir)) { return @() }
    $cutoff = (Get-Date).AddHours(-$script:FileWindowHours)
    $results = @()
    Get-ChildItem -Path $script:SessionsDir -Recurse -Filter "*.jsonl" -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.LastWriteTime -ge $cutoff) {
            $results += $_.FullName
        }
    }
    return $results
}

function Read-NewLines {
    param([string]$Path)

    if (-not $script:Files.ContainsKey($Path)) {
        $script:Files[$Path] = @{
            Offset = 0
            Carry = [byte[]]@()
            Metadata = $null
            PendingLines = [System.Collections.ArrayList]@()
        }
    }
    $state = $script:Files[$Path]

    $size = (Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue).Length
    if (-not $size) { return @() }

    if ($size -lt $state.Offset) {
        $state.Offset = 0
        $state.Carry = [byte[]]@()
        $state.Metadata = $null
        $state.PendingLines = [System.Collections.ArrayList]@()
    }
    if ($size -le $state.Offset) { return @() }

    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $fs.Seek($state.Offset, [System.IO.SeekOrigin]::Begin) | Out-Null
    $newData = New-Object byte[] ($size - $state.Offset)
    $fs.Read($newData, 0, $newData.Length) | Out-Null
    $fs.Close()

    $state.Offset = $size
    $state.Carry += $newData

    # Split into lines
    $lines = @()
    $carry = $state.Carry
    $start = 0
    for ($i = 0; $i -lt $carry.Length; $i++) {
        if ($carry[$i] -eq 10) {  # '\n'
            if ($i -gt $start) {
                $lines += $carry[$start..($i-1)]
            }
            $start = $i + 1
        }
    }
    if ($start -lt $carry.Length) {
        $state.Carry = $carry[$start..($carry.Length-1)]
    } else {
        $state.Carry = [byte[]]@()
    }

    $events = @()
    foreach ($line in $lines) {
        $text = [System.Text.Encoding]::UTF8.GetString($line)
        if ($null -eq $state.Metadata) {
            $meta = ConvertFrom-MetaLine -Text $text
            if ($meta) {
                $state.Metadata = $meta
                if ($meta.UserThread) {
                    foreach ($pending in $state.PendingLines) {
                        $evt = ConvertFrom-EventLine -Text ([System.Text.Encoding]::UTF8.GetString($pending)) -Meta $meta
                        if ($evt) { $events += $evt }
                    }
                }
                $state.PendingLines = [System.Collections.ArrayList]@()
                continue
            }
            [void]$state.PendingLines.Add($line)
            continue
        }
        if (-not $state.Metadata.UserThread) { continue }
        $evt = ConvertFrom-EventLine -Text $text -Meta $state.Metadata
        if ($evt) { $events += $evt }
    }

    $script:Files[$Path] = $state
    return $events
}

function ConvertFrom-MetaLine {
    param([string]$Text)
    try { $obj = ConvertFrom-Json $Text -ErrorAction Stop } catch { return $null }
    if ($obj.type -ne 'session_meta') { return $null }

    $payload = $obj.payload
    if (-not $payload -or -not ($payload -is [PSObject])) { return $null }

    $threadId = if ($payload.id) { $payload.id } elseif ($payload.session_id) { $payload.session_id } else { 'unknown' }
    $isSubagent = $payload.thread_source -eq 'subagent'
    $hasParent = $null -ne $payload.parent_thread_id
    $source = $payload.source
    $sourceIsSubagent = ($source -is [PSObject]) -and ($null -ne $source.subagent)
    $userThread = -not $isSubagent -and -not $hasParent -and -not $sourceIsSubagent

    return @{ ThreadId = $threadId; UserThread = $userThread }
}

function ConvertFrom-EventLine {
    param([string]$Text, $Meta)
    try { $obj = ConvertFrom-Json $Text -ErrorAction Stop } catch { return $null }
    if (-not $obj.timestamp) { return $null }

    $ts = $null
    try { $ts = [DateTime]::Parse($obj.timestamp.Replace('Z', '')) } catch { return $null }

    $payload = $obj.payload
    if (-not $payload -or -not ($payload -is [PSObject])) { return $null }

    $outerType = $obj.type
    $payloadType = $payload.type
    $phase = $null

    if ($outerType -eq 'event_msg') {
        if ($payloadType -eq 'task_started') { $phase = 'working' }
        elseif ($payloadType -eq 'task_complete' -or $payloadType -eq 'turn_aborted') { $phase = 'attention' }
    } elseif ($outerType -eq 'response_item') {
        if ($payloadType -eq 'function_call' -or $payloadType -eq 'custom_tool_call') {
            $name = $payload.name
            if ($name -is [string] -and $name.Contains('request_user_input')) { $phase = 'attention' }
        }
    }

    if (-not $phase) { return $null }
    return @{ Phase = $phase; Timestamp = $ts; ThreadId = $Meta.ThreadId }
}

function Invoke-Scan {
    if (-not (Test-Path $script:SessionsDir)) { return }

    $events = @()
    foreach ($filepath in Get-RecentSessionFiles) {
        $events += Read-NewLines -Path $filepath
    }

    $events = $events | Sort-Object { $_.Timestamp }

    if ($script:Bootstrapping) {
        $script:Bootstrapping = $false
        if ($events.Count -gt 0) {
            Handle-Event -Event $events[-1] -InitialState $true
        }
        return
    }

    foreach ($event in $events) {
        Handle-Event -Event $event -InitialState $false
    }
}

# ============================================================
# Event handling
# ============================================================

function Handle-Event {
    param($Event, [bool]$InitialState)

    $script:HasPhase = $true
    $script:Phase = $Event.Phase
    $script:Generation++
    $gen = $script:Generation

    Write-Log "event phase=$($Event.Phase) initial=$InitialState"

    if ($Event.Phase -eq 'working') {
        if ($InitialState) {
            $age = ((Get-Date).ToUniversalTime() - $Event.Timestamp).TotalSeconds
            if ($age -gt $script:BootstrapStaleSeconds) { return }
        }
        Update-Status
        if ($script:Enabled) {
            Begin-DouyinSession -Generation $gen
        }
    } else {
        if ($InitialState) { return }
        Update-Status
        Recall-ToCodex -Generation $gen
    }
}

# ============================================================
# Window automation
# ============================================================

function Ensure-DouyinWindow {
    $hwnd = [Win32]::FindBrowserWindowByTitle("抖音")
    if ($hwnd -ne [IntPtr]::Zero -and [Win32]::IsWindowTitleValid($hwnd, "抖音")) {
        return $hwnd
    }

    Start-Process $script:DouyinUrl
    Write-Log "waiting for douyin window to appear"

    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 500
        $hwnd = [Win32]::FindBrowserWindowByTitle("抖音")
        if ($hwnd -ne [IntPtr]::Zero) {
            Write-Log "douyin window found hwnd=$hwnd"
            return $hwnd
        }
    }

    Write-Log "WARNING: douyin window not found after launch"
    return [IntPtr]::Zero
}

function Begin-DouyinSession {
    param([int]$Generation)

    if (-not $script:Enabled) {
        Write-Log "working ignored automation=disabled"
        return
    }

    Write-Log "working activate douyin generation=$Generation"
    $script:ManagedSessionActive = $true

    $hwnd = Ensure-DouyinWindow
    if ($hwnd -eq [IntPtr]::Zero) { return }

    # Cancel check
    if ($Generation -ne $script:Generation -or -not $script:HasPhase -or $script:Phase -ne 'working' -or -not $script:Enabled) { return }

    [Win32]::FocusWindow($hwnd)
    Start-Sleep -Milliseconds $script:FocusDelayMs

    # Cancel check
    if ($Generation -ne $script:Generation -or -not $script:HasPhase -or $script:Phase -ne 'working' -or -not $script:Enabled) { return }

    if ($script:PausedByHelper) {
        [Win32]::SendSpace()
        Write-Log "resume douyin with space"
        $script:PausedByHelper = $false
    }

    Update-Status
}

function Recall-ToCodex {
    param([int]$Generation)

    if ($script:ManagedSessionActive) {
        $hwnd = [Win32]::FindBrowserWindowByTitle("抖音")
        if ($hwnd -ne [IntPtr]::Zero -and [Win32]::IsWindowTitleValid($hwnd, "抖音")) {
            [Win32]::SendSpace()
            Write-Log "pause douyin with space"
            $script:PausedByHelper = $true
        }
    }
    $script:ManagedSessionActive = $false

    Write-Log "attention recall generation=$Generation"

    Start-Sleep -Milliseconds $script:RecallDelayMs

    if ($Generation -ne $script:Generation) { return }

    $codexHwnd = [Win32]::FindWindowByTitle("Codex")
    if ($codexHwnd -ne [IntPtr]::Zero) {
        [Win32]::FocusWindow($codexHwnd)
    }

    Update-Status
}

# ============================================================
# Self-tests
# ============================================================

if ($SelfTest) {
    Write-Host "=== Running self-tests ==="

    # User thread metadata
    $meta = ConvertFrom-MetaLine -Text '{"type":"session_meta","payload":{"id":"thread-1","thread_source":"user","source":"vscode"}}'
    if (-not $meta -or -not $meta.UserThread -or $meta.ThreadId -ne 'thread-1') {
        throw "user session decoding failed"
    }

    # Subagent
    $meta = ConvertFrom-MetaLine -Text '{"type":"session_meta","payload":{"id":"thread-2","thread_source":"subagent","parent_thread_id":"thread-1","source":{"subagent":{"other":"guardian"}}}}'
    if (-not $meta -or $meta.UserThread) {
        throw "subagent filtering failed"
    }

    # Source subagent
    $meta = ConvertFrom-MetaLine -Text '{"type":"session_meta","payload":{"id":"thread-3","thread_source":"user","source":{"subagent":true}}}'
    if (-not $meta -or $meta.UserThread) {
        throw "source subagent filtering failed"
    }

    $userMeta = @{ ThreadId = 't1'; UserThread = $true }

    # task_started
    $evt = ConvertFrom-EventLine -Text '{"timestamp":"2026-07-13T07:52:25.288Z","type":"event_msg","payload":{"type":"task_started"}}' -Meta $userMeta
    if (-not $evt -or $evt.Phase -ne 'working') { throw "task_started mapping failed" }

    # task_complete
    $evt = ConvertFrom-EventLine -Text '{"timestamp":"2026-07-13T07:52:25.288Z","type":"event_msg","payload":{"type":"task_complete"}}' -Meta $userMeta
    if (-not $evt -or $evt.Phase -ne 'attention') { throw "task_complete mapping failed" }

    # turn_aborted
    $evt = ConvertFrom-EventLine -Text '{"timestamp":"2026-07-13T07:52:25.288Z","type":"event_msg","payload":{"type":"turn_aborted"}}' -Meta $userMeta
    if (-not $evt -or $evt.Phase -ne 'attention') { throw "turn_aborted mapping failed" }

    # request_user_input
    $evt = ConvertFrom-EventLine -Text '{"timestamp":"2026-07-13T07:52:25.288Z","type":"response_item","payload":{"type":"function_call","name":"request_user_input"}}' -Meta $userMeta
    if (-not $evt -or $evt.Phase -ne 'attention') { throw "request_user_input mapping failed" }

    # custom_tool_call
    $evt = ConvertFrom-EventLine -Text '{"timestamp":"2026-07-13T07:52:25.288Z","type":"response_item","payload":{"type":"custom_tool_call","name":"codex_app.request_user_input"}}' -Meta $userMeta
    if (-not $evt -or $evt.Phase -ne 'attention') { throw "custom_tool_call mapping failed" }

    # malformed
    if ($null -ne (ConvertFrom-MetaLine -Text "nope")) { throw "malformed input failed" }

    Write-Host "Self-tests passed."
    exit 0
}

# ============================================================
# Diagnose
# ============================================================

if ($Diagnose) {
    $douyin = [Win32]::FindBrowserWindowByTitle("抖音")
    $codex = [Win32]::FindWindowByTitle("Codex")
    $fgHwnd = [Win32]::GetForegroundWindow()
    $fgSb = New-Object System.Text.StringBuilder(256)
    [Win32]::GetWindowText($fgHwnd, $fgSb, 256) | Out-Null
    $fg = $fgSb.ToString()

    Write-Host "douyin_window=$(if ($douyin -ne [IntPtr]::Zero) {'found'} else {'none'})"
    Write-Host "codex_window=$(if ($codex -ne [IntPtr]::Zero) {'found'} else {'none'})"
    Write-Host "foreground=$fg"
    Write-Host "sessions_dir_exists=$(Test-Path $script:SessionsDir)"
    Write-Host "log=$script:LogDir\helper.log"

    if (Test-Path $script:SessionsDir) {
        $cutoff = (Get-Date).AddHours(-48)
        $recent = (Get-ChildItem -Path $script:SessionsDir -Recurse -Filter "*.jsonl" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $cutoff }).Count
        Write-Host "recent_session_files=$recent"
    }
    exit 0
}

# ============================================================
# Main — timer + message loop
# ============================================================

# Manual polling loop — more reliable than Timer + Application.Run()
# Use DoEvents to keep the tray icon responsive

Update-Status
Write-Log "running"

while ($true) {
    try {
        Invoke-Scan
    } catch {
        Write-Log "scan error: $_"
    }
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds $script:PollIntervalMs
}

