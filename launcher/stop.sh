#!/bin/sh
# 停止本整合包启动的 kiwix-serve
if pkill -f kiwix-serve 2>/dev/null; then
    echo "[OK] kiwix-serve 已停止"
else
    echo "[i] 没有正在运行的 kiwix-serve"
fi
