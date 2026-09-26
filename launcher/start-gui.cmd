@echo off
REM ============================================================
REM  Kiwix offline wiki - portable Windows GUI launcher
REM
REM  Prefers the WinUI 3 build (app\gui\KiwixWinUI\). Falls
REM  back to the tkinter single file (app\gui\KiwixUSB.exe) so
REM  a slimmed-down bundle still starts.
REM
REM  Keep this file ASCII-only. cmd.exe decodes a batch file
REM  with the console code page (936 on a Chinese Windows), so
REM  UTF-8 Chinese in here is displayed as mojibake. The user-
REM  facing text lives in the GUI, which is properly localized.
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
    echo [!] WinUI 3 build not found, falling back to the tkinter build.
    start "" "%TKUI%"
    exit /b 0
)

echo [X] Neither GUI is present. This bundle looks incomplete.
echo     Expected: "%WINUI%"
echo     or:       "%TKUI%"
pause
exit /b 1
