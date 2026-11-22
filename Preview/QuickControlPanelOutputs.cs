using Microsoft.UI.Xaml.Automation;
using Windows.System;

namespace AITaskbar;

internal sealed partial class QuickControlPanel
{
    private readonly Func<SystemVolume.Output[]?> readOutputs;
    private readonly Func<string, bool> switchOutput;
    private SystemVolume.Output[]? displayedOutputs;

    private void RefreshOutputs(bool force = false, string? error = null)
    {
        var devices = readOutputs();
        if (!force && (devices == displayedOutputs || devices is not null && displayedOutputs is not null &&
            devices.SequenceEqual(displayedOutputs))) return;
        displayedOutputs = devices;
        detailList.Children.Clear();
        listStatus.Text = error ?? (devices is null ? "暂时无法读取音频输出设备。" :
            devices.Length == 0 ? "没有可用的音频输出设备。" : "");
        if (devices is not null)
        {
            foreach (var output in devices)
            {
                var button = DetailAction(output.Name, output.Selected ? "当前输出" : "");
                AutomationProperties.SetItemStatus(button, output.Selected ? "当前输出" : "");
                button.Click += (_, _) =>
                {
                    if (output.Selected) return;
                    bool changed = switchOutput(output.Id);
                    RefreshOutputs(force: true, changed ? null : "Windows 未能切换输出设备，可在声音设置中重试。");
                    detailClose.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                };
                detailList.Children.Add(button);
            }
        }
        var settings = DetailAction("Windows 声音设置", "打开");
        settings.Click += async (_, _) =>
        {
            try { if (await Launcher.LaunchUriAsync(new Uri("ms-settings:sound"))) Hide(); }
            catch (Exception error) when (QuickControlRadios.IsUnavailable(error)) { }
        };
        detailList.Children.Add(settings);
    }
}
