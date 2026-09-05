using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;

namespace AgentIsland.UI;

public partial class IslandWindow
{
    private bool _chromeTransparency;
    private const int WmStyleChanging = 0x007C;
    private const int WsExLayered = 0x00080000;

    // Opt in per process so language-triggered window recreation keeps the
    // same renderer. The shipping/default renderer remains per-pixel alpha.
    private void ConfigureTransparencyExperiment()
    {
        if (!Environment.GetCommandLineArgs().Any(arg =>
                string.Equals(arg, "--island-renderer=chrome", StringComparison.OrdinalIgnoreCase)))
            return;

        if (DwmIsCompositionEnabled(out var enabled) < 0 || !enabled)
            return; // Decide before creating the HWND; AllowsTransparency cannot change later.

        _chromeTransparency = true;
        AllowsTransparency = false;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1),
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });

        // Register before OnLoaded starts the existing cursor watchdog.
        Loaded += InitializeChromeTransparency;
    }

    private void InitializeChromeTransparency(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializeChromeTransparency;
        if (_windowSource is null) return;

        // WindowChrome also installs a hook during source initialization.
        // Re-add ours last so it runs before WPF's style enforcement and
        // before WindowChrome's rectangular non-client hit testing.
        _windowSource.RemoveHook(WindowMessageHook);
        _windowSource.AddHook(WindowMessageHook);
        var handle = _windowSource.Handle;
        var style = IntPtr.Size == 8
            ? GetWindowLongPtr64(handle, GwlExStyle).ToInt64()
            : GetWindowLong32(handle, GwlExStyle);
        if (IntPtr.Size == 8)
            SetWindowLongPtr64(handle, GwlExStyle, new IntPtr(style | WsExLayered));
        else
            SetWindowLong32(handle, GwlExStyle, unchecked((int)style) | WsExLayered);

        // Constant alpha keeps DWM glass rendering, while the layered style
        // enables cross-process WS_EX_TRANSPARENT hit-through. Do not call
        // UpdateLayeredWindow or enable WPF UsesPerPixelOpacity here.
        if (!SetLayeredWindowAttributes(handle, 0, 255, 0x2 /* LWA_ALPHA */))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "WindowChrome transparency initialization failed. Restart without --island-renderer=chrome.");
    }

    private bool PreserveExperimentalLayeredStyle(
        int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_chromeTransparency || message != WmStyleChanging || wParam.ToInt64() != GwlExStyle)
            return false;

        // STYLESTRUCT contains two DWORDs even on x64. WPF otherwise strips
        // WS_EX_LAYERED when AllowsTransparency is false, including whenever
        // our cursor watchdog toggles WS_EX_TRANSPARENT.
        var style = Marshal.ReadInt32(lParam, sizeof(int));
        Marshal.WriteInt32(lParam, sizeof(int), style | WsExLayered);
        handled = true;
        return true;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
}
