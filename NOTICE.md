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
| 本项目使用版本 | 3.8.2（`kiwix-serve` / `kiwix-manage` / `kiwix-search`）|

包含：
- `kiwix-tools_linux-x86_64-3.8.2.tar.gz` → 静态 ELF，部署于 `app/linux-x86_64/`
- Windows 构建 → `app/windows-x86_64/`
- `kiwix-serve` Docker 镜像（`ghcr.io/kiwix/kiwix-serve`）→ `app/docker/kiwix-serve-legacy.tar`

kiwix-tools 是 GPL 软件，因此**包含其二进制文件的分发包整体以 GPL-3.0 发布**。
官方 Docker 镜像内置于 Alpine 用户空间，提供 Tcl/Tk 与 X11 运行库。

---

## 2. Python / PyInstaller / Tkinter

| 组件 | 许可 |
|---|---|
| Python 3.11+ | PSF License |
| PyInstaller | GPL-2.0-or-later（附带 bootloader 例外条款）|
| Tcl / Tk | BSD-style（Tcl）/ BSD-style（Tk）|

GUI 启动器使用标准库 `tkinter` + `urllib`，**不引入额外第三方 Python 依赖**。

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
