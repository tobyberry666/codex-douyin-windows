using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Threading;
using System.Windows.Forms;

static class Win32 {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint dwProcessId);
    [DllImport("user32.dll")] public static extern bool LockSetForegroundWindow(uint uLockCode);
    public const uint ASFW_ANY = 0xFFFFFFFF;
    public const uint LSFW_UNLOCK = 2;
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hWnd);
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;

    // ChatGPT（原 Codex，合体后窗口/进程名即 ChatGPT）窗口匹配候选。
    // 标题匹配用 CodexWindowTitles；进程名匹配用 CodexProcessNames（不依赖标题文字，作兜底）。
    // 集中定义，避免散落在 RecallToCodex / Diagnose 多处不同步（见 CLAUDE.md 操作红线第 4 条）。
    public static readonly string[] CodexWindowTitles = new string[] { "ChatGPT", "Codex" };
    public static readonly string[] CodexProcessNames = new string[] { "ChatGPT", "Codex" };

    public const int SW_RESTORE = 9;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const byte VK_SPACE = 0x20;
    public const byte VK_MENU = 0x12;
    public const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2000;
    public const uint SPIF_SENDCHANGE = 0x0002;

    // 任务栏图标闪烁提醒（用于无法强抢焦点的场景：独占全屏游戏/视频，系统禁止其他进程抢前台）
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO {
        public int cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pfwi);
    private const uint FLASHW_ALL = 0x3;
    private const uint FLASHW_TIMERNOFG = 0xC; // 闪烁直到该窗口再次成为前台

    public static void FlashWindow(IntPtr hWnd) {
        if (hWnd == IntPtr.Zero) return;
        try {
            FLASHWINFO fi = new FLASHWINFO();
            fi.cbSize = Marshal.SizeOf(typeof(FLASHWINFO));
            fi.hwnd = hWnd;
            fi.dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG;
            fi.uCount = 5;
            fi.dwTimeout = 0;
            FlashWindowEx(ref fi);
        } catch (Exception ex) { Log("flash failed: " + ex.Message); }
    }

    public static IntPtr FindBrowserWindow(string titleContains) {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (!IsWindow(hWnd)) return true;
            var sb = new StringBuilder(256); GetWindowText(hWnd, sb, 256);
            if (!sb.ToString().Contains(titleContains)) return true;
            var csb = new StringBuilder(256); GetClassName(hWnd, csb, 256);
            if (!csb.ToString().Contains("Chrome_WidgetWin") && !csb.ToString().Contains("MozillaWindowClass")) return true;
            result = hWnd; return false;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindWindowByTitle(params string[] candidates) {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam) {
            if (!IsWindow(hWnd)) return true;
            var sb = new StringBuilder(256); GetWindowText(hWnd, sb, 256);
            string title = sb.ToString();
            foreach (string c in candidates) {
                if (title.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) { result = hWnd; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindWindowByProcessName(params string[] candidates) {
        IntPtr result = IntPtr.Zero;
        try {
            foreach (Process p in Process.GetProcesses()) {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                string pn = p.ProcessName;
                foreach (string c in candidates) {
                    if (pn.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) {
                        result = p.MainWindowHandle;
                        return result;
                    }
                }
            }
        } catch (Exception ex) { Log("find by process failed: " + ex.Message); }
        return result;
    }

    static void Log(string m) {
        try {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexDouyinHelper");
            Directory.CreateDirectory(d);
            File.AppendAllText(Path.Combine(d, "helper.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + "\n");
        } catch { }
    }

    public static bool IsWindowTitleValid(IntPtr hWnd, string titleContains) {
        if (!IsWindow(hWnd)) return false;
        var sb = new StringBuilder(256); GetWindowText(hWnd, sb, 256);
        return sb.ToString().Contains(titleContains);
    }

    public static void SendSpace() {
        keybd_event(VK_SPACE, 0, 0, UIntPtr.Zero); Thread.Sleep(50);
        keybd_event(VK_SPACE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // 把指定窗口带到前台。从后台线程（定时器）调用也能工作。
    // 核心：Windows 有「前台锁」——若用户刚在别的窗口操作过，后台进程直接 SetForegroundWindow 会被拒（只闪任务栏）。
    // 正解是把「前台线程」的输入队列桥接到「本线程」（AttachThreadInput），
    // 这样系统便认为本线程有资格置前，对任何前台窗口（浏览器/其他应用/桌面）都生效。
    // 最后用 SwitchToThisWindow 兜底（任务管理器同款强制置前）。
    public static bool FocusWindow(IntPtr hWnd) {
        if (hWnd == IntPtr.Zero) return false;
        try {
            // 解除前台锁超时（仅当前会话生效，失败不影响后续）
            try { SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, SPIF_SENDCHANGE); }
            catch (Exception ex) { Log("spi fglock failed: " + ex.Message); }
            ShowWindow(hWnd, SW_RESTORE);
            // 桥接输入队列：把前台线程挂到本线程
            uint dummyPid;
            uint foreThread = GetWindowThreadProcessId(GetForegroundWindow(), out dummyPid);
            uint selfThread = GetCurrentThreadId();
            bool attached = false;
            if (foreThread != 0 && foreThread != selfThread) {
                try { attached = AttachThreadInput(foreThread, selfThread, true); }
                catch (Exception ex) { Log("attach failed: " + ex.Message); }
            }
            try {
                SetForegroundWindow(hWnd);
                SetActiveWindow(hWnd);
                SetFocus(hWnd);
                SetWindowPos(hWnd, HWND_TOP, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);
            } finally {
                if (attached) {
                    try { AttachThreadInput(foreThread, selfThread, false); }
                    catch { }
                }
            }
            if (GetForegroundWindow() == hWnd) { Log("focus ok (attach)"); return true; }
            // 兜底：强制切换（对桌面/其他进程也有效）
            for (int i = 0; i < 2; i++) {
                SwitchToThisWindow(hWnd, true);
                if (GetForegroundWindow() == hWnd) { Log("focus ok (switch)"); return true; }
                Thread.Sleep(40);
            }
            bool ok = GetForegroundWindow() == hWnd;
            Log("focus result=" + ok);
            return ok;
        } catch (Exception ex) { Log("focus failed: " + ex.Message); return false; }
    }

    public static string GetForegroundTitle() {
        var hWnd = GetForegroundWindow();
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, 256);
        return sb.ToString();
    }
}

class Event { public string Phase; public DateTime Timestamp; public string ThreadId; }

class SessionMonitor {
    const int PollIntervalMs = 700, FileWindowHours = 48;
    public class SessionMetadata { public string ThreadId; public bool UserThread; }
    class FileState {
        public long Offset;
        public byte[] Carry = new byte[0];
        public SessionMetadata Metadata;
        public List<byte[]> PendingLines = new List<byte[]>();
    }
    public event Action<Event, bool> OnEvent;
    Dictionary<string, FileState> _files = new Dictionary<string, FileState>();
    bool _bootstrapping = true, _running;
    Thread _thread;
    string _sessionsDir;

    public SessionMonitor() {
        _sessionsDir = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\.codex\sessions");
    }

    public void Start() {
        _running = true;
        _thread = new Thread(PollLoop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop() { _running = false; }

    void PollLoop() {
        while (_running) {
            try { Scan(); }
            catch (Exception ex) { Log("scan error: " + ex.Message); }
            Thread.Sleep(PollIntervalMs);
        }
    }

    void Scan() {
        if (!Directory.Exists(_sessionsDir)) return;
        var events = new List<Event>();
        foreach (var fp in RecentSessionFiles())
            events.AddRange(ReadNewLines(fp));
        events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        if (_bootstrapping) {
            _bootstrapping = false;
            if (events.Count > 0 && OnEvent != null)
                OnEvent(events[events.Count - 1], true);
            return;
        }
        foreach (var e in events)
            if (OnEvent != null) OnEvent(e, false);
    }

    IEnumerable<string> RecentSessionFiles() {
        var cutoff = DateTime.Now.AddHours(-FileWindowHours);
        var results = new List<string>();
        try {
            foreach (var fp in Directory.EnumerateFiles(_sessionsDir, "*.jsonl", SearchOption.AllDirectories))
                if (File.GetLastWriteTime(fp) >= cutoff) results.Add(fp);
        } catch (Exception ex) { Log("enumerate sessions failed: " + ex.Message); }
        return results;
    }

    FileState GetOrCreateFileState(string path) {
        FileState state;
        if (_files.TryGetValue(path, out state)) return state;
        state = new FileState();
        _files[path] = state;
        return state;
    }

    long GetFileSize(string path, out bool ok) {
        ok = false;
        try {
            long size = new FileInfo(path).Length;
            ok = true;
            return size;
        } catch (Exception ex) { Log("get file size failed: " + ex.Message); return 0; }
    }

    byte[] ReadBytes(string path, long offset, long size, out bool ok) {
        ok = false;
        try {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                fs.Seek(offset, SeekOrigin.Begin);
                byte[] buffer = new byte[size - offset];
                fs.Read(buffer, 0, buffer.Length);
                ok = true;
                return buffer;
            }
        } catch (Exception ex) { Log("read file failed: " + ex.Message); return null; }
    }

    static byte[] Combine(byte[] carry, byte[] newData) {
        byte[] result = new byte[carry.Length + newData.Length];
        Array.Copy(carry, 0, result, 0, carry.Length);
        Array.Copy(newData, 0, result, carry.Length, newData.Length);
        return result;
    }

    static List<byte[]> SplitLines(byte[] carry, out byte[] remaining) {
        var lines = new List<byte[]>();
        int start = 0;
        for (int i = 0; i < carry.Length; i++) {
            if (carry[i] == '\n') {
                if (i > start) {
                    byte[] line = new byte[i - start];
                    Array.Copy(carry, start, line, 0, i - start);
                    lines.Add(line);
                }
                start = i + 1;
            }
        }
        remaining = start < carry.Length ? carry.Skip(start).ToArray() : new byte[0];
        return lines;
    }

    List<Event> DecodeLines(FileState state, List<byte[]> lines) {
        var events = new List<Event>();
        foreach (var line in lines) {
            if (state.Metadata == null) {
                var meta = DecodeMetadata(line);
                if (meta != null) {
                    state.Metadata = meta;
                    if (meta.UserThread) {
                        foreach (var pl in state.PendingLines) {
                            var ev = DecodeStateEvent(pl, meta);
                            if (ev != null) events.Add(ev);
                        }
                    }
                    state.PendingLines.Clear();
                    continue;
                }
                state.PendingLines.Add(line);
                continue;
            }
            if (!state.Metadata.UserThread) continue;
            var ev2 = DecodeStateEvent(line, state.Metadata);
            if (ev2 != null) events.Add(ev2);
        }
        return events;
    }

    List<Event> ReadNewLines(string path) {
        FileState state = GetOrCreateFileState(path);
        bool sizeOk;
        long size = GetFileSize(path, out sizeOk);
        if (!sizeOk) return new List<Event>();
        if (size < state.Offset) {
            state = new FileState();
            _files[path] = state;
        }
        if (size <= state.Offset) return new List<Event>();

        bool readOk;
        byte[] newData = ReadBytes(path, state.Offset, size, out readOk);
        if (!readOk) return new List<Event>();

        state.Offset += newData.Length;
        byte[] carry = Combine(state.Carry, newData);
        byte[] remaining;
        List<byte[]> lines = SplitLines(carry, out remaining);
        state.Carry = remaining;
        _files[path] = state;

        return DecodeLines(state, lines);
    }

    #pragma warning disable 0618
    // 目标框架为 .NET Framework 4.x，运行环境无 NuGet / dotnet 还原能力，
    // System.Text.Json 无法作为引用程序集被 Add-Type 解析。故改用框架内置的
    // System.Web.Script.Serialization.JavaScriptSerializer（GAC 自带，零依赖、
    // 零下载、零 dotnet）。若未来迁移到现代 .NET（dotnet build / csproj + NuGet），
    // 应改回 System.Text.Json。时间戳仍按 UTC 解析（见 DecodeStateEvent）。
    static readonly JavaScriptSerializer Js = new JavaScriptSerializer();
    #pragma warning restore 0618

    static string AsStr(object o) {
        return o as string;
    }

    static string GetThreadId(Dictionary<string, object> payload) {
        object idv;
        if (payload.TryGetValue("id", out idv)) {
            string ids = idv as string;
            if (ids != null && ids.Length > 0) return ids;
        }
        object sidv;
        if (payload.TryGetValue("session_id", out sidv)) {
            string sids = sidv as string;
            if (sids != null && sids.Length > 0) return sids;
        }
        return "unknown";
    }

    public static SessionMetadata DecodeMetadata(byte[] lineData) {
        Dictionary<string, object> root;
        try {
            root = Js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(lineData));
        } catch (Exception ex) { Log("decode metadata failed: " + ex.Message); return null; }
        if (root == null) return null;
        object typev;
        if (!root.TryGetValue("type", out typev) || AsStr(typev) != "session_meta") return null;
        object payloadv;
        if (!root.TryGetValue("payload", out payloadv)) return null;
        Dictionary<string, object> payload = payloadv as Dictionary<string, object>;
        if (payload == null) return null;

        var tid = GetThreadId(payload);
        object tsv;
        bool sub = payload.TryGetValue("thread_source", out tsv) && AsStr(tsv) == "subagent";
        bool hasParent = payload.ContainsKey("parent_thread_id");
        object srcv;
        bool srcSub = payload.TryGetValue("source", out srcv);
        if (srcSub) {
            Dictionary<string, object> src = srcv as Dictionary<string, object>;
            srcSub = src != null && src.ContainsKey("subagent");
        }

        return new SessionMetadata { ThreadId = tid, UserThread = !sub && !hasParent && !srcSub };
    }

    public static Event DecodeStateEvent(byte[] lineData, SessionMetadata meta) {
        Dictionary<string, object> root;
        try {
            root = Js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(lineData));
        } catch (Exception ex) { Log("decode state event failed: " + ex.Message); return null; }
        if (root == null) return null;

        object tsv;
        if (!root.TryGetValue("timestamp", out tsv)) return null;
        string tsStr = tsv as string;
        if (tsStr == null) return null;
        DateTime ts;
        try {
            ts = DateTime.Parse(tsStr, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        } catch (Exception ex) { Log("parse timestamp failed: " + ex.Message); return null; }

        object payloadv;
        if (!root.TryGetValue("payload", out payloadv)) return null;
        Dictionary<string, object> payload = payloadv as Dictionary<string, object>;
        if (payload == null) return null;

        object otv;
        string ot = root.TryGetValue("type", out otv) ? (AsStr(otv) ?? "") : "";
        object ptv;
        string pt = payload.TryGetValue("type", out ptv) ? (AsStr(ptv) ?? "") : "";

        string phase = null;
        if (ot == "event_msg") {
            if (pt == "task_started") phase = "working";
            else if (pt == "task_complete" || pt == "turn_aborted") phase = "attention";
        } else if (ot == "response_item") {
            if (pt == "function_call" || pt == "custom_tool_call") {
                object namev;
                string name = payload.TryGetValue("name", out namev) ? (AsStr(namev) ?? "") : "";
                if (name.Contains("request_user_input")) phase = "attention";
            }
        }

        if (phase == null) return null;
        return new Event { Phase = phase, Timestamp = ts, ThreadId = meta != null ? meta.ThreadId : "unknown" };
    }

    static void Log(string m) {
        try {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexDouyinHelper");
            Directory.CreateDirectory(d);
            File.AppendAllText(Path.Combine(d, "helper.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + "\n");
        } catch { }
    }
}

class TrayApp : ApplicationContext {
    NotifyIcon _icon;
    ToolStripMenuItem _statusItem, _toggleItem;
    SessionMonitor _monitor;
    bool _enabled = true, _hasPhase, _managedSessionActive, _pausedByHelper;
    string _phase;
    int _generation;
    readonly object _lock = new object();
    const string DouyinUrl = "https://www.douyin.com", DOUYIN_TITLE = "\u6296\u97f3";
    const int FocusDelayMs = 450, RecallDelayMs = 120, BootstrapStaleSeconds = 21600;

    public TrayApp() {
        _monitor = new SessionMonitor();
        _monitor.OnEvent += HandleEvent;
        BuildTrayIcon();
        _monitor.Start();
        UpdateStatus();
        Log("running");
    }

    void BuildTrayIcon() {
        _statusItem = new ToolStripMenuItem("\u6b63\u5728\u76d1\u542c ChatGPT...") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("\u2713 \u542f\u7528\u81ea\u52a8\u5237");
        var openItem = new ToolStripMenuItem("\u6253\u5f00\u6296\u97f3");
        var returnItem = new ToolStripMenuItem("\u6682\u505c\u5e76\u56de\u5230 ChatGPT");
        var quitItem = new ToolStripMenuItem("\u9000\u51fa");

        _toggleItem.Click += (s, e) => {
            lock (_lock) { _enabled = !_enabled; _generation++; }
            UpdateStatus();
            Log("automation " + (_enabled ? "enabled" : "disabled"));
            if (_enabled && _hasPhase && _phase == "working")
                new Thread(() => BeginDouyinSession()) { IsBackground = true }.Start();
        };
        openItem.Click += (s, e) => {
            try {
                Process.Start(new ProcessStartInfo(DouyinUrl) { UseShellExecute = true });
                Log("launched browser for douyin");
            } catch (Exception ex) { Log("launch douyin failed: " + ex.Message); }
        };
        returnItem.Click += (s, e) => {
            lock (_lock) _generation++;
            new Thread(() => RecallToCodex(true)) { IsBackground = true }.Start();
        };
        quitItem.Click += (s, e) => {
            _icon.Visible = false;
            _monitor.Stop();
            Log("terminate");
            Application.Exit();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(openItem);
        menu.Items.Add(returnItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        _icon = new NotifyIcon {
            Icon = MakeIcon("pause"),
            Text = "ChatGPT \u6296\u97f3\u52a9\u624b",
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    void UpdateStatus() {
        string text, symbol;
        if (!_hasPhase) {
            text = "\u6b63\u5728\u76d1\u542c ChatGPT..."; symbol = "pause";
        } else if (_phase == "working") {
            text = _enabled ? "ChatGPT \u5de5\u4f5c\u4e2d" : "ChatGPT \u5de5\u4f5c\u4e2d \u00b7 \u81ea\u52a8\u5237\u5df2\u5173\u95ed";
            symbol = "play";
        } else {
            text = "ChatGPT \u7b49\u4f60\u53cd\u9988"; symbol = "pause";
        }
        _icon.Text = text;
        _icon.Icon = MakeIcon(symbol);
        _statusItem.Text = text;
        _toggleItem.Text = _enabled ? "\u2713 \u542f\u7528\u81ea\u52a8\u5237" : "\u542f\u7528\u81ea\u52a8\u5237";
    }

    static Icon MakeIcon(string symbol) {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(Color.Black)) {
            if (symbol == "play")
                g.FillPolygon(brush, new[] { new Point(10, 6), new Point(10, 26), new Point(24, 16) });
            else {
                g.FillRectangle(brush, 8, 6, 6, 20);
                g.FillRectangle(brush, 18, 6, 6, 20);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    void HandleEvent(Event ev, bool initialState) {
        lock (_lock) { _hasPhase = true; _phase = ev.Phase; _generation++; }
        Log("event phase=" + ev.Phase + " initial=" + initialState);
        InvokeUI(() => UpdateStatus());
        if (ev.Phase == "working") {
            if (initialState && (DateTime.UtcNow - ev.Timestamp).TotalSeconds > BootstrapStaleSeconds) return;
            if (_enabled) new Thread(() => BeginDouyinSession()) { IsBackground = true }.Start();
        } else {
            if (initialState) return;
            new Thread(() => RecallToCodex()) { IsBackground = true }.Start();
        }
    }

    void InvokeUI(Action action) {
        var parent = _statusItem.GetCurrentParent();
        if (parent != null && parent.InvokeRequired) parent.Invoke(action);
        else action();
    }

    IntPtr EnsureDouyinWindow() {
        var hwnd = Win32.FindBrowserWindow(DOUYIN_TITLE);
        if (hwnd != IntPtr.Zero && Win32.IsWindowTitleValid(hwnd, DOUYIN_TITLE)) return hwnd;
        try {
            Process.Start(new ProcessStartInfo(DouyinUrl) { UseShellExecute = true });
        } catch (Exception ex) { Log("launch douyin failed: " + ex.Message); }
        Log("waiting for douyin window");
        for (int i = 0; i < 20; i++) {
            Thread.Sleep(500);
            hwnd = Win32.FindBrowserWindow(DOUYIN_TITLE);
            if (hwnd != IntPtr.Zero) { Log("douyin window found hwnd=" + hwnd); return hwnd; }
        }
        Log("WARNING: douyin window not found");
        return IntPtr.Zero;
    }

    bool IsForegroundDouyin() {
        IntPtr fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var sb = new StringBuilder(256);
        Win32.GetWindowText(fg, sb, 256);
        if (sb.ToString().IndexOf(DOUYIN_TITLE, StringComparison.OrdinalIgnoreCase) < 0) return false;
        var csb = new StringBuilder(256);
        Win32.GetClassName(fg, csb, 256);
        string cls = csb.ToString();
        return cls.Contains("Chrome_WidgetWin") || cls.Contains("MozillaWindowClass");
    }

    void BeginDouyinSession() {
        lock (_lock) { if (!_enabled) { Log("working ignored"); return; } }
        // working 阶段始终自动切到抖音（老程序行为：ChatGPT 干活时帮你刷抖音）
        // 「是否在前台抖音」只用于完成时决定是否暂停，见 RecallToCodex
        Log("working activate douyin");
        int gen;
        lock (_lock) _managedSessionActive = true;
        lock (_lock) gen = _generation;

        var hwnd = EnsureDouyinWindow();
        if (hwnd == IntPtr.Zero) return;

        lock (_lock) { if (gen != _generation || !_hasPhase || _phase != "working" || !_enabled) return; }
        Win32.FocusWindow(hwnd);
        Thread.Sleep(FocusDelayMs);

        lock (_lock) { if (gen != _generation || !_hasPhase || _phase != "working" || !_enabled) return; }
        if (_pausedByHelper) {
            Win32.SendSpace();
            Log("resume douyin with space");
            lock (_lock) _pausedByHelper = false;
        }
        InvokeUI(() => UpdateStatus());
    }

    void RecallToCodex(bool forcePause = false) {
        int gen;
        lock (_lock) gen = _generation;
        lock (_lock) {
            bool onDouyin = IsForegroundDouyin();
            // 自动返回时，仅当「本工具接管过抖音且用户仍在抖音」才暂停，避免误发空格到别的窗口；
            // 手动触发(forcePause)则只要抖音窗口存在就暂停。
            bool shouldPause = forcePause || (_managedSessionActive && onDouyin);
            if (shouldPause) {
                var hwnd = Win32.FindBrowserWindow(DOUYIN_TITLE);
                if (hwnd != IntPtr.Zero && Win32.IsWindowTitleValid(hwnd, DOUYIN_TITLE)) {
                    Win32.SendSpace();
                    Log("pause douyin with space");
                    _pausedByHelper = true;
                }
            }
        }
        lock (_lock) _managedSessionActive = false;
        Log("attention recall generation=" + gen);
        Thread.Sleep(RecallDelayMs);
        lock (_lock) { if (gen != _generation) return; }

        var codexHwnd = Win32.FindWindowByTitle(Win32.CodexWindowTitles);
        if (codexHwnd == IntPtr.Zero) codexHwnd = Win32.FindWindowByProcessName(Win32.CodexProcessNames);
        Log("codex hwnd=" + (codexHwnd != IntPtr.Zero ? codexHwnd.ToString() : "NOT FOUND"));
        if (codexHwnd != IntPtr.Zero) {
            bool ok = Win32.FocusWindow(codexHwnd);
            Log("focus codex=" + ok);
            if (!ok) {
                // 抢焦点失败：多半是前台处于独占全屏（游戏/全屏视频），系统禁止其他进程抢前台。
                // 改用任务栏图标闪烁提醒，用户切出全屏即可看到。
                Win32.FlashWindow(codexHwnd);
                Log("focus fallback: flashed taskbar (foreground likely exclusive fullscreen - cannot steal focus)");
            }
        }
        InvokeUI(() => UpdateStatus());
    }

    static void Log(string m) {
        try {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexDouyinHelper");
            Directory.CreateDirectory(d);
            File.AppendAllText(Path.Combine(d, "helper.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + "\n");
        } catch { }
    }
}

class Program {
    static string LogDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexDouyinHelper"); } }
    static string LogPath { get { return Path.Combine(LogDir, "helper.log"); } }

    static void Log(string m) {
        try {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + m + "\n");
        } catch { }
    }

    [STAThread]
    static void Main(string[] args) {
        if (args.Length > 0 && args[0] == "--self-test") { SelfTest(); return; }
        if (args.Length > 0 && args[0] == "--diagnose") { Diagnose(); return; }
        Log("launch");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
    }

    static void SelfTest() {
        Console.WriteLine("=== Running self-tests ===");

        var meta = SessionMonitor.DecodeMetadata(Encoding.UTF8.GetBytes(
            @"{""type"":""session_meta"",""payload"":{""id"":""thread-1"",""thread_source"":""user"",""source"":""vscode""}}"));
        Debug.Assert(meta != null && meta.UserThread && meta.ThreadId == "thread-1");

        meta = SessionMonitor.DecodeMetadata(Encoding.UTF8.GetBytes(
            @"{""type"":""session_meta"",""payload"":{""id"":""thread-2"",""thread_source"":""subagent"",""parent_thread_id"":""thread-1"",""source"":{""subagent"":{""other"":""guardian""}}}}"));
        Debug.Assert(meta != null && !meta.UserThread);

        meta = SessionMonitor.DecodeMetadata(Encoding.UTF8.GetBytes(
            @"{""type"":""session_meta"",""payload"":{""id"":""thread-3"",""thread_source"":""user"",""source"":{""subagent"":true}}}"));
        Debug.Assert(meta != null && !meta.UserThread);

        var um = new SessionMonitor.SessionMetadata { ThreadId = "t1", UserThread = true };

        Debug.Assert(SessionMonitor.DecodeStateEvent(Encoding.UTF8.GetBytes(
            @"{""timestamp"":""2026-07-13T07:52:25.288Z"",""type"":""event_msg"",""payload"":{""type"":""task_started""}}"), um).Phase == "working");
        Debug.Assert(SessionMonitor.DecodeStateEvent(Encoding.UTF8.GetBytes(
            @"{""timestamp"":""2026-07-13T07:52:25.288Z"",""type"":""event_msg"",""payload"":{""type"":""task_complete""}}"), um).Phase == "attention");
        Debug.Assert(SessionMonitor.DecodeStateEvent(Encoding.UTF8.GetBytes(
            @"{""timestamp"":""2026-07-13T07:52:25.288Z"",""type"":""event_msg"",""payload"":{""type"":""turn_aborted""}}"), um).Phase == "attention");
        Debug.Assert(SessionMonitor.DecodeStateEvent(Encoding.UTF8.GetBytes(
            @"{""timestamp"":""2026-07-13T07:52:25.288Z"",""type"":""response_item"",""payload"":{""type"":""function_call"",""name"":""request_user_input""}}"), um).Phase == "attention");
        Debug.Assert(SessionMonitor.DecodeStateEvent(Encoding.UTF8.GetBytes(
            @"{""timestamp"":""2026-07-13T07:52:25.288Z"",""type"":""response_item"",""payload"":{""type"":""custom_tool_call"",""name"":""codex_app.request_user_input""}}"), um).Phase == "attention");
        Debug.Assert(SessionMonitor.DecodeStateEvent(Encoding.UTF8.GetBytes(
            @"{""timestamp"":""not-a-timestamp"",""type"":""event_msg"",""payload"":{""type"":""task_started""}}"), um) == null);
        Debug.Assert(SessionMonitor.DecodeStateEvent(Encoding.UTF8.GetBytes(
            @"{""timestamp"":""2026-07-13T07:52:25.288Z"",""type"":""unrelated_event"",""payload"":{""type"":""task_started""}}"), um) == null);

        Debug.Assert(SessionMonitor.DecodeMetadata(Encoding.UTF8.GetBytes("nope")) == null);
        Console.WriteLine("Self-tests passed.");
    }

    static void Diagnose() {
        Console.WriteLine("douyin_window=" + (Win32.FindBrowserWindow("\u6296\u97f3") != IntPtr.Zero ? "found" : "none"));
        var byTitle = Win32.FindWindowByTitle(Win32.CodexWindowTitles);
        var byProc = Win32.FindWindowByProcessName(Win32.CodexProcessNames);
        Console.WriteLine("codex_window_by_title=" + (byTitle != IntPtr.Zero ? "found" : "none"));
        Console.WriteLine("codex_window_by_process=" + (byProc != IntPtr.Zero ? "found" : "none"));
        Console.WriteLine("foreground=" + Win32.GetForegroundTitle() + " is_douyin=" + (Win32.GetForegroundTitle().IndexOf("\u6296\u97f3", StringComparison.OrdinalIgnoreCase) >= 0));
        var sd = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\.codex\sessions");
        Console.WriteLine("sessions_dir_exists=" + Directory.Exists(sd));
        Console.WriteLine("log=" + LogPath);
        if (Directory.Exists(sd)) {
            var cutoff = DateTime.Now.AddHours(-48);
            Console.WriteLine("recent_session_files=" + Directory.EnumerateFiles(sd, "*.jsonl", SearchOption.AllDirectories)
                .Count(f => File.GetLastWriteTime(f) >= cutoff));
        }
    }
}
