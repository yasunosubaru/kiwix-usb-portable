# 把 Kiwix ZIM 搬到飞牛 fnOS + 外网访问 · 完整方案

> 现状：本机 `$HOME/Downloads\` 下两个 ZIM
> - `wikipedia_zh_all_maxi_2026-05_2.zim` = 26.16 GB
> - `wikipedia_en_all_maxi_2026-08.zim` = 118.67 GB
> - 合计 **≈ 144.8 GB**
>
> 目标：ZIM 放飞牛，服务在飞牛上跑，本机回收 145 GB，外网可访问。

---

## 0. 关键前提：fnOS 和群晖不一样

| | 群晖 DSM | **飞牛 fnOS（你的）** |
|---|---|---|
| 套件中心装原生服务 | ✅ SynoCommunity 有 Kiwix 套件 | ❌ 没有这套体系 |
| Docker | 有（看型号） | ✅ **系统自带，直接用** |
| 文件路径 | `/volume1/xxx` | **`/vol1/1000/xxx`**（1000 是用户 ID） |
| Docker 编排入口 | 容器管理 | **Docker → Compose → 新增项目** |
| 官方远程访问 | QuickConnect（不支持自定义端口） | **FN Connect**（同样**只代理飞牛自家应用**，不支持 kiwix） |

**结论：部署方式 = Docker（官方镜像 `ghcr.io/kiwix/kiwix-serve`）。**

---

## 1. 先做安全加固（必做，你刚中过木马）

你的 NAS 凭据曾以 Windows 凭据形式存在本机（（已脱敏：某 NAS 的 SMB 域名凭据条目）），
这在木马的窃取目标范围内（它有 `SMTP` / Cookie / 注册表读取能力）。先把 NAS 锁好，再谈外网暴露：

1. **改 fnOS 登录密码**（必做）
2. **开两步验证**（fnOS 账户设置里）
3. **SSH 用完就关**；要长期开着就换非标端口 + 只用密钥登录
4. **查操作/登录日志**，看有没有陌生 IP
5. 更新 fnOS 系统与 Docker 镜像
6. **不要把 fnOS 管理面板端口、Docker 的 2375、SSH 直接暴露到公网**
7. kiwix 容器**不需要特权模式**，别乱加 `--privileged`

---

## 2. 在飞牛上准备目录

你的 fnOS 版本 **1.2.0701**，Docker 功能齐全，路径方案没问题。✅

**已确认可用路径**：`/vol1/1000/kiwix`

上线前核对两件事：
1. `1000` 是不是你的用户 ID（多用户可能是 1001、1002…）
   —— 飞牛「文件管理」里右键文件夹 → **详细信息** → **复制原始路径**，以它为准
2. `/vol1` 是你打算放 ZIM 的那个存储空间，且有 **> 160 GB** 空闲

要建的结构（**先在文件管理里手动建好这两个空文件夹**，避免 Docker 用 root 身份自动创建后你在文件管理里没权限改）：
```
/vol1/1000/kiwix/
├── zim/            ← 放 *.zim（只读挂载）
└── library/        ← 放 library.xml（读写）
```

**磁盘空间**：目标卷至少要 **> 160 GB** 空闲。

---

## 3. 部署 Kiwix（Docker Compose，推荐配置）

### 3.1 开启 Docker
飞牛 → **设置** → 找到 **Docker** → 开启，并指定一个存储空间用于 Docker 数据（首次使用必须配）。

### 3.2 拉镜像
Docker → **镜像** → 拉取 `ghcr.io/kiwix/kiwix-serve`，标签 `latest`。
> 国内拉 `ghcr.io` 可能慢/失败。两个办法：
> - 在 Docker 设置里配镜像加速 / 走代理
> - 先在 PC 上 `docker pull` 再 `docker save` 拷过去（最稳）

### 3.3 创建 Compose 项目
Docker → **Compose** → **新增项目**
- 项目名：`kiwix`
- yaml 保存位置：`/vol1/1000/Docker/kiwix`
- 内容：

```yaml
services:
  kiwix:
    image: ghcr.io/kiwix/kiwix-serve:latest
    container_name: kiwix
    command: ['--library', '/data/lib/library.xml', '--monitorLibrary']
    ports:
      - "8092:8080"
    volumes:
      - /vol1/1000/kiwix/zim:/data/zim:ro
      - /vol1/1000/kiwix/library:/data/lib
    restart: unless-stopped
