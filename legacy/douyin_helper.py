#!/usr/bin/env python3
"""
Codex 抖音助手 - Windows 版
基于 yekwennnn/codex-douyin-helper macOS 版移植

在 Codex 工作时自动切换到抖音，完成后自动暂停抖音并切回 Codex。
状态判断通过读取 ~/.codex/sessions 中的会话事件实现。
"""

import os
import sys
import json
import time
import logging
import threading
import webbrowser
from datetime import datetime, timedelta, timezone

import win32gui
import win32api
import win32con
from PIL import Image, ImageDraw
import pystray

# ============================================================
# Constants — values mirror the macOS version exactly
# ============================================================

SESSIONS_DIR = os.path.expandvars(r"%USERPROFILE%\.codex\sessions")
LOG_DIR = os.path.expandvars(r"%LOCALAPPDATA%\CodexDouyinHelper")
DOUYIN_URL = "https://www.douyin.com"

VK_SPACE = 0x20
VK_MENU = 0x12

POLL_INTERVAL_SEC = 0.7
FILE_WINDOW_HOURS = 48
FOCUS_DELAY_SEC = 0.45
RECALL_DELAY_SEC = 0.12
BOOTSTRAP_STALE_SEC = 6 * 60 * 60


def setup_logging():
    os.makedirs(LOG_DIR, exist_ok=True)
    log_path = os.path.join(LOG_DIR, "helper.log")
    logging.basicConfig(
        filename=log_path,
        level=logging.DEBUG,
        format="%(asctime)s %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
        force=True,
    )


setup_logging()


# ============================================================
# Event — simple value object, mirrors DYStateEvent
# ============================================================

class Event:
    def __init__(self, phase, timestamp, thread_id):
        self.phase = phase
        self.timestamp = timestamp
        self.thread_id = thread_id


# ============================================================
# SessionMonitor — reads Codex JSONL session files
# ============================================================

class SessionMonitor:
    def __init__(self, event_handler):
        self.event_handler = event_handler
        self.files = {}
        self.bootstrapping = True
        self.running = False

    def start(self):
        self.running = True
        t = threading.Thread(target=self._poll_loop, daemon=True)
        t.start()

    def stop(self):
        self.running = False

    def _poll_loop(self):
        while self.running:
            try:
                self._scan()
            except Exception:
                logging.exception("scan error")
            time.sleep(POLL_INTERVAL_SEC)

    def _scan(self):
        if not os.path.isdir(SESSIONS_DIR):
            return

        events = []
        for filepath in self._recent_session_files():
            events.extend(self._read_new_lines(filepath))

        events.sort(key=lambda e: e.timestamp)

        if self.bootstrapping:
            self.bootstrapping = False
            if events:
                self.event_handler(events[-1], initial_state=True)
            return

        for event in events:
            self.event_handler(event, initial_state=False)

    def _recent_session_files(self):
        cutoff = datetime.now() - timedelta(hours=FILE_WINDOW_HOURS)
        results = []
        for root, _dirs, files in os.walk(SESSIONS_DIR):
            for f in files:
                if not f.endswith(".jsonl"):
                    continue
                path = os.path.join(root, f)
                try:
                    mtime = datetime.fromtimestamp(os.path.getmtime(path))
                    if mtime >= cutoff:
                        results.append(path)
                except OSError:
                    pass
        return results

    def _read_new_lines(self, path):
        state = self.files.get(path, {
            "offset": 0,
            "carry": b"",
            "metadata": None,
            "pending_lines": [],
        })

        try:
            current_size = os.path.getsize(path)
        except OSError:
            return []

        if current_size < state["offset"]:
            state = {"offset": 0, "carry": b"", "metadata": None, "pending_lines": []}

        if current_size <= state["offset"]:
            self.files[path] = state
            return []

        try:
            with open(path, "rb") as f:
                f.seek(state["offset"])
                new_data = f.read()
        except OSError:
            return []

        state["offset"] += len(new_data)
        state["carry"] += new_data

        lines = []
        data = state["carry"]
        start = 0
        for i in range(len(data)):
            if data[i] == 0x0A:
                if i > start:
                    lines.append(data[start:i])
                start = i + 1

        state["carry"] = data[start:] if start < len(data) else b""

        events = []
        for line in lines:
            if state["metadata"] is None:
                meta = self._decode_metadata(line)
                if meta is not None:
                    state["metadata"] = meta
                    if meta["user_thread"]:
                        for pending in state["pending_lines"]:
                            event = self._decode_state_event(pending, meta)
                            if event:
                                events.append(event)
                    state["pending_lines"] = []
                    continue
                state["pending_lines"].append(line)
                continue

            if not state["metadata"]["user_thread"]:
                continue

            event = self._decode_state_event(line, state["metadata"])
            if event:
                events.append(event)

        self.files[path] = state
        return events

    @staticmethod
    def _decode_metadata(line_data):
        try:
            obj = json.loads(line_data)
        except json.JSONDecodeError:
            return None

        if obj.get("type") != "session_meta":
            return None

        payload = obj.get("payload", {})
        if not isinstance(payload, dict):
            return None

        thread_id = payload.get("id") or payload.get("session_id") or "unknown"
        is_subagent = payload.get("thread_source") == "subagent"
        has_parent = "parent_thread_id" in payload
        source = payload.get("source", {})
        source_is_subagent = isinstance(source, dict) and "subagent" in source
        user_thread = not is_subagent and not has_parent and not source_is_subagent

        return {"thread_id": thread_id, "user_thread": user_thread}

    @staticmethod
    def _decode_state_event(line_data, metadata):
        try:
            obj = json.loads(line_data)
        except json.JSONDecodeError:
            return None

        timestamp_str = obj.get("timestamp")
        if not timestamp_str:
            return None

        try:
            timestamp = datetime.fromisoformat(timestamp_str.replace("Z", "+00:00"))
        except ValueError:
            return None

        payload = obj.get("payload", {})
        if not isinstance(payload, dict):
            return None

        outer_type = obj.get("type")
        payload_type = payload.get("type")
        phase = None

        if outer_type == "event_msg":
            if payload_type == "task_started":
                phase = "working"
            elif payload_type in ("task_complete", "turn_aborted"):
                phase = "attention"
        elif outer_type == "response_item":
            if payload_type in ("function_call", "custom_tool_call"):
                name = payload.get("name", "")
                if "request_user_input" in name:
                    phase = "attention"

        if phase is None:
            return None

        return Event(phase=phase, timestamp=timestamp, thread_id=metadata["thread_id"])


