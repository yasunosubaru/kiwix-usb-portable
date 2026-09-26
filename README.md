# Kiwix USB Portable · Kiwix 离线维基 U 盘便携版

> A self-contained, zero-install offline Wikipedia (Kiwix) server that lives on a USB drive.
> Built for people who want 145 GB of offline encyclopedias without installing anything.

一个**免安装、离线、跨平台**的 Kiwix 离线维基服务器。把 U 盘插上，双击即用。

![platforms](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-lightgrey) ![license](https://img.shields.io/badge/license-GPL--3.0-blue) ![kiwix](https://img.shields.io/badge/kiwix--tools-3.8.2-green)

---

## 这是什么

| | |
|---|---|
| **一个 U 盘就是一套服务器** | `zim/` 放资料库，`app/` 放程序，插上就能跑 |
| **不安装** | Windows 单文件 `.exe`（约 11 MB）；Linux 单文件 ELF（约 12 MB） |
| **不联网** | 无遥测、无自动更新、无出站请求 |
| **不依赖 Docker** | 直接跑 kiwix 官方静态二进制 |
| **也可装 Docker** | 附离线镜像 tar，飞牛/群晖上一键常驻 |
| **GUI + 命令行** | 图形启动器带预检与自检；无桌面环境用 shell 脚本 |
| **两套 Windows GUI** | **WinUI 3**（现代界面，默认）与 Python/tkinter 单文件版（11 MB 轻量备用）|

已内置 kiwix-tools **3.8.2** 官方静态二进制（Linux x86_64 / Windows x86_64）。

---

## 快速开始

### Windows
**双击 `Kiwix.exe`** → 点「▶ 启动服务」→ 自动打开浏览器。

`Kiwix.exe` 是整合包根目录的入口：一个约 130 KB 的原生 Win32 程序，
不依赖 .NET、Python 或 Windows App Runtime。它只做一件事——
找到整合包、启动图形界面、然后自己退出。

- 优先启动 **WinUI 3** 版（`app/gui/KiwixWinUI/`）
- 该目录不存在时（例如精简整包），自动回退到 tkinter 单文件版（`app/gui/KiwixUSB.exe`）
- 两个都不存在时弹出对话框说明缺了什么，而不是静默失败

图形界面有两版，功能与安全边界完全一致。`launcher/` 下另有
`start-gui.cmd` / `启动Kiwix便携版.cmd` 两个脚本入口，作用相同，
只是需要在有脚本宿主的环境里用。

### Linux 桌面
```bash
chmod +x bundle/app/gui/KiwixUSB-linux-x86_64
./bundle/app/gui/KiwixUSB-linux-x86_64
```

### Linux / NAS（无桌面）
```bash
cd bundle
chmod +x start.sh stop.sh
./start.sh
```

### 飞牛 fnOS / 群晖：让服务常驻
```bash
sudo sh install-fnos.sh      # 脚本名随发行版而定，见 docs/
```

---

## 目录结构

```
Kiwix-USB/
├── Kiwix.exe                  Windows 双击入口（原生 Win32，约 130 KB）
├── start-gui.cmd              同上，脚本版备用
├── start.sh / stop.sh         Linux 命令行启停
├── install-fnos.sh            NAS 一键常驻部署（Docker）
├── library.xml
├── zim/                       ← ZIM 资料库（不进版本库，见 zim/README.md）
│   └── wikipedia_*.zim
└── app/
    ├── gui/                   GUI 启动器
    │   ├── KiwixWinUI/               Windows WinUI 3（自包含，约 166 MB）
    │   ├── KiwixUSB.exe              Windows tkinter 单文件（约 11 MB）
    │   ├── KiwixUSB-linux-x86_64     Linux tkinter 单文件（glibc 2.36 基线）
    │   └── KiwixUSB.py               tkinter 版源码
    ├── linux-x86_64/           kiwix-serve / manage / search（官方静态）
    ├── windows-x86_64/         同上（.exe）
    └── docker/                 Docker 离线镜像 tar
```

---

## 从源码构建

```bash
git clone <this-repo>
cd <this-repo>

# 1) Windows 入口（原生 Win32，双击用的那个）
powershell -File scripts\build-launcher.ps1

# 2) Windows WinUI 3 启动器
powershell -File scripts\build-winui.ps1

# 3) Windows / Linux tkinter 单文件启动器

# 4) 下载 kiwix 官方静态二进制（不入库）
bash scripts/fetch-kiwix-binaries.sh          # Linux
powershell -File scripts/fetch-kiwix-binaries.ps1   # Windows

# 5) 打包 tkinter GUI
pip install -r requirements-build.txt
python -m PyInstaller --onefile --windowed --name KiwixUSB src/KiwixUSB.py

# Linux 产物必须在 Debian 12 基线上构建，否则 glibc 过高无法在 NAS 上运行
bash scripts/build-linux-docker.sh
```

### 启动器自检

两套启动器都能在无界面下自证可用：

```powershell
# WinUI 3：定位资料库 → 启动 kiwix-serve → 核对服务器实际加载的书目数 → 干净停止
app\gui\KiwixWinUI\KiwixWinUI.exe --selftest

# tkinter 版：同一套断言
app\gui\KiwixUSB.exe --selftest
```

`scripts\build-winui.ps1` 在构建后会自动做一次启动检查；GitHub Actions 的
`build-winui` job 同样会启动一次并确认进程存活。

GitHub Actions 会在 `v*` 标签推送时自动构建三个平台（WinUI 3 / tkinter / Linux）并发布 Release 资产。

---

## 常见问题

**Q：打开网页只有一本维基，另一本不见了？**
A：不是文件丢了。kiwix 首页脚本在 **URL 没有任何 `#` 参数**时，会按
**浏览器界面语言**自动加语言过滤（中文浏览器 → 只显示 `zho`），并把过滤
状态写进 cookie 保留一天。本项目的「打开书架（全部）」按钮会自动追加
`#lang=` 跳过该逻辑。手动访问也可用 `http://IP:端口/#lang=`。
GUI 启动后还会自动比对「服务器实际加载的书目数」与磁盘上的 ZIM 数量，
在状态栏显示 `已加载 2/2 本`。

**Q：容器/进程显示 `Exited (0)`？**
A：kiwix 官方镜像的 `start.sh` 末行是 `if [ $? -ne 0 ]; then find /data -type f; fi`，
子进程的失败退出码会被 `find` 的 0 覆盖，**不能用退出码判断成败**。
最常见真因是 `zim/` 目录为空导致 `*.zim` 匹配不到。看日志即可确认。

**Q：U 盘提示 `Permission denied` / 界面打不开？**
A：系统把 U 盘以 `noexec` 方式挂载了。此时连 GUI 都无法从 U 盘启动，
正确做法是用 `install-fnos.sh` 把资料库复制到 NAS 硬盘，再用 Docker 运行。

**Q：全文搜索很慢？**
A：118 GB 的英文维基检索确实吃 CPU、内存和磁盘带宽。资料库放在机械盘或
通过网络盘时会更慢。GUI 会在启动前提示可用内存。

---

## 安全边界（有意不做的事）

- ❌ 不静默提权、**不需要管理员权限**
- ❌ 不创建系统自启项、不写注册表、不修改防火墙
- ❌ 不联网、不自动更新、不上报任何数据
- ❌ 不修改 / 移动 / 重写 ZIM（**始终只读**）
- ❌ 启动时不做全盘哈希扫描（145 GB 扫描会拖慢启动；校验需用户显式触发）
- ❌ 日志不含搜索词与浏览记录（未开启 `--verbose`）
- ❌ 端口被占用时不会结束对方进程，只改用下一个端口并记录说明
- ✅ 关闭 GUI 会停止它启动的服务，不留孤儿进程

> ⚠️ **kiwix-serve 本身没有登录功能。** 直接暴露到公网等于「拿到网址的人都能看」。
> 外网访问请走 Tailscale / 带认证的反向代理 / Cloudflare Access，详见 `docs/`。

---

## 授权

- 本项目源码与打包脚本：**GPL-3.0-or-later**（见 [LICENSE](LICENSE)）
- kiwix-tools：**GPL-3.0**，由脚本从 <https://download.kiwix.org/release/kiwix-tools/> 官方下载
- Docker 基础镜像 `ghcr.io/kiwix/kiwix-serve`：上游 openZIM 项目
- ZIM 内容遵循各自原始许可（维基百科为 CC BY-SA 等），**不随本仓库分发**

详见 [NOTICE.md](NOTICE.md)。

## 致谢

- [Kiwix](https://www.kiwix.org/) 与 [openZIM](https://openzim.org/)：离线阅读器的发明者
- 感谢社区对「便携式离线维基」各种形态的探索

## 相关文档

- [`docs/使用说明.md`](docs/使用说明.md) — 便携版完整使用手册
- [`docs/NAS-部署方案.md`](docs/NAS-部署方案.md) — 飞牛 fnOS / 群晖部署与外网访问
- [`zim/README.md`](zim/README.md) — 资料库获取与格式要求
