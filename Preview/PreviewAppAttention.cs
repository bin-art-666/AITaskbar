using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace AITaskbar;

internal sealed partial class PreviewApplicationStrip
{
    private static readonly TimeSpan AttentionCooldown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AttentionRetryLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AttentionIdentityDelay = TimeSpan.FromMilliseconds(1200);
    private readonly record struct AttentionWindow(nint Handle, uint ProcessId)
    {
        internal bool IsAlive => ProcessId != 0 && RunningWindows.ProcessIdForWindow(Handle) == ProcessId;
    }

    private sealed record PendingAttention(long ReceivedAt, string? Executable, string? AppId);
    private sealed record AppAttention(string GroupKey, bool MatchApplicationOnActivation);
    private readonly Dictionary<AttentionWindow, AppAttention> attentionWindows = [];
    private readonly Dictionary<AttentionWindow, PendingAttention> pendingAttention = [];
    private readonly Dictionary<string, long> lastAttentionPulse = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherQueueTimer attentionRetryTimer;
    private readonly AppAttentionDiagnosticLog attentionLog;

    private bool CanAnimateAttention => system.AnimationsEnabled && !accessibility.HighContrast;
    private bool AttentionEnabled => iconLayout.AppAttention != AppAttentionMode.Off ||
        iconLayout.AppAttentionOverrides.ContainsValue(AppAttentionOverride.DotOnly);

    private bool HasAppAttention(AppGroup group) => iconLayout.AttentionMode(group.Key) != AppAttentionMode.Off && attentionWindows.Any(entry =>
        AppKeys.Equals(entry.Value.GroupKey, group.Key) || group.Windows.Any(window =>
            window.Handle == entry.Key.Handle && window.ProcessId == entry.Key.ProcessId));

    internal void UpdateAppAttention(nint handle, bool flashing)
    {
        if (!flashing)
        {
            if (ClearWindowAttention(handle)) UpdateIndicators();
            return;
        }

        var source = new AttentionWindow(handle, RunningWindows.ProcessIdForWindow(handle));
        if (source.ProcessId == 0 || source.ProcessId == Environment.ProcessId) return;
        attentionLog.Record("收到信号", source.Handle, source.ProcessId);
        if (!AttentionEnabled || pendingAttention.ContainsKey(source) || attentionWindows.ContainsKey(source)) return;

        if (items.IsLoaded && TryApplyAttention(source, null)) return;
        var (executable, processAppId) = RunningWindows.DescribeProcess(source.ProcessId);
        string? appId = RunningWindows.IsFrameHost(executable) ? RunningWindows.WindowAppId(handle) : processAppId;
        pendingAttention.Add(source, new(Stopwatch.GetTimestamp(), executable, appId));
        attentionLog.Record("等待匹配", source.Handle, source.ProcessId);
        if (!attentionRetryTimer.IsRunning) attentionRetryTimer.Start();
    }

    private bool TryApplyAttention(AttentionWindow source, PendingAttention? pending)
    {
        if (!source.IsAlive) return true;
        nint root = RunningWindows.RootOwnerOrSelf(source.Handle);
        var target = source;
        var group = FindAttentionGroup(source);
        if (group is null && root != source.Handle)
        {
            var owner = new AttentionWindow(root, RunningWindows.ProcessIdForWindow(root));
            if (owner.ProcessId == source.ProcessId && FindAttentionGroup(owner) is { } ownerGroup)
            {
                target = owner;
                group = ownerGroup;
            }
        }

        bool matchApplication = false;
        if (group is null && pending is not null && Stopwatch.GetElapsedTime(pending.ReceivedAt) >= AttentionIdentityDelay)
        {
            group = FindAttentionApplication(pending);
            matchApplication = group is not null && !RunningWindows.IsTaskbarWindowCandidate(source.Handle);
        }
        if (group is null) return false;

        var mode = iconLayout.AttentionMode(group.Key);
        if (mode == AppAttentionMode.Off)
        {
            attentionLog.Record("提醒已关闭", source.Handle, source.ProcessId, group);
            return true;
        }

        nint foregroundHandle = RunningWindows.RawForeground();
        if (IsAttentionWindowForeground(source, foregroundHandle) || IsAttentionWindowForeground(target, foregroundHandle))
        {
            attentionLog.Record("窗口已在前台", source.Handle, source.ProcessId, group);
            return true;
        }

        bool alreadyPending = HasAppAttention(group);
        if (!attentionWindows.TryAdd(target, new(group.Key, matchApplication))) return true;
        attentionLog.Record("登记红点", target.Handle, target.ProcessId, group);
        UpdateIndicators();
        if (!alreadyPending && mode == AppAttentionMode.PulseAndDot && CanAnimateAttention && HasAppAttention(group) &&
            (!lastAttentionPulse.TryGetValue(group.Key, out long last) || Stopwatch.GetElapsedTime(last) >= AttentionCooldown) &&
            Shown.FirstOrDefault(item => AppKeys.Equals(item.Group.Key, group.Key)) is { } view)
        {
            lastAttentionPulse[group.Key] = Stopwatch.GetTimestamp();
            attentionBadges.Pulse(view.IconPlate);
        }
        return true;
    }

