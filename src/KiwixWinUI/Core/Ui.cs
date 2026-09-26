using System;
using Windows.UI;

namespace KiwixWinUI;

/// <summary>
/// Small helpers shared by the code-behind. WinUI 3 has no
/// Windows.UI.ColorFromArgb, and only Microsoft.UI.ColorHelper.FromArgb
/// takes numeric channels, so hex strings get parsed here once.
/// </summary>
public static class Ui
{
    public static Color Hex(string hex)
    {
        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6) return Color.FromArgb(0xFF, 0x8B, 0x93, 0xA7);
        return Color.FromArgb(0xFF,
            Convert.ToByte(h.Substring(0, 2), 16),
            Convert.ToByte(h.Substring(2, 2), 16),
            Convert.ToByte(h.Substring(4, 2), 16));
    }
}