# ============================================================
# WindowManager — browser, window finding, focus, keys
# ============================================================

class WindowManager:
    @staticmethod
    def launch_douyin():
        webbrowser.open(DOUYIN_URL)
        logging.info("launched browser for douyin")

    @staticmethod
    def find_douyin_window():
        result = []

        def callback(hwnd, _lparam):
            if not win32gui.IsWindowVisible(hwnd):
                return True
            title = win32gui.GetWindowText(hwnd)
            if title and "抖音" in title:
                class_name = win32gui.GetClassName(hwnd)
                if any(c in class_name for c in ("Chrome_WidgetWin", "MozillaWindowClass")):
                    result.append(hwnd)
                    return False
            return True

        win32gui.EnumWindows(callback, None)
        return result[0] if result else None

    @staticmethod
    def is_window_valid(hwnd):
        try:
            if not win32gui.IsWindow(hwnd):
                return False
            title = win32gui.GetWindowText(hwnd)
            return title and "抖音" in title
        except Exception:
            return False

    @staticmethod
    def focus_window(hwnd):
        try:
            win32api.keybd_event(VK_MENU, 0, 0, 0)
            time.sleep(0.02)
            win32api.keybd_event(VK_MENU, 0, win32con.KEYEVENTF_KEYUP, 0)
            win32gui.ShowWindow(hwnd, win32con.SW_RESTORE)
            win32gui.SetForegroundWindow(hwnd)
            return True
        except Exception:
            return False

    @staticmethod
    def send_space():
        try:
            win32api.keybd_event(VK_SPACE, 0, 0, 0)
            time.sleep(0.05)
            win32api.keybd_event(VK_SPACE, 0, win32con.KEYEVENTF_KEYUP, 0)
            logging.info("space key sent")
            return True
        except Exception:
            return False

    @staticmethod
    def find_codex_window():
        result = []

        def callback(hwnd, _lparam):
            if not win32gui.IsWindowVisible(hwnd):
                return True
            title = win32gui.GetWindowText(hwnd)
            if title and "Codex" in title:
                result.append(hwnd)
                return False
            return True

        win32gui.EnumWindows(callback, None)
        return result[0] if result else None

    @staticmethod
    def get_foreground_title():
        try:
            return win32gui.GetWindowText(win32gui.GetForegroundWindow())
        except Exception:
            return ""


# ============================================================
# Tray icon helpers
# ============================================================

def _make_icon(symbol):
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    if symbol == "play":
        draw.polygon([(22, 16), (22, 48), (46, 32)], fill="black")
    else:
        draw.rectangle([20, 16, 28, 48], fill="black")
        draw.rectangle([36, 16, 44, 48], fill="black")
    return img


