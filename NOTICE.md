# NOTICE — 第三方组件与许可

本项目**只分发源码与构建脚本**。以下第三方二进制组件由 `scripts/fetch-*`
在构建时从官方源下载，或作为 GitHub Release 资产提供，**不存入 Git 历史**。

---

## 1. kiwix-tools

| | |
|---|---|
| 项目 | Kiwix / openZIM |
| 官网 | <https://www.kiwix.org/> |
| 源码 | <https://github.com/kiwix/kiwix-tools> |
| 下载 | <https://download.kiwix.org/release/kiwix-tools/> |
| 许可 | **GPL-3.0-or-later** |
| 本项目使用版本 | 见下表 |

包含 `kiwix-serve` / `kiwix-manage` / `kiwix-search`（Linux x86_64、Windows x86_64、macOS），
以及 `ghcr.io/kiwix/kiwix-serve` Docker 镜像（`app/docker/kiwix-serve-legacy.tar`）。

kiwix-tools 是 GPL 软件，因此**包含其二进制文件的分发包整体以 GPL-3.0 发布**。
官方 Docker 镜像内置于 Alpine 用户空间，提供 Tcl/Tk 与 X11 运行库。

### 平台版本不一致（上游现状）

kiwix-tools **不保证每个平台在同一版本同时发布**。`scripts/fetch-kiwix-binaries.*`
因此会读取官方索引，自动选用该平台**实际存在的最新版本**，并在回退时给出警告。

以 3.8.2 为例：

| 平台 | 采用版本 | 说明 |
|---|---|---|
| Linux x86_64 / aarch64 | 3.8.2 | 与源码包同版本 |
| macOS x86_64 / arm64 | 3.8.2 | 同上 |
| **Windows x86_64** | **3.8.1** | 上游 3.8.2 **未发布 Windows 构建**，回退到最新可用的 3.8.1 |

**Windows 构建不是单文件**：3.8.x 的 `win-x86_64` 归档中，`kiwix-*.exe` 需要同目录下的
配套 DLL 才能启动（3.7.0 及更早的版本是静态链接，单个 exe 约 10 MB；3.8.1 的 exe 仅约
3.7 MB）。因此 `app/windows-x86_64/` 是一个**必须保持完整的目录**，只取 exe 会得到一个
以 `STATUS_DLL_NOT_FOUND` 秒退的包。`scripts/fetch-kiwix-binaries.ps1` 会整目录拷贝，
并在 Windows 上执行 `kiwix-serve.exe --version` 验证；CI 的 `build-kiwix-windows` job
同样会执行一次，失败即阻断发布。

GUI 启动器与 kiwix-serve 之间没有版本耦合（它们只是通过命令行和 HTTP 交互），
因此这一补丁级差异不影响功能；`SHA256SUMS.txt` 记录了实际分发文件的哈希。

---

## 2. GUI 运行时

仓库内提供**两套** Windows 图形启动器，功能与安全边界完全一致，可任选其一：

| 启动器 | 技术栈 | 产物 | 许可 |
|---|---|---|---|
| `app/gui/KiwixWinUI/` | **WinUI 3**（Windows App SDK 1.7）| 自包含目录，约 166 MB | WinUI 3 **MIT**；Windows App SDK **MIT**；.NET Runtime **MIT** |
| `app/gui/KiwixUSB.exe` | Python 3 + tkinter | 单文件，约 11 MB | Python **PSF**；PyInstaller **GPL-2.0-or-later**（附 bootloader 例外条款）；Tcl/Tk **BSD-style** |
| `app/gui/KiwixUSB-linux-x86_64` | Python 3 + tkinter | 单文件，约 12 MB | 同上 |

整包根目录的 `Kiwix.exe` 是入口：一个约 130 KB 的原生 Win32 程序（`src/launcher/winlauncher.c`），
只依赖 `user32.dll` / `kernel32.dll`，不引入任何第三方组件。优先启动 WinUI 3 版；
该目录不存在时自动回退到 tkinter 单文件版。`launcher/` 下的 `.cmd` 是等价的脚本入口。

**为什么体积差这么多**

- WinUI 3 走的是**自包含（self-contained）**发布。为了做到「U 盘插上即用、不需要另外安装
  Windows App Runtime」，.NET 运行时与 Windows App Runtime 全部打进产物（约 480 个文件，166 MB）。
- tkinter 版用 PyInstaller `--onefile` 打包，Python 解释器一并压进单个 exe，因此只有 11 MB。

两者都**不需要安装、不需要预装 Python 或 .NET 运行时**。

---

## 3. ZIM 内容

**ZIM 离线资料库不随本项目分发**（体积巨大，且遵循各自独立许可）。

- 来源：<https://download.kiwix.org/zim/>
- 维基百科条目通常为 **CC BY-SA 3.0**（具体以各 ZIM 内嵌元数据为准）

使用者需自行遵守对应内容条目的许可条款。

---

## 4. 本项目自身的许可

源码与脚本：**GPL-3.0-or-later**，见 [LICENSE](LICENSE)。

若你在分发包中保留了 kiwix-tools 的二进制文件，则该分发包整体需符合
GPL-3.0 的再分发要求（提供源码、保留版权声明、标注修改）。
