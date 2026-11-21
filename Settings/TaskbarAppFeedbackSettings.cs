using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AITaskbar;

internal sealed partial class TaskbarSettingsWindow
{
    private void BuildAppFeedbackSettings(StackPanel page, Action<TaskbarAppFeedbackStyle> updateSample)
    {
        var defaults = TaskbarAppFeedbackStyle.Default;
        var active = new Button { CornerRadius = new CornerRadius(18) };
        var background = new Button { CornerRadius = new CornerRadius(18) };
        var diameter = FeedbackSlider("圆点直径", TaskbarAppFeedbackStyle.MinDiameter, TaskbarAppFeedbackStyle.MaxDiameter);
        var expansion = FeedbackSlider("外扩距离", TaskbarAppFeedbackStyle.MinExpansion, TaskbarAppFeedbackStyle.MaxExpansion);
        var diameterValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var expansionValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var hover = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(hover, "显示悬停底框");

        bool syncing = false;
        void Sync()
        {
            syncing = true;
            var style = app.AppFeedback;
            active.Content = style.Active;
            background.Content = style.Background;
            AutomationProperties.SetName(active, $"前台圆点颜色 {style.Active}");
            AutomationProperties.SetName(background, $"后台圆点颜色 {style.Background}");
            diameter.Value = style.Diameter;
            diameterValue.Text = $"{style.Diameter} DIP";
            expansion.Value = style.HoverExpansion;
            expansionValue.Text = $"{style.HoverExpansion} DIP";
            hover.IsOn = expansion.IsEnabled = style.ShowHover;
            syncing = false;
            updateSample(style);
        }

        void Save(TaskbarAppFeedbackStyle style)
        {
            if (syncing) return;
            app.TrySetAppFeedback(style);
            Sync();
        }

        void PickColor(Button anchor, bool foreground)
        {
            string current = foreground ? app.AppFeedback.Active : app.AppFeedback.Background;
            var picker = new ColorPicker
            {
                Color = TaskbarAppFeedbackStyle.Parse(current) ?? Microsoft.UI.Colors.Black,
                IsAlphaEnabled = false, IsHexInputVisible = true, IsColorChannelTextInputVisible = false, MaxWidth = 258
            };
            TaskbarAppFeedbackStyle WithColor() => foreground
                ? app.AppFeedback with { Active = TaskbarAppFeedbackStyle.Format(picker.Color) }
                : app.AppFeedback with { Background = TaskbarAppFeedbackStyle.Format(picker.Color) };
            var content = new StackPanel { Spacing = 8, MaxWidth = 258 };
            var flyout = new Flyout { Content = content };
            picker.ColorChanged += (_, _) =>
            {
                var draft = WithColor();
                app.PreviewAppFeedback(draft);
                updateSample(draft);
            };
            flyout.Closed += (_, _) =>
            {
                app.PreviewAppFeedback(app.AppFeedback);
                Sync();
            };
            content.Children.Add(picker);
            content.Children.Add(ActionButton("应用颜色", () =>
            {
                if (app.TrySetAppFeedback(WithColor())) flyout.Hide();
            }));
            flyout.ShowAt(anchor);
        }

        active.Click += (_, _) => PickColor(active, true);
        background.Click += (_, _) => PickColor(background, false);
        diameter.ValueChanged += (_, args) => Save(app.AppFeedback with { Diameter = (int)Math.Round(args.NewValue) });
        expansion.ValueChanged += (_, args) => Save(app.AppFeedback with { HoverExpansion = (int)Math.Round(args.NewValue) });
        hover.Toggled += (_, _) => Save(app.AppFeedback with { ShowHover = hover.IsOn });
        var controls = new StackPanel { Spacing = 4 };
        void AddRow(string title, string help, FrameworkElement input)
        {
            var row = SettingRow(title, help, input);
            row.MinHeight = 42;
            controls.Children.Add(row);
        }

        AddRow("窗口状态圆点", "预览栏中的已打开应用显示状态圆点。",
            ActionButton("恢复默认", () => Save(app.AppFeedback with
            {
                Active = defaults.Active, Background = defaults.Background, Diameter = defaults.Diameter
            }), "恢复窗口状态圆点默认值"));
        AddRow("前台圆点颜色", "当前正在使用的窗口。", active);
        AddRow("后台圆点颜色", "已打开但不在前台的窗口。", background);
        AddRow("圆点直径", "前台和后台圆点使用同样的大小。", Actions(diameter, diameterValue));
        AddRow("图标悬停底框", "将鼠标移到上方预览图标可查看效果。",
            ActionButton("恢复默认", () => Save(app.AppFeedback with
            {
                ShowHover = defaults.ShowHover, HoverExpansion = defaults.HoverExpansion
            }), "恢复图标悬停底框默认值"));
        AddRow("显示悬停底框", "鼠标移到应用图标时显示。", hover);
        AddRow("外扩距离", "底框在图标四周延伸的距离。", Actions(expansion, expansionValue));
        page.Children.Add(controls);
        Sync();
    }

    private static Slider FeedbackSlider(string title, int minimum, int maximum)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, StepFrequency = 1, MinWidth = 94 };
        AutomationProperties.SetName(slider, title);
        return slider;
    }
}
