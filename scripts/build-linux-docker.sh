#!/bin/bash
# ============================================================
#  Build the Linux GUI binary inside a Debian 12 container.
#
#  WHY: PyInstaller bundles libpython linked against the *build*
#  host's glibc. Building on Ubuntu 24.04 (glibc 2.39) yields a
#  binary that dies on Debian 12 / Synology fnOS (glibc 2.36) with
#      "libm.so.6: version `GLIBC_2.38' not found"
#  Pinning the build container to bookworm keeps the floor at 2.36.
#
#  Usage:  bash scripts/build-linux-docker.sh
# ============================================================
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${OUT_DIR:-$ROOT/dist}"
NAME="KiwixUSB-linux-x86_64"

command -v docker >/dev/null 2>&1 || {
    echo "[X] docker is required to reproduce the glibc 2.36 baseline"; exit 1; }

echo "[*] building $NAME inside debian:12 (glibc 2.36 baseline)"
docker run --rm -v "$ROOT":/src -v "$OUT":/out debian:12-slim bash -c '
  set -e
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -qq >/dev/null 2>&1
  # NOTE: libpython3.11 is required, otherwise PyInstaller aborts with
  #       "Python shared library (libpython3.11.so.1.0) was not found"
  apt-get install -y -qq \
      python3 python3-tk python3-venv libpython3.11 binutils \
      libx11-6 libxext6 libxrender1 >/dev/null 2>&1
  python3 -m venv /tmp/venv >/dev/null 2>&1
  /tmp/venv/bin/pip install -q --upgrade pip >/dev/null 2>&1
  /tmp/venv/bin/pip install -q -r /src/requirements-build.txt >/dev/null 2>&1
  /tmp/venv/bin/pyinstaller --noconfirm --clean --onefile --windowed \
      --name KiwixUSB-linux-x86_64 \
      --distpath /tmp/out --workpath /tmp/work /src/src/KiwixUSB.py >/dev/null 2>&1
  cp /tmp/out/KiwixUSB-linux-x86_64 /out/
  chmod +x /out/KiwixUSB-linux-x86_64
  # Report the highest glibc symbol the bundle actually requires.
  echo -n "highest GLIBC requirement: "
  mkdir -p /tmp/x && cp /out/KiwixUSB-linux-x86_64 /tmp/x/bin
  objdump -T /tmp/x/bin 2>/dev/null | grep -o "GLIBC_2\.[0-9]*" | sort -Vu | tail -1
'

echo
ls -lh "$OUT/$NAME"
echo "-> ship as release asset: $NAME"
