# 更新日志

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [1.2.0] - 2026-09-27

Windows 入口从批处理脚本换成真正的原生 GUI 可执行文件。

### 新增

- **`Kiwix.exe`：整包根目录的原生 Win32 入口**（`src/launcher/winlauncher.c`）
  - 约 130 KB，静态链接 CRT，只依赖 `user32.dll` / `kernel32.dll`。
    U 盘场景要求「不装任何东西也能跑」，.NET / Python / Windows App Runtime
    三者都不能作为入口的前提
  - 定位整合包的规则与 `KiwixService.ResolveRoot` 一致：自自身目录向上找同时
    包含 `zim\` 与 `app\` 的目录，两者不会对「整包在哪」产生分歧
  - 优先启动 WinUI 3，缺失时回退 tkinter，两者都缺失时**弹窗说明缺了什么**，
    而不是静默失败
  - 启动后立即退出：GUI 是独立进程，关掉或杀掉这个壳不应连带停掉服务
  - 编译期用 `/W4 /WX`，并断言产物的 PE subsystem 为 2（GUI）——
    一个每次双击都闪一下黑框的入口，正是这个文件要消除的东西
  - `scripts/build-launcher.ps1` 负责构建、清理中间产物、校验体积与 subsystem

### 变更

- 文档与整包结构以 `Kiwix.exe` 为正式入口，`launcher/*.cmd` 降为脚本版备用
- CI `build-windows` job 增加：构建启动器 + 用「删掉 WinUI 目录的精简整包」
  验证它确实能回退到 tkinter；`Kiwix.exe` 进入整包根目录与缺件门禁

## [1.1.2] - 2026-09-26

界面显示的版本号此前是假的，一并修正。

### 修复

- **WinUI 3 头部硬写着 `kiwix-tools 3.8.2`，而 Windows 包实际是 3.8.1**
  - 上游 3.8.2 没有 Windows 构建（见 NOTICE），实际分发的是 3.8.1，界面却在宣称 3.8.2
  - 修复：改为启动时执行一次 `kiwix-serve --version` 取得真实版本，探测失败才显示「未知」
  - 标题栏同时显示启动器自身版本
- Tkinter 版标题栏的 `v1.0.0` 已同步
- GUI 端到端测试新增版本文本断言，防止探测静默失败后变空

## [1.1.1] — 2026-09-26

修复 1.1.0 里让 GUI「看起来坏了」的两个真实缺陷。1.1.0 的服务其实能起来，
但界面从不进入运行状态，「打开书架」永远点不动。

### 修复

- **WinUI 3：点「启动服务」后界面完全不更新**（严重）
  - `MainWindow.OnRunning()` 写了但**从未被调用**，是死代码。于是地址、状态徽标、
    按钮可用性、运行时长全部停在初始值，「打开书架」永远是 disabled
  - 这就是「启动服务后自己就结束了」的观感来源：服务在跑，但界面毫无反应，
    关窗时又把它一并停掉
  - 修复：`StartAsync` 成功后调用 `OnRunning()`；端口顺延时同步刷新端口框
- **两版 GUI 都用局域网 IP 打开浏览器**（严重）
  - 界面上显示的是局域网地址（给手机等其它设备用），但「打开书架」也用它
  - Windows 防火墙默认拦截入站连接，于是 `http://<局域网IP>:<端口>/` 连提供
    服务的这台机器自己都可能打不开
  - 修复：「打开书架」一律走 `http://127.0.0.1:<端口>/#lang=`（回环永不被拦），
    界面仍显示局域网地址供分享；状态栏明确区分「本机用回环 / 其它设备用上方地址」
- **不等服务真正就绪就报「启动成功」**
  - `Process.Start` 返回即视为成功。kiwix-serve 若两秒后退出，界面仍宣称一切正常
  - 修复：启动后轮询端口直到真正接受连接（上限 25 秒）；若进程提前退出，
    新增 `DiedDuringStartup`，界面直接显示退出码**和 kiwix-serve 的输出**
- **重复点击「启动服务」会起两个 kiwix-serve**
  - `IsRunning` 在 `StartAsync` 真正赋值前为 false，二次点击得以通过
  - 旧进程被 `_proc` 字段覆盖而失去引用，`Exited` 回调读到的是另一个进程的退出码
  - 修复：UI 侧加 `_starting` 重入保护；服务侧 `Exited` 回调捕获自己的进程引用

### 新增

- `scripts/test-gui-start.ps1`：通过 UI Automation **真正按下按钮**的端到端测试。
  此前只验证「窗口能打开」和 `--selftest`，两者在启动按钮完全失效时都会通过——
  v1.1.0 正是这样漏出去的。断言：徽标切到「运行中」、地址与 PID/运行时长出现、
  自检报告书目数、端口在监听、「打开书架」可用、10 秒后仍在运行；
  无资料库时退化为「启动被拒后界面仍可用」
- `scripts/build-winui.ps1` 与 CI `build-winui` job 接入该测试

### 已知环境问题

若 Windows 事件日志出现 `KiwixWinUI.exe` / `CoreMessagingXP.dll` / `0xc000027b`
崩溃，那是系统组件层面的间歇故障，非本项目代码问题：同一台机器上
`DumpSearch.exe`、`CasLoginHarness.exe` 等程序也在以相同特征崩溃。
连续 7 次启动验证均正常。

## [1.1.0] — 2026-09-26

Windows 图形启动器重写为 **WinUI 3**；原 Python/tkinter 版作为轻量备用保留。

### 新增

- **WinUI 3 启动器**（`src/KiwixWinUI/`）
  - Windows App SDK 1.7，**免打包（unpackaged）+ 自包含**发布：无 MSIX、无需安装、无需预装
    .NET 桌面运行时或 Windows App Runtime
  - 现代 Fluent 视觉：`InfoBar` 错误中心、`ListView` 内容库、实时预检状态条、进度条
  - 启动前五项预检、书目自检、端口顺延、SHA256 校验（显式触发），行为与 tkinter 版一致
  - `KiwixWinUI.exe --selftest`：无界面自证（定位资料库 → 起服务 → 核对服务器实际加载书目数 → 干净停止），
    CI 与脚本均可调用
- **tkinter 版也获得 `--selftest`**（`KiwixUSB.exe --selftest`），断言集与 WinUI 3 版一致
- `launcher/start-gui.cmd` 改为**优先启动 WinUI 3**，目录缺失时自动回退到 tkinter 单文件版
- `scripts/build-winui.ps1`：发布 + 产物校验 + 启动检查一步到位
- CI 新增 `build-winui` job（构建 → 校验文件数 → 启动 → 确认进程存活）

### 修复

- **Windows 版 `kiwix-serve.exe` 缺少配套 DLL，发布产物完全不可用**（严重）
  - `scripts/fetch-kiwix-binaries.ps1` 解压后只拷贝 `kiwix-*.exe`，把归档里同目录的
    DLL 全部丢弃。而 kiwix-tools **3.8.1 的 Windows 构建并非静态链接**：其 exe 仅约
    3.7 MB，依赖同归档中的 DLL（自带静态链接的 3.7.0 约 10 MB/个，可作对照）
  - 后果：`kiwix-serve.exe` 以 `0xC0000135 STATUS_DLL_NOT_FOUND` **瞬间退出**。
    启动器本身能正常打开，所以前一轮验收没有发现；只有在用户点「启动服务」时才暴露
  - 修复一：整目录拷贝（含 DLL），并在 Windows 主机上执行 `kiwix-serve.exe --version`
    校验，失败即抛错
  - 修复二：Windows 二进制抓取从 release job（ubuntu + pwsh，只能下载不能运行）
    迁到新的 `build-kiwix-windows` job（`windows-latest`），下载后**真正执行一次**
    再上传为 artifact
  - 修复三：原先的 `pwsh ... || echo "::warning::"` 会把抓取失败静默降级为警告，
    现已移除
- `KiwixUSB.py`：补回模块级 `free_port()`（重写时误删，导致 `start_service` 逻辑重复）
- 自检输出改为行缓冲，重定向到文件时不再因缓冲丢失尾部日志

### 已知限制

- WinUI 3 自包含产物约 **166 MB**（约 480 个文件），相比 tkinter 单文件 11 MB 大得多；
  这是「免安装」与「体积」之间的取舍。`start-gui.cmd` 会在两者间自动选择
- WinUI 3 免打包应用在**无桌面会话**的环境无法运行（与 tkinter 版不同，后者可用 `--selftest` 在
  纯命令行环境完成同等校验）

### 构建备注

WinUI 3 的 XAML 有两处易踩的坑，已在 `App.xaml` 注释中记录：WinUI 3 **不是 WPF**，
`Button` 没有 `CornerRadius`、`TextBox` 没有 `IsScrollBarEnabled`；写入不存在的属性会让
XAML 编译器**静默失败（退出码 1、无任何输出）**。XAML 文件必须走 SDK 默认的
`Page` / `ApplicationDefinition` 隐式项，既不能显式声明（重复项报错），
也不能设 `EnableDefaultPageItems=false`（会跳过 XAML 编译器，导致
`InitializeComponent()` 不存在）。

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
