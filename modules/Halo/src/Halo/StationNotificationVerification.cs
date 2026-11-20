using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text.Json;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifyNotifications(Action<string, bool> check)
    {
        using (var retain = Retain()) {
            NotifyProgress("正在复制测试内容…");
            check("ProgressRemainsUntilOperationCompletes", statusHost.Visibility == Visibility.Visible && !feedback.IsRunning);
            string incoming = Path.Combine(App.DataDirectory, "incoming-drops"); Directory.CreateDirectory(incoming);
            string packet = Path.Combine(incoming, Guid.NewGuid() + ".json");
            await File.WriteAllTextAsync(packet, JsonSerializer.Serialize(new TaskbarPacket(Version: 999)));
            await App.Current.DrainDrops();
            check("DropFailureStartsDismissTimer", statusHost.Visibility == Visibility.Visible && status.Text.StartsWith("拖入未完成") && feedback.IsRunning && feedback.Interval == TimeSpan.FromSeconds(8));
            await Task.Delay(8400);
            check("DropFailureActuallyExpires", statusHost.Visibility == Visibility.Collapsed && status.Text.Length == 0 && !feedback.IsRunning);
            check("DismissedFailureKeepsRetryPacket", File.Exists(packet + ".retry"));
            // Only remove this generated invalid test packet; do not trigger another retry on reopen.
            File.Delete(packet + ".retry");
            Notify("测试成功");
            check("SuccessReplacesFailureWithShortTimer", status.Text == "测试成功" && feedback.IsRunning && feedback.Interval == TimeSpan.FromSeconds(4));
            NotifyProgress("正在备份测试内容…");
            check("ProgressStopsPreviousDismissTimer", statusHost.Visibility == Visibility.Visible && !feedback.IsRunning);
            Notify("备份失败测试", true);
            check("ProgressFailureBecomesTimedNotification", feedback.IsRunning && feedback.Interval == TimeSpan.FromSeconds(8));
        }
        Hide(); await Task.Delay(200);
        check("HideClearsOldNotification", statusHost.Visibility == Visibility.Collapsed && status.Text.Length == 0 && !feedback.IsRunning);
        Open(); await Task.Delay(80);
        check("ReopenDoesNotRestoreOldFailure", statusHost.Visibility == Visibility.Collapsed);
    }
}

public partial class App
{
    async Task VerifyNotificationSettings(Action<string, bool> check)
    {
        var panel = Panel!; var initial = Settings.Data;
        using var retain = panel.Retain();
        var legacy = JsonSerializer.Deserialize<HaloSettings>("{\"Version\":1}")!;
        check("LegacyNotificationTimingDefaults", legacy.NormalNotificationSeconds == 4 && legacy.ErrorNotificationSeconds == 8);
        foreach (var invalid in new[] { initial with { NormalNotificationSeconds = 0 }, initial with { NormalNotificationSeconds = 31 }, initial with { ErrorNotificationSeconds = 0 }, initial with { ErrorNotificationSeconds = 31 } }) {
            bool rejected = false; try { invalid.Validate(); } catch (InvalidDataException) { rejected = true; }
            check($"NotificationTimingRejects{invalid.NormalNotificationSeconds}_{invalid.ErrorNotificationSeconds}", rejected);
        }
        OpenSettings();
        await Task.Delay(100);
        var window = panel.SettingsForVerification;
        Slider Slider(string name) => window.FindSlider(name);
        void Set(int normal, int error) { Slider("普通提示显示时长").Value = normal; Slider("失败提示显示时长").Value = error; }
        void Invoke(string name) {
            var button = window.FindButton(name);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        Set(1, 2);
        check("NotificationTimingPreviewDoesNotSave", panel.NotificationTiming == new NotificationTiming(1, 2) && NotificationTiming.From(Settings.Data) == NotificationTiming.From(initial));
        foreach (var (label, message, seconds) in new[] { ("预览普通提示", "这是普通提示预览", 1), ("预览失败提示", "这是失败提示预览", 2) }) {
            Invoke(label); await Task.Delay(50);
            var sample = window.NotificationSampleForVerification;
            check($"NotificationSampleShown{seconds}", sample.IsOpen && sample.Message == message);
            await Task.Delay(TimeSpan.FromSeconds(seconds + .4));
            check($"NotificationCustomDurationActuallyExpires{seconds}", !sample.IsOpen);
        }
        Invoke("取消");
        check("NotificationTimingCancelRestores", panel.NotificationTiming == NotificationTiming.From(initial));
        OpenSettings(); Set(2, 3); Invoke("保存"); await Settings.Flush();
        var reloaded = new SettingsStore(Path.Combine(DataDirectory, "settings.json")).Data;
        check("NotificationTimingSaveAndReload", reloaded.NormalNotificationSeconds == 2 && reloaded.ErrorNotificationSeconds == 3 && panel.NotificationTiming == NotificationTiming.From(reloaded));
        OpenSettings();
        check("NotificationTimingReopenShowsSavedValues", Slider("普通提示显示时长").Value == 2 && Slider("失败提示显示时长").Value == 3);
        Set(initial.NormalNotificationSeconds, initial.ErrorNotificationSeconds); Invoke("保存"); await Settings.Flush();
        panel.ReturnFromSettings(); await Task.Delay(120);

    }
}
