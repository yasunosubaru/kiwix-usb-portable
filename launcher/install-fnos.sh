#!/bin/sh
# ============================================================
#  飞牛 fnOS 一键安装
#
#  作用：把 U 盘里的 ZIM 复制到 NAS 硬盘，再用 Docker 装好
#        kiwix 服务（开机自启）。这样读盘快、不依赖 U 盘一直插着。
#
#  用法：sudo sh install-fnos.sh
# ============================================================
set -e

DIR=$(cd "$(dirname "$0")" && pwd)
PORT=${KIWIX_PORT:-8092}
NAS_DIR=/vol1/1000/kiwix
NAS_ZIM="$NAS_DIR/zim"
IMG="$DIR/app/docker/kiwix-serve-legacy.tar"

echo "========================================"
echo "  Kiwix 整合版 -> 飞牛 fnOS 一键安装"
echo "========================================"
echo ""

# ---------- 前置检查 ----------
if [ "$(id -u)" != "0" ]; then
    echo "[X] 需要 root 权限"
    echo "    请改用:  sudo sh $0"
    exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
    echo "[X] 没找到 docker 命令"
    echo "    飞牛请先到: 设置 -> Docker -> 开启"
    exit 1
fi

if [ ! -f "$IMG" ]; then
    echo "[X] 找不到离线镜像: $IMG"
    echo "    可改用 Docker -> 镜像 -> 拉取 ghcr.io/kiwix/kiwix-serve"
    exit 1
fi

TOTAL=$(ls "$DIR"/zim/*.zim 2>/dev/null | wc -l)
if [ "$TOTAL" -eq 0 ]; then
    echo "[X] U 盘的 zim 目录里没有 .zim 文件"
    echo "    请先把 wikipedia_*.zim 放到 $DIR/zim/"
    exit 1
fi

# ---------- 1. 建目录 ----------
echo "[1/4] 创建目录 $NAS_ZIM"
mkdir -p "$NAS_ZIM"

# ---------- 2. 复制 ZIM ----------
echo "[2/4] 复制 ZIM 文件（共 $TOTAL 个，约 145GB，请耐心等待）"
i=0
for f in "$DIR"/zim/*.zim; do
    i=$((i + 1))
    base=$(basename "$f")
    if [ -f "$NAS_ZIM/$base" ]; then
        echo "      [$i/$TOTAL] $base  已存在，跳过"
    else
        echo "      [$i/$TOTAL] 正在复制 $base ..."
        cp "$f" "$NAS_ZIM/$base"
    fi
done
echo "      复制完成，当前内容："
ls -lh "$NAS_ZIM" | tail -n +2

# ---------- 3. 导入镜像 ----------
echo ""
echo "[3/4] 导入 Docker 离线镜像"
if docker images 2>/dev/null | grep -q "kiwix/kiwix-serve"; then
    echo "      镜像已存在，跳过导入"
else
    docker load -i "$IMG"
fi

# ---------- 4. 启动容器 ----------
echo ""
echo "[4/4] 启动 kiwix 容器"
docker rm -f kiwix >/dev/null 2>&1 || true
docker run -d \
    --name kiwix \
    --restart unless-stopped \
    -p "$PORT":8080 \
    -v "$NAS_ZIM":/data \
    ghcr.io/kiwix/kiwix-serve:latest '*.zim'

sleep 5

echo ""
docker ps --filter name=kiwix --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
echo ""
echo "--- 容器日志 ---"
docker logs kiwix 2>&1 | tail -n 10
echo ""

IP=$(hostname -I 2>/dev/null | awk '{print $1}')
cat <<EOF
========================================
  安装完成
  访问地址: http://$IP:$PORT
========================================
  打开上面地址，书架上应该能看到
  中文维基百科 和 英文维基百科 两本书。

  如果书架是空的，把上面的「容器日志」
  发出来即可定位。
========================================
EOF
