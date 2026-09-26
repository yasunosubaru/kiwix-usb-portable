/*
 * ============================================================
 *  Kiwix portable bundle - Windows entry point
 *
 *  A native Win32 GUI executable so the bundle is opened by
 *  double-clicking an .exe, not a .cmd script. It is deliberately
 *  tiny and dependency-free: a USB stick has to work on a machine
 *  with no .NET, no Python and no Windows App Runtime, which rules
 *  out both launchers it has to choose between.
 *
 *  What it does
 *  ------------
 *  1. Locates the bundle by walking up from its own directory looking
 *     for zim\ plus app\ -- the same rule KiwixService.ResolveRoot
 *     uses, so the two can never disagree about where the bundle is.
 *  2. Starts app\gui\KiwixWinUI\KiwixWinUI.exe (the WinUI 3 build).
 *  3. Falls back to app\gui\KiwixUSB.exe (the tkinter single file).
 *  4. If neither exists, shows a dialog naming both expected paths
 *     instead of failing silently.
 *
 *  It starts the launcher and exits: the GUI is a separate process
 *  with its own lifetime, and closing or killing this stub must never
 *  take the service down with it.
 *
 *  Build: cl /nologo /W4 /O2 /utf-8 /SUBSYSTEM:WINDOWS
 *         /NOENTRY winlauncher.c user32.lib shell32.lib
 * ============================================================
 */
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

#ifndef UNICODE
#error "must be compiled with UNICODE: the bundle path can contain Chinese characters"
#endif

#define MAX_TRY 6

/* Longest path we ever build. The GUI is a few levels below the root,
   and MAX_PATH still applies to what CreateProcess accepts here. */
static void join(wchar_t *out, const wchar_t *dir, const wchar_t *rel)
{
    _snwprintf_s(out, MAX_PATH, _TRUNCATE, L"%s\\%s", dir, rel);
}

static int is_dir(const wchar_t *p)
{
    DWORD a = GetFileAttributesW(p);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

static int is_file(const wchar_t *p)
{
    DWORD a = GetFileAttributesW(p);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

/* Walk up from this executable looking for the directory that holds both
   zim\ and app\. Returns 0 when nothing is found, which the caller turns
   into a message rather than a silent no-op. */
static int find_bundle_root(wchar_t *out)
{
    wchar_t dir[MAX_PATH];
    DWORD n = GetModuleFileNameW(NULL, dir, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return 0;

    for (int i = 0; i < MAX_TRY; i++) {
        wchar_t z[MAX_PATH], a[MAX_PATH];
        join(z, dir, L"zim");
        join(a, dir, L"app");
        if (is_dir(z) && is_dir(a)) {
            _snwprintf_s(out, MAX_PATH, _TRUNCATE, L"%s", dir);
            return 1;
        }
        wchar_t *slash = wcsrchr(dir, L'\\');
        if (slash == NULL) break;
        *slash = L'\0';
        if (dir[0] == L'\0') break;
    }
    return 0;
}

/* Start `exe` detached. The working directory is set to the bundle root so
   anything that resolves relative paths agrees with the GUI. */
static int spawn(const wchar_t *exe, const wchar_t *cwd)
{
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof(si));
    ZeroMemory(&pi, sizeof(pi));
    si.cb = sizeof(si);

    if (!CreateProcessW(exe, NULL, NULL, NULL, FALSE, 0, NULL, cwd, &si, &pi)) return 0;

    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 1;
}

static void report(const wchar_t *title, const wchar_t *text)
{
    MessageBoxW(NULL, text, title, MB_OK | MB_ICONERROR);
}

int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE hPrev, PWSTR cmdLine, int nShow)
{
    (void)hInst; (void)hPrev; (void)cmdLine; (void)nShow;

    wchar_t root[MAX_PATH];
    if (!find_bundle_root(root)) {
        report(L"找不到整合包",
               L"这个启动器需要在整合包根目录运行，但向上找不到同时包含 zim\\ 和 app\\ 的目录。\n\n"
               L"请确认它位于解压后的 Kiwix-USB 文件夹内，且 zim\\ 与 app\\ 没有被移动或改名。");
        return 2;
    }

    wchar_t winui[MAX_PATH], tk[MAX_PATH];
    join(winui, root, L"app\\gui\\KiwixWinUI\\KiwixWinUI.exe");
    join(tk,    root, L"app\\gui\\KiwixUSB.exe");

    if (is_file(winui) && spawn(winui, root)) return 0;
    if (is_file(tk)    && spawn(tk,    root)) return 0;

    wchar_t text[2048];
    _snwprintf_s(text, 2048, _TRUNCATE,
        L"整合包里没有找到任何图形启动器。\n\n已查找：\n%s\n\n和：\n%s\n\n"
        L"请重新解压完整的整合包，或检查安全软件是否把这些文件隔离了。\n\n"
        L"整合包根目录：%s",
        winui, tk, root);
    report(L"无法启动", text);
    return 3;
}
