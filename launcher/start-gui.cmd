@echo off
REM ============================================================
REM  Kiwix 离线维基 - 便携版 GUI 启动器
REM  优先使用 WinUI 3 版（app\gui\KiwixWinUI\KiwixWinUI.exe）；
REM  若该目录不存在，则回退到 Tkinter 单文件版（app\gui\KiwixUSB.exe）。
REM ============================================================
setlocal
set "HERE=%~dp0"
set "WINUI=%HERE%app\gui\KiwixWinUI\KiwixWinUI.exe"
set "TKUI=%HERE%app\gui\KiwixUSB.exe"

if exist "%WINUI%" (
    start "" "%WINUI%"
    exit /b 0
)

if exist "%TKUI%" (
    echo [!] 未找到 WinUI 3 版，回退到 Tkinter 版
    start "" "%TKUI%"
    exit /b 0
)

echo [X] 两个 GUI 都不存在，请检查整合包是否完整
echo     期望: "%WINUI%"
echo     或:   "%TKUI%"
pause
exit /b 1
