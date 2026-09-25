#!/bin/sh
# ============================================================
#  Kiwix 免安装整合版 - 启动器 (Linux / macOS / 飞牛 fnOS)
#  用法:  chmod +x start.sh  然后  ./start.sh
#  也可双击（部分桌面环境需先给执行权限）
# ============================================================
DIR=$(cd "$(dirname "$0")" && pwd)
PORT=${KIWIX_PORT:-8092}
ZIMDIR="$DIR/zim"

echo "========================================"
echo "  Kiwix 免安装整合版"
echo "========================================"
echo ""

# ---------- 1. 按 CPU 架构挑选二进制 ----------
ARCH=$(uname -m)
case "$ARCH" in
    x86_64|amd64)                SUBDIR="linux-x86_64"  ;;
    aarch64|arm64)               SUBDIR="linux-aarch64" ;;
    armv7l|armv6l)               SUBDIR="linux-armv8"   ;;
    i386|i486|i586|i686)         SUBDIR="linux-i586"    ;;
    *) echo "[X] 不支持的 CPU 架构: $ARCH"; exit 1 ;;
esac
SERVE="$DIR/app/$SUBDIR/kiwix-serve"

if [ ! -f "$SERVE" ]; then
    echo "[X] 找不到: $SERVE"
    echo "    本整合包内置的架构: $(ls "$DIR/app" 2>/dev/null | tr '\n' ' ')"
    exit 1
fi
chmod +x "$DIR/app/$SUBDIR"/* 2>/dev/null
echo "[OK] 使用二进制: app/$SUBDIR (架构 $ARCH)"

# ---------- 2. 检查 ZIM 文件 ----------
if ! ls "$ZIMDIR"/*.zim >/dev/null 2>&1; then
    echo ""
    echo "[X] $ZIMDIR 目录里没有 .zim 文件"
    echo "    请把 wikipedia_*.zim 放进去再运行。"
    exit 1
fi
echo "[OK] 找到 ZIM 文件:"
for f in "$ZIMDIR"/*.zim; do
    printf "      %-52s %s\n" "$(basename "$f")" "$(du -h "$f" | cut -f1)"
done

# ---------- 3. 端口占用自动顺延 ----------
while command -v ss >/dev/null 2>&1 && ss -ltn 2>/dev/null | grep -q ":$PORT "; do
    echo "    端口 $PORT 被占用，试试 $((PORT+1)) ..."
    PORT=$((PORT+1))
done

# ---------- 4. 取本机 IP ----------
IP=$(hostname -I 2>/dev/null | awk '{print $1}')
[ -z "$IP" ] && IP=$(ipconfig getifaddr en0 2>/dev/null)
[ -z "$IP" ] && IP=127.0.0.1

cat <<EOF

========================================
  启动中...
  本机访问:   http://127.0.0.1:$PORT
  局域网访问: http://$IP:$PORT
  停止服务:   Ctrl+C  或另开终端 ./stop.sh
========================================

EOF

exec "$SERVE" --port="$PORT" "$ZIMDIR"/*.zim
