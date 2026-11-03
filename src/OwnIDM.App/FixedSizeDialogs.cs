using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Controls;

namespace OwnIDM.App;

/// <summary>
/// Keeps fixed-size dialogs (ResizeMode NoResize, sized to their content) from being resized by the mouse.
/// WPF-UI's TitleBar answers WM_NCHITTEST with the resize edges whatever the ResizeMode, so Windows let the
/// user drag an edge, and the fixed-width content was clipped: the buttons disappeared.
/// </summary>
internal static class FixedSizeDialogs
{
    private const int WM_SETCURSOR = 0x0020;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int HTLEFT = 10, HTBOTTOMRIGHT = 17;
    private const int IDC_ARROW = 32512;

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(FluentWindow), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Attach((Window)sender)));

    private static void Attach(Window window)
    {
        if (window.ResizeMode != ResizeMode.NoResize || PresentationSource.FromVisual(window) is not HwndSource source)
            return;
        source.RemoveHook(Hook); // Loaded can fire again
        source.AddHook(Hook);
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when IsEdge((int)wParam):
                // No drag to resize, and no double-click on an edge to stretch it to the screen.
                handled = true;
                return IntPtr.Zero;
            case WM_SETCURSOR when IsEdge((short)((long)lParam & 0xFFFF)):
                SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
                handled = true;
                return 1;
        }
        return IntPtr.Zero;
    }

    private static bool IsEdge(int hitTest) => hitTest is >= HTLEFT and <= HTBOTTOMRIGHT;

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, int name);
}