# ============================================================
# TrayApp — state machine + pystray tray icon
# ============================================================

class TrayApp:
    def __init__(self):
        self.enabled = True
        self.has_phase = False
        self.phase = None
        self.generation = 0
        self.managed_session_active = False
        self.paused_by_helper = False
        self._lock = threading.Lock()

        self.wm = WindowManager()

        self.icon = pystray.Icon(
            "douyin_helper",
            _make_icon("pause"),
            "Codex 抖音助手",
        )
        self._rebuild_menu()

    def _rebuild_menu(self):
        if not self.has_phase:
            status_text = "正在监听 Codex..."
        elif self.phase == "working":
            status_text = "Codex 工作中" if self.enabled else "Codex 工作中 · 自动刷已关闭"
        else:
            status_text = "Codex 等你反馈"

        self.icon.menu = pystray.Menu(
            pystray.MenuItem(status_text, None, enabled=False),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem(
                "✓ 启用自动刷" if self.enabled else "启用自动刷",
                self._toggle_automation,
                checked=lambda item: self.enabled,
            ),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("打开抖音", self._open_douyin),
            pystray.MenuItem("暂停并回到 Codex", self._return_to_codex),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("退出", self._quit),
        )

    def _toggle_automation(self, icon, item):
        with self._lock:
            self.enabled = not self.enabled
            self.generation += 1
            gen = self.generation
            should_start = self.enabled and self.has_phase and self.phase == "working"
        self._update_icon()
        self._rebuild_menu()
        logging.info("automation %s", "enabled" if self.enabled else "disabled")
        if should_start:
            threading.Thread(target=self._begin_douyin_session, args=(gen,), daemon=True).start()

    def _open_douyin(self, icon, item):
        self.wm.launch_douyin()

    def _return_to_codex(self, icon, item):
        with self._lock:
            self.generation += 1
            gen = self.generation
        threading.Thread(target=self._recall_to_codex, args=(gen,), daemon=True).start()

    def _quit(self, icon, item):
        icon.stop()

    def handle_event(self, event, initial_state=False):
        with self._lock:
            self.has_phase = True
            self.phase = event.phase
            self.generation += 1
            gen = self.generation

        logging.info("event phase=%s initial=%s", event.phase, initial_state)

        if event.phase == "working":
            self._update_icon("play")
            if initial_state:
                age = (datetime.now(timezone.utc) - event.timestamp).total_seconds()
                if age > BOOTSTRAP_STALE_SEC:
                    return
            if self.enabled:
                threading.Thread(target=self._begin_douyin_session, args=(gen,), daemon=True).start()
        else:
            self._update_icon("pause")
            if initial_state:
                return
            threading.Thread(target=self._recall_to_codex, args=(gen,), daemon=True).start()

        self._rebuild_menu()

    def _update_icon(self, symbol):
        try:
            self.icon.icon = _make_icon(symbol)
        except Exception:
            pass

    def _ensure_douyin_window(self):
        hwnd = self.wm.find_douyin_window()
        if hwnd and self.wm.is_window_valid(hwnd):
            return hwnd

        self.wm.launch_douyin()
        logging.info("waiting for douyin window to appear")

        for _ in range(20):
            time.sleep(0.5)
            hwnd = self.wm.find_douyin_window()
            if hwnd:
                logging.info("douyin window found hwnd=%s", hwnd)
                return hwnd

        logging.warning("douyin window not found after launch")
        return None

    def _begin_douyin_session(self, generation):
        with self._lock:
            if not self.enabled:
                logging.info("working ignored automation=disabled")
                return

        logging.info("working activate douyin generation=%d", generation)

        with self._lock:
            self.managed_session_active = True

        hwnd = self._ensure_douyin_window()
        if not hwnd:
            return

        with self._lock:
            if (generation != self.generation or not self.has_phase
                    or self.phase != "working" or not self.enabled):
                return

        self.wm.focus_window(hwnd)
        time.sleep(FOCUS_DELAY_SEC)

        with self._lock:
            if (generation != self.generation or not self.has_phase
                    or self.phase != "working" or not self.enabled):
                return
            paused = self.paused_by_helper

        if paused:
            self.wm.send_space()
            logging.info("resume douyin with space")
            with self._lock:
                self.paused_by_helper = False

        self._update_icon("play")

    def _recall_to_codex(self, generation):
        with self._lock:
            managed = self.managed_session_active

        if managed:
            hwnd = self.wm.find_douyin_window()
            if hwnd and self.wm.is_window_valid(hwnd):
                self.wm.send_space()
                logging.info("pause douyin with space")
                with self._lock:
                    self.paused_by_helper = True

        with self._lock:
            self.managed_session_active = False

        logging.info("attention recall generation=%d", generation)

        time.sleep(RECALL_DELAY_SEC)

        with self._lock:
            if generation != self.generation:
                return

        codex_hwnd = self.wm.find_codex_window()
        if codex_hwnd:
            self.wm.focus_window(codex_hwnd)

        self._update_icon("pause")

    def run(self):
        monitor = SessionMonitor(self.handle_event)
        monitor.start()
        logging.info("running")
        try:
            self.icon.run()
        finally:
            monitor.stop()
            logging.info("terminate")


