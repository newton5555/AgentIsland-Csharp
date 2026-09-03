using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AgentIsland.UI;

/// Restores and raises one of the app's borderless windows when its menu or
/// settings entry is clicked again.
internal static class WindowActivation
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    public static void BringToFront(Window window)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            if (!window.IsVisible)
            {
                window.Show();
            }

            // Toggling an already-topmost window reasserts its position among
            // other topmost windows before the foreground handoff.
            if (window.Topmost)
            {
                window.Topmost = false;
                window.Topmost = true;
            }

            window.Activate();
            window.Focus();

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            ShowWindow(handle, SwRestore);
            BringWindowToTop(handle);
            SwitchToThisWindow(handle, true);
            SetForegroundWindow(handle);
        }
        catch
        {
            // A closing window or a desktop foreground lock must not interrupt
            // the menu click that requested it.
        }
    }
}
