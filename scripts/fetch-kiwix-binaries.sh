#!/bin/bash
# ============================================================
#  从 Kiwix 官方源下载静态二进制（Linux / macOS）
#  用法:  bash scripts/fetch-kiwix-binaries.sh [版本号]
#  这些二进制不入 Git，构建时按需获取。
# ============================================================
set -euo pipefail

VER="${1:-${KIWIX_VERSION:-3.8.2}}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BUNDLE="${BUNDLE_DIR:-$ROOT/bundle}"
DL="$ROOT/.cache/kiwix-tools-$VER"
BASE_URL="https://download.kiwix.org/release/kiwix-tools"

mkdir -p "$BUNDLE/app" "$DL"

case "$(uname -s)" in
    Linux)  PLAT="linux" ;;
    Darwin) PLAT="macos" ;;
    *) echo "[X] 不支持的平台: $(uname -s)"; exit 1 ;;
esac
case "$(uname -m)" in
    x86_64|amd64)  ARCH="x86_64" ;;
    aarch64|arm64) ARCH="aarch64" ;;
    *) echo "[X] 不支持的架构: $(uname -m)"; exit 1 ;;
esac

SUBDIR="app/${PLAT}-${ARCH}"
FILE="${PLAT}-${ARCH}"
TARBALL="kiwix-tools_${PLAT}-${ARCH}-${VER}.tar.gz"

echo "[1/3] 下载 $TARBALL"
if [ ! -f "$DL/$TARBALL" ]; then
    curl -fL --retry 3 -o "$DL/$TARBALL" "$BASE_URL/$TARBALL"
else
    echo "      已缓存，跳过下载"
fi

echo "[2/3] 校验官方 MD5"
MD5_URL="$BASE_URL/$TARBALL.md5"
if curl -fsSL -o "$DL/$TARBALL.md5" "$MD5_URL" 2>/dev/null; then
    EXPECT=$(awk '{print $1; exit}' "$DL/$TARBALL.md5")
    ACTUAL=$(md5sum "$DL/$TARBALL" | awk '{print $1}')
    if [ "$EXPECT" != "$ACTUAL" ]; then
        echo "[X] MD5 校验失败: 期望 $EXPECT / 实际 $ACTUAL"
        exit 1
    fi
    echo "      MD5 OK: $ACTUAL"
else
    echo "      [i] 未提供 MD5，跳过校验"
fi

echo "[3/3] 解包到 $SUBDIR"
tar -xzf "$DL/$TARBALL" -C "$DL"
mkdir -p "$BUNDLE/$SUBDIR"
find "$DL" -type f \( -name 'kiwix-serve' -o -name 'kiwix-manage' -o -name 'kiwix-search' \) \
    -exec cp {} "$BUNDLE/$SUBDIR/" \;
chmod +x "$BUNDLE/$SUBDIR"/* 2>/dev/null || true

echo
echo "完成。当前 $SUBDIR 内容："
ls -lh "$BUNDLE/$SUBDIR"
echo
echo "下一步: 把 *.zim 放进 $BUNDLE/zim/ 然后运行 ./start.sh"