# ============================================================
# Self-tests
# ============================================================

def self_test():
    print("=== Running self-tests ===")

    monitor = SessionMonitor(lambda e, initial: None)

    meta = monitor._decode_metadata(json.dumps({
        "type": "session_meta",
        "payload": {"id": "thread-1", "thread_source": "user", "source": "vscode"},
    }).encode())
    assert meta and meta["user_thread"] and meta["thread_id"] == "thread-1", \
        "user session decoding failed"

    meta = monitor._decode_metadata(json.dumps({
        "type": "session_meta",
        "payload": {
            "id": "thread-2",
            "thread_source": "subagent",
            "parent_thread_id": "thread-1",
            "source": {"subagent": {"other": "guardian"}},
        },
    }).encode())
    assert meta and not meta["user_thread"], "subagent filtering failed"

    meta = monitor._decode_metadata(json.dumps({
        "type": "session_meta",
        "payload": {"id": "thread-3", "thread_source": "user", "source": {"subagent": True}},
    }).encode())
    assert meta and not meta["user_thread"], "source subagent filtering failed"

    user_meta = {"thread_id": "t1", "user_thread": True}

    e = monitor._decode_state_event(json.dumps({
        "timestamp": "2026-07-13T07:52:25.288Z",
        "type": "event_msg",
        "payload": {"type": "task_started"},
    }).encode(), user_meta)
    assert e and e.phase == "working", "task_started mapping failed"

    e = monitor._decode_state_event(json.dumps({
        "timestamp": "2026-07-13T07:52:25.288Z",
        "type": "event_msg",
        "payload": {"type": "task_complete"},
    }).encode(), user_meta)
    assert e and e.phase == "attention", "task_complete mapping failed"

    e = monitor._decode_state_event(json.dumps({
        "timestamp": "2026-07-13T07:52:25.288Z",
        "type": "event_msg",
        "payload": {"type": "turn_aborted"},
    }).encode(), user_meta)
    assert e and e.phase == "attention", "turn_aborted mapping failed"

    e = monitor._decode_state_event(json.dumps({
        "timestamp": "2026-07-13T07:52:25.288Z",
        "type": "response_item",
        "payload": {"type": "function_call", "name": "request_user_input"},
    }).encode(), user_meta)
    assert e and e.phase == "attention", "request_user_input mapping failed"

    e = monitor._decode_state_event(json.dumps({
        "timestamp": "2026-07-13T07:52:25.288Z",
        "type": "response_item",
        "payload": {"type": "custom_tool_call", "name": "codex_app.request_user_input"},
    }).encode(), user_meta)
    assert e and e.phase == "attention", "custom_tool_call mapping failed"

    assert monitor._decode_metadata(b"nope") is None, "malformed input handling failed"

    print("Self-tests passed.")
    return 0


def diagnose():
    wm = WindowManager()
    douyin = wm.find_douyin_window()
    codex = wm.find_codex_window()
    fg = wm.get_foreground_title()

    print(f"douyin_window={'found' if douyin else 'none'}")
    print(f"codex_window={'found' if codex else 'none'}")
    print(f"foreground={fg}")
    print(f"sessions_dir_exists={os.path.isdir(SESSIONS_DIR)}")
    print(f"log={os.path.join(LOG_DIR, 'helper.log')}")

    if os.path.isdir(SESSIONS_DIR):
        recent = 0
        cutoff = datetime.now() - timedelta(hours=48)
        for root, _dirs, files in os.walk(SESSIONS_DIR):
            for f in files:
                if f.endswith(".jsonl"):
                    try:
                        mtime = datetime.fromtimestamp(os.path.getmtime(os.path.join(root, f)))
                        if mtime >= cutoff:
                            recent += 1
                    except OSError:
                        pass
        print(f"recent_session_files={recent}")

    return 0


def main():
    if len(sys.argv) > 1:
        if sys.argv[1] == "--self-test":
            return self_test()
        if sys.argv[1] == "--diagnose":
            return diagnose()

    app = TrayApp()
    app.run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
