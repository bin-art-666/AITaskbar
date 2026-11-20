using Microsoft.UI.Xaml;

namespace AIEnter;

internal sealed partial class AiWindow
{
    async Task VerifyFloatFadeMotion()
    {
        var saved = options.Data; var style = PanelTheme.Current;
        void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
        try
        {
            foreach (bool floating in new[] { true, false })
            {
                options.Save(options.Data with { FloatingPanel = floating, Pinned = true });
                await Open(display, dockTop); await Task.Delay(UiDefaults.PanelEnterMilliseconds + 30);
                var original = current; var size = AppWindow.Size;
                bool animated = PanelMotion.Enabled;
                Hide();
                var rootElement = (UIElement)Content;
                Check(!IsOpen, "Animation delayed logical dismissal or taskbar state.");
                if (animated)
                    Check(IsClosing && AppWindow.IsVisible && !rootElement.IsHitTestVisible && !Native.IsWindowEnabled(WinRT.Interop.WindowNative.GetWindowHandle(this)), "Exit animation did not keep a noninteractive visual window.");
                await Task.Delay(35); await Open(display, dockTop);
                Check(IsOpen && !IsClosing && rootElement.IsHitTestVisible && Native.IsWindowEnabled(WinRT.Interop.WindowNative.GetWindowHandle(this)), "Reopen failed to cancel exit or restore input.");
                await Task.Delay(UiDefaults.PanelEnterMilliseconds + UiDefaults.PanelExitMilliseconds + 50);
                Check(IsOpen && AppWindow.IsVisible && ReferenceEquals(original, current) && AppWindow.Size == size, "Stale animation hid a reopened window or replaced its browser.");
                Hide();
                for (int attempt = 0; attempt < 40 && IsClosing; attempt++) await Task.Delay(20);
                Check(!IsClosing && !AppWindow.IsVisible, "Animated exit never hid the native window.");
            }
            PanelTheme.Apply(style with { Motion = UiMotion.Off });
            await Open(display, dockTop); Hide();
            Check(!IsClosing && !AppWindow.IsVisible, "Disabled motion still delayed hiding.");
        }
        finally { PanelTheme.Apply(style); options.Save(saved); await Open(display, dockTop); }
    }
}
