using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed partial class TaskbarSettingsWindow
{
    private void BuildWindowThumbnailSettings(StackPanel page)
    {
        page.Children.Add(new TextBlock
        {
            Text = "预览配色跟随「右键菜单」页签中的菜单配色。", TextWrapping = TextWrapping.Wrap, Opacity = .72
        });
        var slider = FeedbackSlider("预览画面最大宽度", PreviewIconLayout.MinThumbnailWidth, PreviewIconLayout.MaxThumbnailWidth);
        slider.StepFrequency = 10;
        slider.LargeChange = 40;
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 58 };
        page.Children.Add(SettingRow("画面大小", "按原窗口比例缩放，拖动时预览，松手后保存。", Actions(slider, value)));

        var speed = FeedbackSlider("预览动画时长", PreviewIconLayout.MinThumbnailAnimationMs, PreviewIconLayout.MaxThumbnailAnimationMs);
        speed.StepFrequency = 10;
        speed.LargeChange = 50;
        var speedValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 };
        page.Children.Add(SettingRow("预览动画时长", "同时调节缩略图弹出和移开鼠标时收起的速度。数值越大，动画越慢。",
            Actions(speed, speedValue)));

        var window = new Grid();
        window.RowDefinitions.Add(new() { Height = new GridLength(12) });
        window.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        window.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        window.ColumnDefinitions.Add(new() { Width = new GridLength(3, GridUnitType.Star) });
        var toolbar = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 76, 118, 171)) };
        Grid.SetColumnSpan(toolbar, 2);
        window.Children.Add(toolbar);
        var sidebar = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 211, 224, 240)) };
        Grid.SetRow(sidebar, 1);
        window.Children.Add(sidebar);
        var sampleImage = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 231, 238, 247)), Child = window };
        var sample = new Grid { Padding = new Thickness(WindowThumbnailLayout.Padding), HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(6) };
        sample.RowDefinitions.Add(new() { Height = GridLength.Auto });
        sample.RowDefinitions.Add(new() { Height = new GridLength(WindowThumbnailLayout.FooterHeight) });
        sample.Children.Add(sampleImage);
        var footer = new Grid { ColumnSpacing = 6, IsHitTestVisible = false };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock { Text = "预览示例", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var close = WindowThumbnailPopup.CloseIcon();
        close.Margin = new Thickness(4, 0, 4, 0);
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 1);
        sample.Children.Add(footer);
        var sampleRow = new Grid { Margin = new Thickness(0, 0, 0, 8), IsHitTestVisible = false };
        AutomationProperties.SetName(sampleRow, "窗口大小预览");
        sampleRow.Children.Add(sample);
        page.Children.Add(sampleRow);
        void ApplySampleTheme()
        {
            var theme = MenuPreviewTheme(sampleRow.ActualTheme);
            sample.RequestedTheme = theme;
            sample.Background = accessibility.HighContrast ? new SolidColorBrush(colors.GetColorValue(UIColorType.Background))
                : PreviewApplicationStrip.MenuBackground(theme);
        }
        sampleRow.Loaded += (_, _) => ApplySampleTheme();
        sampleRow.ActualThemeChanged += (_, _) => ApplySampleTheme();

        void UpdateSample(int width)
        {
            value.Text = $"{width} DIP";
            double room = sampleRow.ActualWidth > 0 ? sampleRow.ActualWidth : width + 2 * WindowThumbnailLayout.Padding;
            sampleImage.Width = Math.Max(1, Math.Min(width, room - 2 * WindowThumbnailLayout.Padding));
            sampleImage.Height = sampleImage.Width * 10 / 16;
        }

        BindThumbnailSlider(slider, () => app.IconLayout.ThumbnailWidth,
            width => app.TrySetIconLayout(app.IconLayout with { ThumbnailWidth = width }),
            app.PreviewThumbnailWidth, UpdateSample);
        BindThumbnailSlider(speed, () => app.IconLayout.ThumbnailAnimationMs,
            duration => app.TrySetIconLayout(app.IconLayout with { ThumbnailAnimationMs = duration }),
            app.PreviewThumbnailAnimation, duration => speedValue.Text = $"{duration} 毫秒");
        sampleRow.SizeChanged += (_, _) => UpdateSample((int)Math.Round(slider.Value));
    }

    private static void BindThumbnailSlider(Slider slider, Func<int> read, Func<int, bool> save,
        Action<int> preview, Action<int> updateLabel)
    {
        bool syncing = false;
        uint? heldPointer = null;
        int SelectedValue() => (int)Math.Round(slider.Value);

        void ReadSaved()
        {
            syncing = true;
            slider.Value = read();
            syncing = false;
            updateLabel(SelectedValue());
            preview(SelectedValue());
        }

        void Save()
        {
            if (!slider.IsLoaded || heldPointer is not null || SelectedValue() == read()) return;
            if (!save(SelectedValue())) ReadSaved();
        }

        slider.ValueChanged += (_, _) =>
        {
            updateLabel(SelectedValue());
            if (syncing) return;
            preview(SelectedValue());
            // A track click can raise ValueChanged before PointerPressed reaches the slider.
            slider.DispatcherQueue.TryEnqueue(Save);
        };
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) => heldPointer = args.Pointer.PointerId), true);
        void ReleasePointer(object sender, PointerRoutedEventArgs args)
        {
            if (heldPointer != args.Pointer.PointerId) return;
            heldPointer = null;
            slider.DispatcherQueue.TryEnqueue(Save);
        }
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ReleasePointer), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(ReleasePointer), true);
        slider.Loaded += (_, _) => ReadSaved();
        slider.Unloaded += (_, _) =>
        {
            heldPointer = null;
            preview(read());
        };
        ReadSaved();
    }
}
