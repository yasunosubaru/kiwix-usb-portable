# 更新日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.0.0] — 2026-09-25

首个公开版本。

### 新增

- **GUI 启动器**（`src/KiwixUSB.py`）
  - 跨平台：Windows 单文件 `.exe` 与 Linux 单文件 ELF
  - 启动前五项预检：CPU 架构 / 运行程序 / 离线内容库 / 端口 / 可用内存
  - 一键启停，显示局域网地址、PID、运行时长、每 3 秒本机健康检查
  - **书目自检**：启动后向服务查询实际加载的书目数，与磁盘上的 ZIM 数量比对
  - 显式触发的 SHA256 完整性校验（启动时不做全盘扫描）
  - 「打开书架（全部）」自动追加 `#lang=`，规避 kiwix 首页按浏览器语言自动过滤导致只显示一本的问题
  - 可复制 NAS 一键安装命令
- **免安装整合包**（`launcher/`）
  - `start.sh` / `stop.sh`：Linux 命令行启停，自动识别 CPU 架构、自动避让占用端口
  - `start-gui.cmd`：Windows 双击入口
  - `install-fnos.sh`：NAS 一键常驻部署（复制资料库 → 导入离线镜像 → 启动容器并设置开机自启）
- **构建脚本**（`scripts/`）
  - `fetch-kiwix-binaries.sh` / `.ps1`：从官方源获取 kiwix-tools 3.8.2 静态二进制
  - `build-linux-docker.sh`：在 `debian:12` 容器内构建 Linux GUI，锁定 glibc 2.36 基线
  - `make-docker-legacy-tar.*`：将 OCI 格式镜像转为传统 docker-archive
- **CI/CD**（`.github/workflows/release.yml`）
  - 标签推送时构建 Windows / Linux 两个平台并发布 Release 资产
- **文档**：`README.md`、`docs/使用说明.md`、`docs/NAS-部署方案.md`、`zim/README.md`

### 安全

- 不静默提权、不创建系统自启项、不修改防火墙
- 不联网、无遥测、无自动更新
- ZIM 始终以只读方式访问，不修改 / 移动 / 重写
- 日志不记录搜索词与浏览记录
- 端口被占用时不会结束对方进程

### 已知限制

- Linux GUI 需要图形显示（X11/Wayland），无桌面的 NAS 需使用脚本或 Docker 方式
- kiwix-serve 无内建认证，不得直接暴露到公网
- 部分系统会以 `noexec` 挂载 U 盘，此时无法从 U 盘直接启动 GUI

[1.0.0]: https://github.com/yasunosubaru/kiwix-usb-portable/releases/tag/v1.0.0
