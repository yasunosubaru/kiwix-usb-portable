@echo off
REM ============================================================
REM  Kiwix portable bundle - Windows launcher
REM  Put the *.zim files into the "zim" folder, then run this.
REM ============================================================
setlocal enabledelayedexpansion
set "DIR=%~dp0"
set "PORT=8092"
set "SERVE=%DIR%app\windows-x86_64\kiwix-serve.exe"

if not exist "%SERVE%" (
  echo [X] kiwix-serve.exe not found in "%DIR%app\windows-x86_64\"
  pause
  exit /b 1
)

set "ZIMS="
for %%f in ("%DIR%zim\*.zim") do (
  if exist "%%f" set "ZIMS=!ZIMS! "%%f""
)

if "!ZIMS!"=="" (
  echo [X] No .zim file found in "%DIR%zim\"
  echo     Copy wikipedia_*.zim into that folder first.
  pause
  exit /b 1
)

echo ========================================
echo   Kiwix starting on port %PORT%
echo   Open: http://127.0.0.1:%PORT%
echo   Close this window to stop the server
echo ========================================
echo.

start "" "http://127.0.0.1:%PORT%"
"%SERVE%" --port=%PORT% !ZIMS!
pause
