using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KiwixApple.Core;

/// <summary>
/// A WinExe has no console, so <c>Console.WriteLine</c> from <c>--selftest</c>
/// would go nowhere and the CI script would see an empty log. This gets a real
/// stdout back, in UTF-8, whichever way the process was started.
/// </summary>
public static class ConsoleBridge
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    public static TextWriter OpenOutput()
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                // Already a pipe. Console.Out would encode through the OEM code
                // page, which turns every Chinese log line into mojibake, so
                // write UTF-8 straight to the handle instead.
                return new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
                {
                    AutoFlush = true
                };
            }

            // Started by hand (double-clicked, or run from a console that did
            // not redirect): borrow the launching console so the user sees the
            // verdict scroll past.
            if (!AttachConsole(AttachParentProcess)) return TextWriter.Null;
            Console.OutputEncoding = new UTF8Encoding(false);
            return Console.Out;
        }
        catch
        {
            // No console anywhere. Not an error: the GUI itself does not need one.
            return TextWriter.Null;
        }
    }
}