```

保存并启动。

**说明**（都已对过官方源码）：
- 容器**内部端口固定 8080**，外部端口随便映射（这里映射 8092）
- 容器以**非特权用户 uid 1001** 运行，要能读 `/data/zim`
- `--monitorLibrary` = library.xml 一变就自动重载

### 3.4 把 ZIM 注册进 library
飞牛：**计划任务**（推荐，不依赖 SSH）执行：
```bash
docker exec kiwix kiwix-manage /data/lib/library.xml add /data/zim/wikipedia_zh_all_maxi_2026-05_2.zim
docker exec kiwix kiwix-manage /data/lib/library.xml add /data/zim/wikipedia_en_all_maxi_2026-08.zim
docker exec kiwix kiwix-manage /data/lib/library.xml show
```

> ⚠️ **fnOS 1.1.19 起 SSH 默认关闭**（官方安全策略升级导致），要用命令行得先去 **系统设置 → SSH** 手动开启。
> 不想开 SSH 就直接用 **计划任务 → 新增 → 用户定义的脚本**，把上面三行粘进去跑一次。
> 镜像基于 `kiwix-tools`，一般自带 `kiwix-manage`；先跑 `docker exec kiwix kiwix-manage --help` 确认一下。
> 后续换新版本 ZIM 也是「add 新的 → remove 旧的」，**不用重启容器**。

### 3.5 访问
浏览器打开 `http://<NAS_IP>:8092` → 应该能看到两本维基。

---

## 3'. 更简单的替代配置（glob 模式）

不想管 library.xml 就用这个（**改 ZIM 后需要 `docker restart kiwix`**）：

```yaml
services:
  kiwix:
    image: ghcr.io/kiwix/kiwix-serve:latest
    container_name: kiwix
    command: ['--ipConnectionLimit=6', '*.zim']
    ports:
      - "8092:8080"
    volumes:
      - /vol1/1000/kiwix/zim:/data
    restart: unless-stopped
```

---

## 4. 搬运 ZIM

**顺序很重要：先算哈希 → 拷 → 验哈希 → 才删本机。**

```powershell
# 1) 本机先算
Get-FileHash '$HOME/Downloads\wikipedia_zh_all_maxi_2026-05_2.zim' -Algorithm SHA256
Get-FileHash '$HOME/Downloads\wikipedia_en_all_maxi_2026-08.zim' -Algorithm SHA256

# 2) 拷（换成你的 NAS IP / 共享名；飞牛 SMB 一般是 \\<IP>\<文件夹名>）
robocopy "$HOME/Downloads" "\\192.168.0.x\kiwix\zim" "wikipedia_*_all_maxi_*.zim" /J /Z /NP /TEE /LOG:./copy_zim.log

# 3) 对 NAS 副本再算一次比对，一致后才删本机 → 回收 ≈145 GB
Get-FileHash '\\192.168.0.x\kiwix\zim\wikipedia_zh_all_maxi_2026-05_2.zim' -Algorithm SHA256
Get-FileHash '\\192.168.0.x\kiwix\zim\wikipedia_en_all_maxi_2026-08.zim' -Algorithm SHA256
```

- `/J` = 无缓冲 IO，大文件更稳
- 你是 Wi-Fi（192.168.0.114），145 GB 大约 **40~90 分钟**，别中途断
- 飞牛**接了硬盘的话会休眠**，拷贝前先在飞牛里把该存储空间设为不自动休眠

---

## 5. 外网访问 · 4 个方案