    private AppGroup? FindAttentionGroup(AttentionWindow target) => groups.FirstOrDefault(group =>
        group.Windows.Any(window => window.Handle == target.Handle && window.ProcessId == target.ProcessId));

    private AppGroup? FindAttentionApplication(PendingAttention pending)
    {
        if (pending.AppId is { } appId && FindApp(groups, "app:" + appId) is { } packaged) return packaged;
        if (pending.Executable is not { } executable || RunningWindows.IsFrameHost(executable)) return null;
        return FindApp(groups, "exe:" + executable);
    }

    private void RetryAppAttention()
    {
        foreach (var (source, pending) in pendingAttention.ToArray())
        {
            if (!source.IsAlive || Stopwatch.GetElapsedTime(pending.ReceivedAt) >= AttentionRetryLimit)
            {
                attentionLog.Record(source.IsAlive ? "匹配超时" : "窗口已关闭", source.Handle, source.ProcessId);
                pendingAttention.Remove(source);
            }
            else if (items.IsLoaded && TryApplyAttention(source, pending)) pendingAttention.Remove(source);
        }
        if (pendingAttention.Count == 0) attentionRetryTimer.Stop();
    }

    private static bool IsAttentionWindowForeground(AttentionWindow window, nint foregroundHandle) =>
        foregroundHandle != 0 && window.IsAlive &&
        (window.Handle == foregroundHandle || window.Handle == RunningWindows.RootOwnerOrSelf(foregroundHandle));

    private static bool IsAttentionGroupForeground(AppGroup group, nint foregroundHandle) =>
        group.Windows.Any(window => RunningWindows.MatchesForeground(window, foregroundHandle));

    private bool ClearWindowAttention(nint handle)
    {
        if (handle == 0) return false;
        foreach (var source in pendingAttention.Keys.Where(window => IsAttentionWindowForeground(window, handle)).ToArray())
        {
            pendingAttention.Remove(source);
            attentionLog.Record("窗口已在前台，取消匹配", source.Handle, source.ProcessId);
        }
        var cleared = attentionWindows.Where(entry => IsAttentionWindowForeground(entry.Key, handle) ||
            entry.Value.MatchApplicationOnActivation && FindAttentionGroup(entry.Key) is null &&
            FindApp(groups, entry.Value.GroupKey) is { } group &&
            IsAttentionGroupForeground(group, handle)).Select(entry => entry.Key).ToArray();
        foreach (var window in cleared)
        {
            attentionWindows.Remove(window);
            attentionLog.Record("窗口进入前台，清除红点", window.Handle, window.ProcessId);
        }
        return cleared.Length > 0;
    }

    private void PruneAppAttention()
    {
        foreach (var window in attentionWindows.Keys.Where(window => !window.IsAlive).ToArray())
        {
            attentionWindows.Remove(window);
            attentionLog.Record("窗口关闭，清除红点", window.Handle, window.ProcessId);
        }
        foreach (string key in lastAttentionPulse.Where(entry => Stopwatch.GetElapsedTime(entry.Value) >= AttentionCooldown)
            .Select(entry => entry.Key).ToArray()) lastAttentionPulse.Remove(key);
    }

    private void ApplyAttentionPreferences()
    {
        if (!AttentionEnabled) { ResetAppAttention(); return; }
        foreach (var (window, state) in attentionWindows.ToArray())
        {
            var key = FindAttentionGroup(window)?.Key ?? state.GroupKey;
            if (iconLayout.AttentionMode(key) != AppAttentionMode.Off) continue;
            attentionWindows.Remove(window);
            attentionLog.Record("关闭应用提醒，清除红点", window.Handle, window.ProcessId);
        }
    }

    private void UpdateAttentionOverflow()
    {
        bool left = false, right = false;
        double width = runningApps.Panel.Width;
        if (attentionWindows.Count > 0 && width > 0 && runningApps.LastStart > 0)
        {
            int first = Array.FindIndex(groups, group => group.Pin is null);
            if (first >= 0)
            {
                double step = iconSize + iconLayout.Gap;
                for (int i = first; i < groups.Length && !(left && right); i++)
                {
                    double center = (i - first) * step + iconSize / 2d - scrollPosition;
                    if ((center < 0 || center >= width) && HasAppAttention(groups[i]))
                    {
                        left |= center < 0;
                        right |= center >= width;
                    }
                }
            }
        }
        attentionBadges.SetOverflow(runningApps.Panel, left, right, accessibility.HighContrast
            ? system.UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight) : null);
    }

    private void ResetAppAttention()
    {
        attentionRetryTimer.Stop();
        attentionWindows.Clear();
        pendingAttention.Clear();
        lastAttentionPulse.Clear();
    }
}
