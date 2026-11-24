using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Halo;

internal sealed partial class SettingsPage
{
    static readonly string[] BackupDays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    readonly TextBlock backupDirectory = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly CheckBox backupEnabled = new() { Content = "启用定期备份" };
    readonly ComboBox backupFrequency = new() { Header = "频率", ItemsSource = new[] { "每天", "每周" } };
    readonly ComboBox backupDay = new() { Header = "星期", ItemsSource = BackupDays };
    readonly TimePicker backupTime = new() { Header = "执行时间", ClockIdentifier = "24HourClock" };
    readonly Button openBackupDirectory = new() { Content = "打开备份目录" };
    readonly Button runBackup = new() { Content = "立即备份" };
    readonly TextBlock backupStatus = new() { TextWrapping = TextWrapping.Wrap };
    string backupDirectoryDraft = "";
    string? backupUiError;

    void BuildBackupSettings(StackPanel body)
    {
        body.Children.Add(Section("面板备份"));
        body.Children.Add(backupDirectory);
        var choose = new Button { Content = "选择备份目录…" };
        var actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(choose);
        actions.Children.Add(openBackupDirectory);
        actions.Children.Add(runBackup);
        body.Children.Add(actions);
        body.Children.Add(backupEnabled);
        var schedule = new StackPanel { Spacing = 8 };
        schedule.Children.Add(backupFrequency);
        schedule.Children.Add(backupDay);
        schedule.Children.Add(backupTime);
        body.Children.Add(schedule);
        body.Children.Add(new TextBlock { Text = "每次生成新的 ZIP，历史备份由你手动清理。", TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetLiveSetting(backupStatus, AutomationLiveSetting.Polite);
        body.Children.Add(backupStatus);

        choose.Click += async (_, _) => await BackupSettingsAction(async () => {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(settingsHost));
            if (await picker.PickSingleFolderAsync() is { } folder) {
                App.Station.ValidateBackupDirectory(folder.Path);
                backupDirectoryDraft = folder.Path;
                BackupDraftChanged();
            }
        });
        openBackupDirectory.Click += async (_, _) => await BackupSettingsAction(async () => {
            if (!await Windows.System.Launcher.LaunchFolderPathAsync(backupDirectoryDraft)) throw new IOException("无法打开备份目录。");
        });
        runBackup.Click += async (_, _) => await BackupSettingsAction(() => App.Current.BackupNow(backupDirectoryDraft));
        backupEnabled.Checked += (_, _) => BackupDraftChanged();
        backupEnabled.Unchecked += (_, _) => BackupDraftChanged();
        backupFrequency.SelectionChanged += (_, _) => BackupDraftChanged();
        backupDay.SelectionChanged += (_, _) => BackupDraftChanged();
        backupTime.TimeChanged += (_, _) => BackupDraftChanged();
        Loaded += (_, _) => {
            App.Current.BackupStatusChanged += RefreshBackupSettings;
            RefreshBackupSettings();
        };
        Unloaded += (_, _) => App.Current.BackupStatusChanged -= RefreshBackupSettings;
    }

    void BackupDraftChanged()
    {
        if (loading) return;
        RefreshBackupSettings();
        UpdateSaveState();
    }

    async Task BackupSettingsAction(Func<Task> action)
    {
        backupUiError = null;
        try { await action(); }
        catch (Exception error) { backupUiError = error.Message; }
        RefreshBackupSettings();
    }

    void LoadBackupSettings(HaloBackupPlan? configured)
    {
        var plan = configured ?? new HaloBackupPlan();
        backupDirectoryDraft = plan.Directory;
        backupEnabled.IsChecked = plan.Enabled;
        backupFrequency.SelectedIndex = plan.Weekly ? 1 : 0;
        backupDay.SelectedIndex = (int)plan.Day;
        backupTime.Time = TimeSpan.FromMinutes(plan.Minute);
        backupUiError = null;
        RefreshBackupSettings();
    }

    HaloBackupPlan? BackupDraft(HaloBackupPlan? current)
    {
        var value = new HaloBackupPlan(backupDirectoryDraft, backupEnabled.IsChecked == true, backupFrequency.SelectedIndex != 0,
            (DayOfWeek)Math.Max(backupDay.SelectedIndex, 0), Math.Clamp((int)backupTime.Time.TotalMinutes, 0, 1439), current?.ActivatedAt);
        return current == null && value == new HaloBackupPlan() ? null : value;
    }

    void RefreshBackupSettings()
    {
        bool hasDirectory = backupDirectoryDraft.Length != 0;
        bool enabled = backupEnabled.IsChecked == true;
        backupDirectory.Text = hasDirectory ? backupDirectoryDraft : "尚未选择备份目录";
        openBackupDirectory.IsEnabled = hasDirectory;
        runBackup.IsEnabled = hasDirectory && !App.Current.BackupRunning;
        backupFrequency.IsEnabled = backupTime.IsEnabled = enabled;
        backupDay.Visibility = backupFrequency.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        backupDay.IsEnabled = enabled;
        backupStatus.Text = backupUiError ?? App.Current.BackupStatus;
    }
}