| 方案 | 开公网端口？ | 安全性 | 难度 | 适合 |
|---|---|---|---|---|
| **Cloudflare Tunnel**（Docker 跑 cloudflared） | ❌ 不用 | 高，可加登录验证 | 中 | 想要公网网址 ⭐ |
| **Lucky**（飞牛应用中心自带） | 看配置 | 高 | 低 | 飞牛用户最省事的一站式方案 ⭐ |
| **Tailscale** | ❌ 不用 | **最高** | 低 | 只有自己/家人用 ⭐ |
| 公网 IP + 路由器端口映射 + 反代 | ✅ 443 | 中 | 中 | 有公网 IP、不想依赖第三方 |
| ~~FN Connect（飞牛官方）~~ | — | — | — | ❌ **不支持**，只代理飞牛自家应用；且基础版限 **2 Mbps** |

> **Kiwix 本身没有登录功能**，谁拿到网址谁就能看。维基内容虽是公开的，但"全网可访问"和"仅自己可访问"差别很大，建议加一层访问控制。

### 5.1 Cloudflare Tunnel（推荐）
1. 域名 NS 托管到 Cloudflare（免费套餐够用）
2. Cloudflare **Zero Trust → Networks → Tunnels → Create tunnel** → 复制 **Token**
3. 在飞牛的同一个 Compose 项目里追加（Docker 网络内按**服务名**互访，用**容器端口 8080**）：
   ```yaml
     cloudflared:
       image: cloudflare/cloudflared:latest
       container_name: cloudflared
       command: tunnel --no-autoupdate run --token <把 Token 粘这里>
       restart: unless-stopped
   ```
4. 回 Cloudflare 给 tunnel 加 **Public Hostname**：
   - 域名：`kiwix.你的域名`
   - Service：`HTTP` → `kiwix:8080`
5. 访问 `https://kiwix.你的域名`
6. （强烈建议）Cloudflare **Access** 加一条策略：只允许你的邮箱 OTP 登录

### 5.2 Lucky（飞牛应用中心自带）
在应用中心搜 **Lucky** 安装。它一个应用就能搞定：DDNS（Cloudflare / 阿里云 / 腾讯云）+ 反向代理 + 内网穿透（Cloudflare Tunnel / STUN / IPv6）。
配一条反向代理把域名指到 `http://127.0.0.1:8092` 即可。适合不想再注册 Cloudflare 的情况。

### 5.3 Tailscale（最安全，仅自用）
飞牛 Docker 里跑：
```yaml
  tailscale:
    image: tailscale/tailscale:latest
    container_name: tailscale
    hostname: fnos
    environment:
      - TS_AUTHKEY=你的-authkey
      - TS_STATE_DIR=/var/lib/tailscale
    volumes:
      - /vol1/1000/kiwix/tailscale:/var/lib/tailscale
    devices:
      - /dev/net/tun:/dev/net/tun
    cap_add:
      - net_admin
    restart: unless-stopped
```
PC / 手机装 Tailscale 客户端登录同一账号 → 外网访问 `http://fnos:8092`。
**全程不开任何公网端口。**

### 5.4 公网 IP + 端口映射
1. 确认真公网 IP：路由器 WAN 口 IP 必须和 `https://ip.sb` 看到的一致（不一致 = CGNAT，此路不通 → 改走 5.1/5.2/5.3）
2. 路由器映射 `443` → NAS
3. 飞牛装 **Lucky** 或 `nginx-proxy-manager` 做 HTTPS 反代 → `127.0.0.1:8092`
4. 证书用 Let's Encrypt（DNS 验证最省事，不用开 80）

---

## 6. 收尾

- 删本机两个 ZIM（**校验通过后**）→ 回收 ≈145 GB
- 本机自建的 Kiwix Server 可退役：`$HOME/KiwixServe\` + 桌面/开始菜单快捷方式（或改成指向飞牛的浏览器书签）
- `C:\Program Files\Kiwix\kiwix-desktop.exe`（桌面版）可留着离线用

---

## 7. NAS 上线后告诉我就行

1. 飞牛**版本**
2. Docker **存储空间**配好了没、拉 `ghcr.io` 镜像**顺不顺利**
3. 有没有**域名**（有没有 Cloudflare 账号）
4. 家宽有没有**公网 IP**（路由器 WAN IP 和 ip.sb 是否一致）
5. 外网要「**任何人可打开**」还是「**只有自己和家人**」
