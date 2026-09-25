# 离线内容库

这个目录用来放 **ZIM 离线资料库文件**，例如维基百科的镜像：

```
zim/
├── wikipedia_en_all_maxi_2026-08.zim     (118.7 GB)
└── wikipedia_zh_all_maxi_2026-05_2.zim   ( 26.2 GB)
```

> ⚠️ **ZIM 文件不进 Git 仓库**（体积达 TB 级，且遵循各自的独立许可）。
> 本目录已被 `.gitignore` 忽略，只保留 `.gitkeep` 与本说明。

## 从哪里获取 ZIM

- 官方内容列表：<https://wiki.kiwix.org/wiki/Content_in_all_languages>
- 镜像文件直链：<https://download.kiwix.org/zim/>

## 注意事项

| 项目 | 说明 |
|---|---|
| 磁盘格式 | 必须 **exFAT** 或 **NTFS**。FAT32 单文件上限 4 GB，装不下这些 ZIM |
| 空间 | 两本合计约 **144.8 GB** |
| 权限 | 文件对运行服务的用户可读即可（容器内以 uid 1001 运行时建议 `chmod 644`） |
| 校验 | 首次拷入后建议算一次 SHA256 对照官方 `.md5` / 校验和 |

## 快捷方式（同一磁盘内不占额外空间）

如果你暂时不想移动 145 GB 文件，Windows 上可以用**硬链接**临时挂进本目录，
零额外占用即可先试用：

```powershell
cd <repo>\zim
New-Item -ItemType HardLink -Path wikipedia_en_all_maxi_2026-08.zim `
         -Target $HOME\Downloads\wikipedia_en_all_maxi_2026-08.zim
```

> 移动硬盘时不适用（跨卷无法硬链接），需要真实拷贝。
