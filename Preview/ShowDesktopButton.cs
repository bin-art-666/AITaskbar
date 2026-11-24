using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class ShowDesktopButton : Button
{
    internal const double EdgeWidth = 12;
    private readonly Grid highlight = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly Border glow = new();
    private readonly Border line = new() { Width = 2, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ShowDesktopWindows desktop = new();
    private readonly UISettings system = new();
    private CancellationTokenSource? transition;

    internal ShowDesktopButton(nint taskbar, Func<nint> foregroundAtPress, Action dismissThumbnails, Action<string> report, Action<Exception> log)
    {
        Width = EdgeWidth;
        MinWidth = MinHeight = 0;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        UseSystemFocusVisuals = true;
        Template = (ControlTemplate)XamlReader.Load("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                <ContentPresenter Background="{TemplateBinding Background}"
                    HorizontalContentAlignment="Stretch" VerticalContentAlignment="Stretch"
                    Content="{TemplateBinding Content}" />
            </ControlTemplate>
            """);
        highlight.Children.Add(glow);
        highlight.Children.Add(line);
        Content = highlight;
        AutomationProperties.SetName(this, "显示桌面");
        AutomationProperties.SetHelpText(this, "再次点击恢复窗口");
        ToolTipService.SetToolTip(this, "显示桌面");
        PointerEntered += (_, args) =>
        {
            if (args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse) highlight.Opacity = 1;
        };
        PointerExited += (_, _) => highlight.Opacity = 0;
        PointerCaptureLost += (_, args) => highlight.Opacity =
            args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
            new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight).Contains(args.GetCurrentPoint(this).Position) ? 1 : 0;
        Unloaded += (_, _) => transition?.Cancel();
        Click += async (_, _) =>
        {
            if (transition is not null) return;
            using var cancellation = new CancellationTokenSource();
            transition = cancellation;
            try
            {
                var foreground = foregroundAtPress();
                dismissThumbnails();
                await desktop.ToggleAsync(this, taskbar, foreground, system.AnimationsEnabled, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                log(error);
                report("暂时无法显示桌面。");
            }
            finally { transition = null; }
        };
    }

    internal void ApplyTone(bool light, UISettings system, bool highContrast)
    {
        Color ink = highContrast ? system.GetColorValue(UIColorType.Foreground)
            : light ? Color.FromArgb(235, 12, 74, 145) : Color.FromArgb(255, 188, 232, 255);
        line.Background = new SolidColorBrush(ink);
        var brush = new LinearGradientBrush { StartPoint = new(0, .5), EndPoint = new(1, .5) };
        brush.GradientStops.Add(new() { Offset = 0, Color = Color.FromArgb(0, ink.R, ink.G, ink.B) });
        brush.GradientStops.Add(new() { Offset = 1, Color = Color.FromArgb(85, ink.R, ink.G, ink.B) });
        glow.Background = highContrast ? null : brush;
        highlight.OpacityTransition = !highContrast && system.AnimationsEnabled
            ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(140) } : null;
    }

}
