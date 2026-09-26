#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Kiwix 离线维基 · 便携版 GUI
==================================================
U 盘免安装应用。插上即用，不需要安装、不需要 Docker、不需要联网。

设计约束（刻意不做的事）：
  * 不静默提权、不创建系统自启项、不修改防火墙
  * 不联网、不自动更新、不上报任何数据
  * 不修改、不移动、不重写 ZIM 文件（只读访问）
  * 启动时不做全盘哈希扫描（校验由用户显式触发）
  * 日志不含搜索词与浏览记录（未开启 --verbose）
"""

import os
import re
import sys
import ctypes
import queue
import socket
import hashlib
import time
import threading
import subprocess
import webbrowser
import urllib.request
import urllib.error

import tkinter as tk
from tkinter import ttk, messagebox
from tkinter.scrolledtext import ScrolledText

APP_NAME = "Kiwix 离线维基"
APP_SUB = "便携版"
APP_VER = "1.2.1"   # keep in step with <Version> in KiwixWinUI.csproj
KIWIX_VER = "3.8.2"
DEFAULT_PORT = 8092
BASELINE_NAME = "SHA256_原机基线.txt"

BG, CARD, CARD2 = "#12151c", "#1b1f2a", "#232838"
FG, DIM = "#e6e9f0", "#8b93a7"
OK, WARN, ERR, ACC = "#3ddc84", "#f5a623", "#ff5c5c", "#4c8dff"

IS_WIN = os.name == "nt"
if IS_WIN:
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass

SUPPORTED_PLATFORMS = ("windows-x86_64", "linux-x86_64", "linux-aarch64",
                     "linux-armv8", "linux-i586")


# ============================================================
#  纯函数工具
# ============================================================
def bundle_root():
    if getattr(sys, "frozen", False):
        base = os.path.dirname(os.path.abspath(sys.executable))
    else:
        base = os.path.dirname(os.path.abspath(__file__))
    d = base
    for _ in range(6):
        if os.path.isdir(os.path.join(d, "zim")) and os.path.isdir(os.path.join(d, "app")):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    return base


def detect_platform_key():
    if sys.platform.startswith("win"):
        return "windows-x86_64"
    machine = os.uname().machine if hasattr(os, "uname") else ""
    return {
        "x86_64": "linux-x86_64", "amd64": "linux-x86_64",
        "aarch64": "linux-aarch64", "arm64": "linux-aarch64",
        "armv7l": "linux-armv8", "armv6l": "linux-armv8",
        "i386": "linux-i586", "i486": "linux-i586", "i586": "linux-i586",
    }.get(machine.lower(), "linux-" + (machine.lower() or "unknown"))


def binary_path_for(app_dir, plat):
    return os.path.join(app_dir, plat, "kiwix-serve.exe" if IS_WIN else "kiwix-serve")


def human_size(n):
    for u in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024 or u == "TB":
            return "%d B" % n if u == "B" else "%.1f %s" % (n, u)
        n /= 1024.0


def local_ip():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(("10.255.255.255", 1))
        return s.getsockname()[0]
    except Exception:
        return "127.0.0.1"
    finally:
        s.close()


def port_busy(port):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        s.bind(("127.0.0.1", port))
        return False
    except OSError:
        return True
    finally:
        s.close()


def free_port(preferred, limit=50):
    """First free port at or after `preferred`.

    Never terminates whatever owns a busy port; the caller is responsible for
    telling the user that the port moved.
    """
    p = preferred
    for _ in range(limit):
        if not port_busy(p):
            return p
        p += 1
    return p


def http_alive(port, timeout=2.0):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(timeout)
    try:
        s.connect(("127.0.0.1", port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def available_mem_gb():
    try:
        import ctypes
        class M(ctypes.Structure):
            _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                        ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                        ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                        ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                        ("ullAvailExtendedVirtual", ctypes.c_ulonglong)]
        m = M(); m.dwLength = ctypes.sizeof(M)
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m)):
            return m.ullAvailPhys / 1024 ** 3
    except Exception:
        pass
    try:
        return os.sysconf("SC_AVPHYS_PAGES") * os.sysconf("SC_PAGE_SIZE") / 1024 ** 3
    except Exception:
        return -1.0


# ============================================================
#  主窗口
# ============================================================
class KiwixApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("%s · %s  v%s" % (APP_NAME, APP_SUB, APP_VER))
        self.geometry("980x740")
        self.minsize(900, 660)
        self.configure(bg=BG)

        self.root_dir = bundle_root()
        self.zim_dir = os.path.join(self.root_dir, "zim")
        self.app_dir = os.path.join(self.root_dir, "app")
        self.plat = detect_platform_key()

        self.proc = None
        self.port = None
        self.q = queue.Queue()
        self.zim_rows = []
        self.verify_state = {}
        self.started_at = None
        self.health_ok = None
        self.chip_labels = {}

        self._init_style()
        self._build_ui()
        self._poll()
        self._health()
        self.refresh_zims()
        self._tick_clock()
        self.protocol("WM_DELETE_WINDOW", self.on_close)

    # ---------------- 样式 ----------------
    def _init_style(self):
        self.ff = self._pick_font(("Microsoft YaHei UI", "微软雅黑", "Noto Sans CJK SC",
                                   "WenQuanYi Micro Hei", "Segoe UI", "Arial"))
        self.fm = self._pick_font(("Cascadia Mono", "Consolas", "DejaVu Sans Mono", "Courier New"))
        self.font_ui = (self.ff, 10)
        self.font_mono = (self.fm, 9)

        st = ttk.Style(self)
        for name in ("clam", "vista"):
            if name in st.theme_names():
                st.theme_use(name)
                break
        st.configure("TFrame", background=BG)
        st.configure("Card.TFrame", background=CARD)
        st.configure("TLabel", background=BG, foreground=FG, font=self.font_ui)
        st.configure("Card.TLabel", background=CARD, foreground=FG, font=self.font_ui)
        st.configure("Dim.TLabel", background=CARD, foreground=DIM, font=self.font_ui)
        st.configure("Title.TLabel", background=BG, foreground=FG, font=(self.ff, 17, "bold"))
        st.configure("Big.TLabel", background=CARD, foreground=FG, font=(self.ff, 19, "bold"))
        st.configure("CardTitle.TLabel", background=CARD, foreground=DIM, font=(self.ff, 9, "bold"))
        st.configure("TButton", font=self.font_ui, padding=(10, 6))
        st.configure("Go.TButton", font=(self.ff, 10, "bold"), padding=(16, 8))
        st.configure("TEntry", fieldbackground=CARD2, foreground=FG,
                     insertbackground=FG, font=self.font_ui)
        st.configure("TProgressbar", background=ACC, troughcolor=CARD2)
        st.configure("Treeview", background=CARD2, fieldbackground=CARD2, foreground=FG,
                     rowheight=24, font=self.font_ui)
        st.configure("Treeview.Heading", background="#2b3145", foreground=FG, font=self.font_ui)

    @staticmethod
    def _pick_font(families):
        import tkinter.font as tkfont
        avail = set(tkfont.families())
        for f in families:
            if f in avail:
                return f
        return "TkDefaultFont"

    def _chip(self, parent, key, text):
        l = tk.Label(parent, text=text, bg=CARD2, fg=DIM, font=(self.ff, 9),
                     padx=8, pady=3)
        self.chip_labels[key] = l
        return l

    def _set_chip(self, key, ok, text):
        l = self.chip_labels.get(key)
        if l is None:
            return
        l.config(bg="#12351f" if ok is True else ("#3a1f1f" if ok is False else CARD2),
                 fg=OK if ok is True else (ERR if ok is False else DIM), text=text)

    # ---------------- 界面 ----------------
    def _build_ui(self):
        self.columnconfigure(0, weight=1)
        self.rowconfigure(3, weight=1)

        # 顶部
        head = ttk.Frame(self, padding=(18, 14, 18, 4))
        head.grid(row=0, column=0, sticky="ew")
        head.columnconfigure(1, weight=1)
        ttk.Label(head, text=APP_NAME, style="Title.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(head, text="%s · 免安装 · 离线可用" % APP_SUB, style="Dim.TLabel").grid(
            row=1, column=0, sticky="w")
        self.badge = tk.Label(head, text="  未运行  ", bg=CARD2, fg=DIM,
                              font=(self.ff, 10, "bold"), padx=12, pady=5)
        self.badge.grid(row=0, column=1, rowspan=2, sticky="e")
        ttk.Label(head, text="kiwix-tools %s" % KIWIX_VER, style="Dim.TLabel").grid(
            row=0, column=2, rowspan=2, sticky="e", padx=(10, 0))

        # 服务控制
        ctl = ttk.Frame(self, style="Card.TFrame", padding=(18, 12))
        ctl.grid(row=1, column=0, sticky="ew", padx=18, pady=(8, 6))
        ctl.columnconfigure(1, weight=1)
        ttk.Label(ctl, text="服务地址", style="CardTitle.TLabel").grid(row=0, column=0, sticky="w")
        self.url_var = tk.StringVar(value="—")
        ttk.Label(ctl, textvariable=self.url_var, style="Big.TLabel").grid(
            row=1, column=0, columnspan=6, sticky="w", pady=(2, 6))

        btns = ttk.Frame(ctl, style="Card.TFrame")
        btns.grid(row=2, column=0, columnspan=6, sticky="ew")
        ttk.Label(btns, text="端口", style="Dim.TLabel").grid(row=0, column=0, sticky="w")
        self.port_var = tk.StringVar(value=str(DEFAULT_PORT))
        self.port_entry = ttk.Entry(btns, textvariable=self.port_var, width=7)
        self.port_entry.grid(row=0, column=1, sticky="w", padx=(8, 16))
        self.btn_start = ttk.Button(btns, text="▶  启动服务", style="Go.TButton", command=self.start_service)
        self.btn_start.grid(row=0, column=2, sticky="w", padx=(0, 8))
        self.btn_stop = ttk.Button(btns, text="■  停止服务", command=self.stop_service, state="disabled")
        self.btn_stop.grid(row=0, column=3, sticky="w", padx=(0, 8))
        self.btn_open = ttk.Button(btns, text="打开书架（全部）", command=self.open_browser, state="disabled")
        self.btn_open.grid(row=0, column=4, sticky="w")
        ttk.Button(btns, text="复制地址", command=self.copy_url).grid(row=0, column=5, sticky="w", padx=(8, 0))

        # 预检状态条
        chips = ttk.Frame(ctl, style="Card.TFrame")
        chips.grid(row=3, column=0, columnspan=6, sticky="w", pady=(10, 0))
        for i, (key, label) in enumerate([("arch", "架构"), ("bin", "程序"), ("zim", "内容库"),
                                          ("port", "端口"), ("mem", "内存")]):
            self._chip(chips, key, label)
            self.chip_labels[key].grid(row=0, column=i * 2, sticky="w", padx=(0 if i == 0 else 14, 0))
            tk.Label(chips, text="·", bg=CARD, fg="#3a4157", font=(self.ff, 9)).grid(
                row=0, column=i * 2 + 1, sticky="w", padx=(3, 0))

        # 内容库 + 日志
        mid = ttk.Frame(self)
        mid.grid(row=3, column=0, sticky="nsew", padx=18, pady=(4, 6))
        mid.columnconfigure(0, weight=3, uniform="mid")
        mid.columnconfigure(1, weight=4, uniform="mid")
        mid.rowconfigure(0, weight=1)

        left = ttk.Frame(mid, style="Card.TFrame", padding=(16, 12))
        left.grid(row=0, column=0, sticky="nsew", padx=(0, 6))
        left.columnconfigure(0, weight=1)
        left.rowconfigure(1, weight=1)
        ttk.Label(left, text="离线内容库（只读）", style="CardTitle.TLabel").grid(row=0, column=0, sticky="w")
        self.tree = ttk.Treeview(left, columns=("size", "state"), show="tree headings", height=7)
        self.tree.heading("#0", text="文件")
        self.tree.heading("size", text="大小")
        self.tree.heading("state", text="完整性")
        self.tree.column("#0", width=240)
        self.tree.column("size", width=78, anchor="e")
        self.tree.column("state", width=96, anchor="center")
        self.tree.tag_configure("ok", foreground=OK)
        self.tree.tag_configure("warn", foreground=WARN)
        self.tree.tag_configure("err", foreground=ERR)
        self.tree.grid(row=1, column=0, sticky="nsew")
        sb = ttk.Scrollbar(left, orient="vertical", command=self.tree.yview)
        sb.grid(row=1, column=1, sticky="ns")
        self.tree.configure(yscrollcommand=sb.set)
        self.zim_sum = ttk.Label(left, text="", style="Dim.TLabel")
        self.zim_sum.grid(row=2, column=0, sticky="w", pady=(8, 0))
        self.btn_verify = ttk.Button(left, text="校验完整性", command=self.verify_zims)
        self.btn_verify.grid(row=2, column=1, sticky="e", pady=(8, 0))
        self.bar = ttk.Progressbar(left, mode="determinate", length=180)
        self.bar.grid(row=3, column=0, sticky="ew", pady=(8, 0))
        self.verify_note = ttk.Label(left, text="启动时不做哈希扫描；仅在你点击后校验。",
                                     style="Dim.TLabel", font=(self.ff, 8))
        self.verify_note.grid(row=4, column=0, columnspan=2, sticky="w", pady=(4, 0))

        right = ttk.Frame(mid, style="Card.TFrame", padding=(16, 12))
        right.grid(row=0, column=1, sticky="nsew", padx=(6, 0))
        right.columnconfigure(0, weight=1)
        right.rowconfigure(1, weight=1)
        ttk.Label(right, text="运行日志（不含搜索词与浏览记录）", style="CardTitle.TLabel").grid(
            row=0, column=0, sticky="w", pady=(0, 8))
        self.log = ScrolledText(right, wrap="word", height=12, state="disabled",
                                bg="#0e1117", fg="#c8d0e0", insertbackground=FG,
                                font=self.font_mono, relief="flat", bd=0, padx=10, pady=8)
        self.log.grid(row=1, column=0, sticky="nsew")

        # fnOS
        fn = ttk.Frame(self, style="Card.TFrame", padding=(18, 10))
        fn.grid(row=4, column=0, sticky="ew", padx=18, pady=(0, 6))
        fn.columnconfigure(1, weight=1)
        ttk.Label(fn, text="飞牛 fnOS 常驻部署", style="CardTitle.TLabel").grid(row=0, column=0, sticky="w")
        ttk.Label(fn, text="SSH 进 NAS 后执行：  sudo sh <U盘路径>/install-fnos.sh    "
                            "（把 ZIM 复制到硬盘 → 装 Docker 容器 → 开机自启）",
                  style="Dim.TLabel").grid(row=1, column=0, columnspan=3, sticky="w", pady=(3, 0))
        ttk.Button(fn, text="复制命令", command=self.copy_fnos_cmd).grid(row=0, column=2, sticky="e")

        # 状态栏
        bar = ttk.Frame(self, padding=(18, 0, 18, 8))
        bar.grid(row=5, column=0, sticky="ew")
        bar.columnconfigure(0, weight=1)
        self.status = ttk.Label(bar, text="就绪", style="Dim.TLabel")
        self.status.grid(row=0, column=0, sticky="w")
        self.clock = ttk.Label(bar, text="", style="Dim.TLabel")
        self.clock.grid(row=0, column=1, sticky="e")
        ttk.Label(bar, text="不静默提权 · 不建自启 · 不改防火墙 · 不联网 · ZIM 只读",
                  style="Dim.TLabel", font=(self.ff, 8)).grid(row=0, column=2, sticky="e", padx=(14, 0))

        self.write_log("整合包目录: %s" % self.root_dir)
        self.write_log("运行平台  : %s / %s" % (sys.platform, self.plat))
        self.write_log("提示      : 纯 U 盘模式需要保持 U 盘插着；若被系统以 noexec 挂载，")
        self.write_log("            请用「飞牛 fnOS 常驻部署」把 ZIM 复制到硬盘后用 Docker 运行。")

    # ---------------- 基础操作 ----------------
    def set_status(self, text, color=DIM):
        self.status.config(text=text, style="TLabel", foreground=color)

    def write_log(self, line):
        self.log.config(state="normal")
        self.log.insert("end", line + "\n")
        self.log.see("end")
        self.log.config(state="disabled")

    def binary_path(self):
        return binary_path_for(self.app_dir, self.plat)

    @staticmethod
    def child_env():
        """给子进程一份干净的环境。

        PyInstaller 会把自己的库目录注入 LD_LIBRARY_PATH，若原样传给外部的
        kiwix-serve，它可能误加载启动器内捆绑的不兼容图形库（Qt/Mesa 等）。
        """
        env = os.environ.copy()
        if not IS_WIN:
            if "LD_LIBRARY_PATH_ORIG" in env:
                env["LD_LIBRARY_PATH"] = env["LD_LIBRARY_PATH_ORIG"]
            else:
                env.pop("LD_LIBRARY_PATH", None)
        return env

    # ---------------- 预检 ----------------
    def preflight(self):
        checks = []
        arch_ok = self.plat in SUPPORTED_PLATFORMS
        checks.append(("arch", arch_ok, self.plat if arch_ok else "不支持的架构 " + self.plat))

        exe = self.binary_path()
        bin_ok = os.path.isfile(exe)
        if not bin_ok:
            checks.append(("bin", False, "缺少 kiwix-serve"))
        else:
            if not IS_WIN:
                try:
                    os.chmod(exe, 0o755)
                except OSError:
                    pass
            readable = os.access(exe, os.R_OK)
            checks.append(("bin", readable, "就绪" if readable else "不可读"))

        n = len(self.zim_rows)
        checks.append(("zim", n > 0, "%d 本" % n if n else "未放入 .zim"))

        try:
            p = int(self.port_var.get().strip())
        except ValueError:
            p = DEFAULT_PORT
        busy = port_busy(p)
        checks.append(("port", not busy, "占用" if busy else "%d 可用" % p))

        mem = available_mem_gb()
        mem_ok = mem < 0 or mem >= 1.5
        checks.append(("mem", mem_ok, "%.1f GB" % mem if mem >= 0 else "未知"))

        for key, ok, text in checks:
            self._set_chip(key, ok, text)
        return checks

    def refresh_zims(self):
        for iid in self.tree.get_children():
            self.tree.delete(iid)
        self.zim_rows = []
        try:
            names = sorted(f for f in os.listdir(self.zim_dir) if f.lower().endswith(".zim"))
        except OSError:
            names = []
        total = 0
        for n in names:
            p = os.path.join(self.zim_dir, n)
            try:
                size = os.path.getsize(p)
            except OSError:
                size = 0
            total += size
            tag, text = self.verify_state.get(n, ("", "未校验"))
            self.tree.insert("", "end", iid=n, text=n, values=(human_size(size), text),
                             tags=(tag,) if tag else ())
            self.zim_rows.append((n, size))
        if self.zim_rows:
            self.zim_sum.config(text="共 %d 本 · 合计 %s" % (len(self.zim_rows), human_size(total)))
        else:
            self.zim_sum.config(text="zim 目录为空")
            self.tree.insert("", "end", iid="__none__", text="（尚未放入 .zim 文件）",
                             values=("", "缺文件"), tags=("err",))
        self.preflight()
        return names

    # ---------------- 服务控制 ----------------
    def start_service(self):
        if self.proc and self.proc.poll() is None:
            self.set_status("服务已在运行", WARN)
            return

        checks = self.preflight()
        by_key = {k: (ok, t) for k, ok, t in checks}
        if not by_key["bin"][0]:
            messagebox.showerror("无法启动", "缺少 kiwix-serve 程序：\n%s\n\n"
                                           "请确认整合包 app 目录完整，或安全软件未将其隔离。" % self.binary_path())
            return
        if not by_key["arch"][0]:
            messagebox.showerror("无法启动", "此版本只包含 %s 的程序。\n\n当前平台：%s\n\n"
                                           "应用不会下载其他架构的版本。" %
                                  ("、".join(SUPPORTED), self.plat))
            return
        if not self.zim_rows:
            messagebox.showwarning("没有离线内容",
                                   "zim 目录里没有 .zim 文件，无法启动。\n\n"
                                   "请确认 U 盘已插入、资料库目录未被移动。\n%s" % self.zim_dir)
            return
        mem = available_mem_gb()
        if 0 < mem < 1.5:
            if not messagebox.askyesno("内存偏少",
                                       "可用内存仅 %.1f GB。\n\n"
                                       "118GB 的英文维基全文搜索较吃内存，可能启动失败或被系统回收。\n\n"
                                       "仍要继续吗？" % mem):
                return

        try:
            want = int(self.port_var.get().strip())
        except ValueError:
            want = DEFAULT_PORT
        port = want
        if port_busy(port):
            alt = free_port(port)
            self.write_log("! 端口 %d 已被其它程序占用。" % port)
            self.write_log("  本应用没有结束该程序，已自动改用端口 %d。" % alt)
            port = alt
            self.port_var.set(str(port))
            self.set_status("端口 %d 被占用，已改用 %d（未结束原程序）" % (want, port), WARN)
        self.port = port

        exe = self.binary_path()
        args = [exe, "--port=%d" % port] + [os.path.join(self.zim_dir, n) for n, _ in self.zim_rows]
        self.write_log("")
        self.write_log("$ " + " ".join('"%s"' % a for a in args))

        kw = {}
        if IS_WIN:
            kw["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        try:
            self.proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                         bufsize=1, universal_newlines=True,
                                         encoding="utf-8", errors="replace",
                                         env=self.child_env(), **kw)
        except OSError as e:
            self.proc = None
            self.write_log("启动失败: %s" % e)
            messagebox.showerror("启动失败",
                                 "%s\n\n应用没有修改任何文件或系统设置。" % e)
            return

        self.started_at = time.time()
        self.health_ok = None
        threading.Thread(target=self._reader, args=(self.proc,), daemon=True).start()
        self.q.put(("started", port))

    def _reader(self, proc):
        try:
            for line in proc.stdout:
                self.q.put(("line", line.rstrip()))
        except Exception:
            pass
        self.q.put(("exit", proc.returncode))

    def stop_service(self):
        if not self.proc:
            return
        self.write_log("正在停止服务 ...")
        try:
            self.proc.terminate()
            self.proc.wait(timeout=8)
        except Exception:
            try:
                self.proc.kill()
            except Exception:
                pass
        self.proc = None
        self.q.put(("stopped", None))

    def open_browser(self):
        if self.port and http_alive(self.port):
            # 用回环地址而不是界面上显示的局域网地址：浏览器就在本机，而
            # Windows 防火墙默认拦截入站连接，http://<局域网IP>:<端口>/ 可能
            # 连提供服务的这台机器自己都访问不了。回环地址永不被防火墙拦。
            #
            # kiwix 首页脚本在 URL 无 # 参数时，会按「浏览器界面语言」自动加语言过滤
            # （中文浏览器 -> 只显示 zho 那本），并把过滤状态写进 cookie 保留一天。
            # 带上 #lang= 即可跳过自动过滤，显示全部书目。
            webbrowser.open("http://127.0.0.1:%d#lang=" % self.port)
        else:
            self.set_status("服务未运行或无响应", WARN)

    def count_loaded_books(self, port):
        """向服务自检：实际加载了几本，而不是只看进程活没活。"""
        url = "http://127.0.0.1:%d/catalog/v2/entries" % port
        for _ in range(3):
            try:
                with urllib.request.urlopen(url, timeout=25) as r:
                    body = r.read().decode("utf-8", "replace")
                return len(re.findall(r"<entry>", body)), None
            except Exception as e:
                err = str(e)
                time.sleep(2)
        return -1, err

    def copy_url(self):
        u = self.url_var.get()
        self.clipboard_clear()
        self.clipboard_append(u)
        self.set_status("已复制: %s" % u, OK)

    def copy_fnos_cmd(self):
        self.clipboard_clear()
        self.clipboard_append('sudo sh "$(pwd)/install-fnos.sh"')
        self.set_status("已复制安装命令", OK)

    # ---------------- 完整性校验（显式触发）----------------
    def verify_zims(self):
        if not self.zim_rows:
            return
        base = {}
        bp = os.path.join(self.zim_dir, BASELINE_NAME)
        if os.path.isfile(bp):
            try:
                with open(bp, "r", encoding="utf-8") as f:
                    for line in f:
                        m = re.match(r"^([0-9A-Fa-f]{64})\s+\d+\s+.*?([^\/\\]+\.zim)\s*$", line.strip())
                        if m:
                            base[m.group(2)] = m.group(1).upper()
            except OSError:
                pass
        threading.Thread(target=self._verify_worker, args=(base,), daemon=True).start()

    def _verify_worker(self, base):
        self.q.put(("vstart", None))
        for name, size in self.zim_rows:
            path = os.path.join(self.zim_dir, name)
            h = hashlib.sha256()
            total = size or 1
            read = 0
            try:
                with open(path, "rb") as f:
                    while True:
                        c = f.read(8 * 1024 * 1024)
                        if not c:
                            break
                        h.update(c)
                        read += len(c)
                        self.q.put(("vprog", read / total))
            except OSError:
                self.q.put(("vstate", (name, "err", "读取失败")))
                continue
            exp = base.get(name)
            if exp is None:
                self.q.put(("vstate", (name, "warn", "无基线")))
            elif exp == h.hexdigest().upper():
                self.q.put(("vstate", (name, "ok", "校验通过")))
            else:
                self.q.put(("vstate", (name, "err", "不匹配")))
        self.q.put(("vdone", None))

    # ---------------- 消息泵 ----------------
    def _poll(self):
        try:
            while True:
                kind, val = self.q.get_nowait()
                if kind == "line":
                    self.write_log(val)
                elif kind == "started":
                    self._set_running(True, val)
                    threading.Thread(target=self._selfcheck, args=(val,), daemon=True).start()
                elif kind == "stopped":
                    self._set_running(False, None)
                elif kind == "exit":
                    self._on_exit(val)
                elif kind == "vstart":
                    self.bar["value"] = 0
                    self.btn_verify.config(state="disabled")
                    self.set_status("正在校验 SHA256，请勿拔出存储设备 ...", ACC)
                elif kind == "vprog":
                    self.bar["value"] = min(100, val * 100)
                elif kind == "vstate":
                    n, tag, text = val
                    self.verify_state[n] = (tag, text)
                    self.refresh_zims()
                elif kind == "vdone":
                    self.bar["value"] = 100
                    self.btn_verify.config(state="normal")
                    self.set_status("校验完成", OK)
                elif kind == "books":
                    n, err, expect = val
                    if n < 0:
                        self.write_log("! 自检失败，无法读取书目: %s" % err)
                    elif n == expect:
                        self.write_log("自检通过：服务器已加载 %d/%d 本资料库" % (n, expect))
                        self.set_status("已加载 %d/%d 本 · 本机已响应（外网/其他设备可达性取决于防火墙）" % (n, expect), OK)
                    else:
                        self.write_log("! 自检异常：磁盘上有 %d 本，服务器只加载了 %d 本" % (expect, n))
                        self.set_status("只加载了 %d/%d 本，请查看日志" % (n, expect), ERR)
        except queue.Empty:
            pass
        self.after(150, self._poll)

    def _selfcheck(self, port):
        n, err = self.count_loaded_books(port)
        self.q.put(("books", (n, err, len(self.zim_rows))))

    def _set_running(self, running, port):
        if running:
            self.port = port
            self.url_var.set("http://%s:%d" % (local_ip(), port))
            self.badge.config(text="  运行中  ", bg="#12351f", fg=OK)
            self.btn_start.config(state="disabled")
            self.btn_stop.config(state="normal")
            self.btn_open.config(state="normal")
            self.port_entry.config(state="disabled")
            self.set_status(
                "运行中 · 本机请用 http://127.0.0.1:%d 打开，其他设备用上方地址" % port, OK)
        else:
            self.url_var.set("—")
            self.port = None
            self.health_ok = None
            self.badge.config(text="  未运行  ", bg=CARD2, fg=DIM)
            self.btn_start.config(state="normal")
            self.btn_stop.config(state="disabled")
            self.btn_open.config(state="disabled")
            self.port_entry.config(state="normal")
            self.set_status("服务已停止")

    def _on_exit(self, code):
        was_running = self.proc is not None
        self.proc = None
        self._set_running(False, None)
        if code in (0, None, -15):
            self.write_log("进程已结束")
            self.set_status("服务已停止")
        else:
            self.write_log("!! 进程异常退出，返回码 %s" % code)
            self.set_status("服务未能持续运行，请查看日志", ERR)
            if was_running:
                messagebox.showwarning(
                    "服务已停止",
                    "kiwix-serve 未能持续运行（退出码 %s）。\n\n"
                    "常见原因：\n"
                    "  · 端口被占用（可在左侧改端口）\n"
                    "  · 资料库不完整或版本不兼容\n"
                    "  · 存储设备被系统以 noexec 挂载\n\n"
                    "应用不会自动重试，也不会修改你的文件。详情见日志。" % code)

    def _health(self):
        if self.proc and self.proc.poll() is None and self.port:
            ok = http_alive(self.port)
            if self.health_ok is not True and ok:
                self.write_log("本机健康检查通过：HTTP 服务已响应")
            self.health_ok = ok
        self.after(3000, self._health)

    def _tick_clock(self):
        if self.started_at and self.proc and self.proc.poll() is None:
            s = int(time.time() - self.started_at)
            self.clock.config(text="PID %d · 已运行 %02d:%02d:%02d%s" % (
                self.proc.pid, s // 3600, s // 60 % 60, s % 60,
                "" if self.health_ok else " · 无响应"))
        else:
            self.clock.config(text="")
        self.after(1000, self._tick_clock)

    def on_close(self):
        if self.proc and self.proc.poll() is None:
            if not messagebox.askyesno("退出", "服务还在运行。\n\n关闭本窗口会一并停止它，确定吗？"):
                return
            self.stop_service()
        self.destroy()


# ---------------------------------------------------------------- self test
def run_selftest():
    """Headless verification, so this launcher can be proven to work in CI
    instead of by eyeballing a window. Exit code 0 = pass."""
    import re
    import subprocess
    import time
    import urllib.request

    # When stdout is redirected to a file Python block-buffers, and a
    # force-kill would then swallow the tail of the report.
    try:
        sys.stdout.reconfigure(line_buffering=True)
    except Exception:
        pass

    failures = 0

    def check(ok, what):
        nonlocal failures
        print(("  [PASS] " if ok else "  [FAIL] ") + what)
        if not ok:
            failures += 1

    root = bundle_root()
    plat = detect_platform_key()
    print("=== KiwixUSB self-test ===")
    print("  bundle root : %s" % root)
    print("  platform    : %s (supported=%s)" % (plat, plat in SUPPORTED_PLATFORMS))
    print("  zim dir     : %s" % os.path.join(root, "zim"))
    print("  local ip    : %s" % local_ip())
    print()

    check(plat in SUPPORTED_PLATFORMS, "platform supported")

    exe = binary_path_for(os.path.join(root, "app"), plat)
    check(os.path.isfile(exe), "kiwix-serve binary present")

    try:
        zims = sorted(f for f in os.listdir(os.path.join(root, "zim"))
                      if f.lower().endswith(".zim"))
    except OSError:
        zims = []
    check(bool(zims), "offline library not empty (%d zim files)" % len(zims))
    for n in zims:
        print("          %s  %s" % (n, human_size(os.path.getsize(os.path.join(root, "zim", n)))))

    if not zims:
        print("\nRESULT: FAIL (no library, cannot exercise the service)")
        return 1

    if not IS_WIN:
        try:
            os.chmod(exe, 0o755)
        except OSError:
            pass

    port = free_port(18999)
    args = [exe, "--port=%d" % port] + [os.path.join(root, "zim", n) for n in zims]
    print("\n=== start service on port %d ===" % port)
    proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            universal_newlines=True, encoding="utf-8", errors="replace")
    check(proc.poll() is None, "service process started")

    loaded = -1
    for _ in range(30):
        time.sleep(1)
        if proc.poll() is not None:
            break
        try:
            with urllib.request.urlopen(
                    "http://127.0.0.1:%d/catalog/v2/entries" % port, timeout=8) as r:
                body = r.read().decode("utf-8", "replace")
            loaded = len(re.findall(r"<entry>", body))
            if loaded:
                break
        except Exception:
            pass
    check(loaded == len(zims), "server loaded %d/%d zim files" % (loaded, len(zims)))

    proc.terminate()
    try:
        proc.wait(timeout=10)
    except Exception:
        proc.kill()
    check(proc.poll() is not None, "service stopped cleanly")

    print()
    print("RESULT: PASS" if failures == 0 else "RESULT: FAIL (%d)" % failures)
    return 0 if failures == 0 else 1


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        sys.exit(run_selftest())
    KiwixApp().mainloop()
