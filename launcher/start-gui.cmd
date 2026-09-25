@echo off
REM ============================================================
REM  Kiwix 离线维基 - 便携版 GUI 启动器
REM  双击本文件即可。U 盘路径变了也没关系，脚本会自动定位。
REM ============================================================
setlocal
set "HERE=%~dp0"
if not exist "%HERE%app\gui\KiwixUSB.exe" (
  echo [X] 找不到 "%HERE%app\gui\KiwixUSB.exe"
  echo     整合包不完整，请重新拷贝。
  pause
  exit /b 1
)
if not exist "%HERE%zim\*.zim" (
  echo.
  echo [!] zim 目录里还没有 .zim 文件
  echo     请先把 wikipedia_*.zim 放到 "%HERE%zim\"  再运行。
  echo.
  pause
  exit /b 1
)
start "" "%HERE%app\gui\KiwixUSB.exe"
