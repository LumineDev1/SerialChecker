using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SerialChecker;

/// <summary>
/// Gives borderless WindowChrome windows a native Windows 11 look (dark frame, rounded corners)
/// and keeps maximized windows from spilling past the screen edge.
/// </summary>
internal static class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyFrame(window);
        window.StateChanged += (_, _) => UpdateMaximizedInset(window);
    }

    /// <summary>Re-tints the frame border of every open window after a theme change.</summary>
    public static void RefreshFrames()
    {
        foreach (Window window in Application.Current.Windows)
            ApplyFrame(window);
    }

    private static int FrameBorderColor()
    {
        // COLORREF is 0x00BBGGRR.
        Color color = Application.Current.TryFindResource("Border.Subtle") is SolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0x20, 0x24, 0x2C);
        return color.R | (color.G << 8) | (color.B << 16);
    }

    public static void ToggleMaximize(Window window)
    {
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static void ApplyFrame(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            int enabled = 1;
            int corner = DwmwcpRound;
            int border = FrameBorderColor();
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static void UpdateMaximizedInset(Window window)
    {
        // A maximized WindowChrome window extends past the work area by the resize border.
        window.BorderThickness = window.WindowState == WindowState.Maximized
            ? new Thickness(7)
            : new Thickness(0);
    }
}
