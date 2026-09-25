#!/bin/bash
# ============================================================
#  From Kiwix official source: fetch static binaries (Linux/macOS)
#
#  Usage:
#     bash scripts/fetch-kiwix-binaries.sh [version]
#     BUNDLE_DIR=/somewhere bash scripts/fetch-kiwix-binaries.sh
#
#  These binaries are NOT committed to git; fetch at build time.
#
#  kiwix-tools does not publish every platform on every release,
#  so the newest archive that actually exists is used and any
#  difference from the requested version is reported.
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
    *) echo "[X] unsupported platform: $(uname -s)"; exit 1 ;;
esac
case "$(uname -m)" in
    x86_64|amd64)  ARCH="x86_64" ;;
    aarch64|arm64) ARCH="aarch64" ;;
    *) echo "[X] unsupported arch: $(uname -m)"; exit 1 ;;
esac

SUBDIR="app/${PLAT}-${ARCH}"
PREFIX="kiwix-tools_${PLAT}-${ARCH}-"

echo "requested version: $VER"

# Discover what the index actually offers for this platform.
INDEX=$(curl -fsSL "$BASE_URL/" || true)
if [ -z "$INDEX" ]; then
    echo "[X] cannot read $BASE_URL/"
    exit 1
fi

PICK=$(printf '%s' "$INDEX" \
    | grep -o "${PREFIX}[0-9][0-9.]*\.tar\.gz" \
    | sed "s|^${PREFIX}||; s|\.tar\.gz$||" \
    | sort -V -u -r | head -n 1)

if [ -z "$PICK" ]; then
    echo "[X] no ${PLAT}-${ARCH} archive found upstream"
    exit 1
fi
echo "available ${PLAT}-${ARCH}: $PICK"

if [ "$PICK" != "$VER" ]; then
    echo "[i] no ${PLAT}-${ARCH} build for $VER, using $PICK"
    echo "    (normal: upstream ships platforms on separate schedules)"
fi

TARBALL="${PREFIX}${PICK}.tar.gz"

echo "[1/3] download $TARBALL"
if [ ! -f "$DL/$TARBALL" ]; then
    curl -fL --retry 3 -o "$DL/$TARBALL" "$BASE_URL/$TARBALL"
else
    echo "      cached"
fi

echo "[2/3] verify official MD5"
if curl -fsSL -o "$DL/$TARBALL.md5" "$BASE_URL/$TARBALL.md5" 2>/dev/null; then
    EXPECT=$(awk '{print $1; exit}' "$DL/$TARBALL.md5")
    ACTUAL=$(md5sum "$DL/$TARBALL" | awk '{print $1}')
    [ "$EXPECT" = "$ACTUAL" ] || { echo "[X] MD5 mismatch: $EXPECT vs $ACTUAL"; exit 1; }
    echo "      MD5 OK: $ACTUAL"
else
    echo "      [i] no MD5 published, skipping"
fi

echo "[3/3] extract -> $BUNDLE/$SUBDIR"
tar -xzf "$DL/$TARBALL" -C "$DL"
mkdir -p "$BUNDLE/$SUBDIR"
find "$DL" -type f \( -name 'kiwix-serve' -o -name 'kiwix-manage' -o -name 'kiwix-search' \) \
    -exec cp {} "$BUNDLE/$SUBDIR/" \;
chmod +x "$BUNDLE/$SUBDIR"/* 2>/dev/null || true

echo
ls -lh "$BUNDLE/$SUBDIR"
echo
echo "next: drop *.zim into $BUNDLE/zim/ and run ./start.sh"
