@echo off
REM ============================================================
REM  Kiwix offline wiki - portable Windows GUI launcher
REM
REM  Tries the three front-ends in the same order Kiwix.exe does:
REM  the WinUI 3 build, then the Avalonia build, then the
REM  tkinter single file, so a slimmed-down bundle still starts.
REM
REM  The order matters and is deliberate. WinUI 3 is the primary
REM  GUI. Avalonia is second because it is also self-contained,
REM  so a machine that cannot run WinUI 3 for want of the
REM  Windows App Runtime can still run it. tkinter is last
REM  because it is the smallest and most conservative.
REM
REM  Keep this file ASCII-only. cmd.exe decodes a batch file
REM  with the console code page (936 on a Chinese Windows), so
REM  UTF-8 Chinese in here is displayed as mojibake. The user-
REM  facing text lives in the GUI, which is properly localized.
REM ============================================================
setlocal
set "HERE=%~dp0"
set "WINUI=%HERE%app\gui\KiwixWinUI\KiwixWinUI.exe"
set "APPLE=%HERE%app\gui\KiwixApple\KiwixApple.exe"
set "TKUI=%HERE%app\gui\KiwixUSB.exe"

if exist "%WINUI%" (
    start "" "%WINUI%"
    exit /b 0
)

if exist "%APPLE%" (
    echo [!] WinUI 3 build not found, falling back to the Avalonia build.
    start "" "%APPLE%"
    exit /b 0
)

if exist "%TKUI%" (
    echo [!] WinUI 3 and Avalonia builds not found, falling back to tkinter.
    start "" "%TKUI%"
    exit /b 0
)

echo [X] No GUI is present. This bundle looks incomplete.
echo     Expected: "%WINUI%"
echo     or:       "%APPLE%"
echo     or:       "%TKUI%"
pause
exit /b 1
