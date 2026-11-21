using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AITaskbar;

internal sealed partial class TaskbarSettingsWindow
{
    private void BuildSoftwareIconSettings(StackPanel page)
    {
        var entries = new StackPanel { Spacing = 4 };
        page.Children.Add(new TextBlock
        {
            Text = "为正在运行、已固定或曾设为原始图标的应用选择样式。更改会即时保存。",
            TextWrapping = TextWrapping.Wrap, Opacity = .72
        });
        page.Children.Add(ActionButton("刷新应用列表", RefreshApps));
        page.Children.Add(entries);
        page.Loaded += (_, _) => RefreshApps();

        void RefreshApps()
        {
            IReadOnlyList<PinnedApp> pinned;
            try { pinned = app.PreviewPins; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                pinned = [];
                SetStatus("无法读取已固定的应用。");
            }

            var apps = AppGroups.Build(pinned, RunningWindows.List())
                .Where(group => group.IconExecutable is not null)
                .Select(group => new PinnedApp(group.Name, group.IconExecutable!))
                .Concat(app.IconPreferences.OriginalExecutables.Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => new PinnedApp(Path.GetFileNameWithoutExtension(path), path)))
                .Where(item => Path.IsPathFullyQualified(item.Executable) && !RunningWindows.IsFrameHost(item.Executable))
                .DistinctBy(item => item.Executable, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);

            entries.Children.Clear();
            foreach (var item in apps) entries.Children.Add(SoftwareIconRow(item));
            if (entries.Children.Count == 0)
                entries.Children.Add(new TextBlock { Text = "打开一个应用后刷新列表。", TextWrapping = TextWrapping.Wrap });
        }
    }

    private FrameworkElement SoftwareIconRow(PinnedApp appEntry)
    {
        var choice = new ComboBox { MinWidth = 124 };
        choice.Items.Add("使用圆角图标");
        choice.Items.Add("使用原始图标");
        choice.SelectedIndex = app.IconPreferences.UsesOriginal(appEntry.Executable) ? 1 : 0;
        AutomationProperties.SetName(choice, $"{appEntry.Name} 图标样式");
        var row = SettingRow(appEntry.Name, "", choice);
        ToolTipService.SetToolTip(row, appEntry.Executable);

        choice.SelectionChanged += (_, _) =>
        {
            bool original = app.IconPreferences.UsesOriginal(appEntry.Executable);
            if (choice.SelectedIndex < 0 || original == (choice.SelectedIndex == 1)) return;
            if (!app.TrySetOriginalIcon(appEntry.Executable, !original)) choice.SelectedIndex = original ? 1 : 0;
        };
        return row;
    }
}
