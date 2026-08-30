using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Nairdwood.Launcher.Services;

public static class WindowAppearance
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    public static void ApplyDarkChrome(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var enabled = 1;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

        var caption = ToColorRef(0x2E, 0x2E, 0x2E);
        var border = ToColorRef(0x20, 0x20, 0x20);
        var text = ToColorRef(0xF4, 0xF5, 0xF7);
        DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
        DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
    }

    private static int ToColorRef(byte red, byte green, byte blue) => red | (green << 8) | (blue << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);
}
