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

    private bool CanPulseAttention => iconLayout.AppAttention == AppAttentionMode.PulseAndDot &&
        system.AnimationsEnabled && !accessibility.HighContrast;

    private bool HasAppAttention(AppGroup group) => attentionWindows.Any(entry =>
        AppKeys.Equals(entry.Value.GroupKey, group.Key) || group.Windows.Any(window =>
            window.Handle == entry.Key.Handle && window.ProcessId == entry.Key.ProcessId));

    internal void UpdateAppAttention(nint handle, bool flashing)
    {
        if (iconLayout.AppAttention == AppAttentionMode.Off) return;
        if (!flashing)
        {
            if (ClearWindowAttention(handle)) UpdateIndicators();
            return;
        }

        var source = new AttentionWindow(handle, RunningWindows.ProcessIdForWindow(handle));
        if (source.ProcessId == 0 || source.ProcessId == Environment.ProcessId ||
            pendingAttention.ContainsKey(source) || attentionWindows.ContainsKey(source)) return;

        if (items.IsLoaded && TryApplyAttention(source, null)) return;
        var (executable, processAppId) = RunningWindows.DescribeProcess(source.ProcessId);
        string? appId = RunningWindows.IsFrameHost(executable) ? RunningWindows.WindowAppId(handle) : processAppId;
        pendingAttention.Add(source, new(Stopwatch.GetTimestamp(), executable, appId));
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

        nint foregroundHandle = RunningWindows.RawForeground();
        if (IsAttentionWindowForeground(source, foregroundHandle) || IsAttentionWindowForeground(target, foregroundHandle)) return true;

        bool alreadyPending = HasAppAttention(group);
        if (!attentionWindows.TryAdd(target, new(group.Key, matchApplication))) return true;
        UpdateIndicators();
        if (!alreadyPending && CanPulseAttention && HasAppAttention(group) &&
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
            if (Stopwatch.GetElapsedTime(pending.ReceivedAt) >= AttentionRetryLimit || !source.IsAlive ||
                items.IsLoaded && TryApplyAttention(source, pending)) pendingAttention.Remove(source);
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
            pendingAttention.Remove(source);
        var cleared = attentionWindows.Where(entry => IsAttentionWindowForeground(entry.Key, handle) ||
            entry.Value.MatchApplicationOnActivation && FindAttentionGroup(entry.Key) is null &&
            FindApp(groups, entry.Value.GroupKey) is { } group &&
            IsAttentionGroupForeground(group, handle)).Select(entry => entry.Key).ToArray();
        foreach (var window in cleared) attentionWindows.Remove(window);
        return cleared.Length > 0;
    }

    private void PruneAppAttention()
    {
        foreach (var window in attentionWindows.Keys.Where(window => !window.IsAlive).ToArray())
            attentionWindows.Remove(window);
        foreach (string key in lastAttentionPulse.Where(entry => Stopwatch.GetElapsedTime(entry.Value) >= AttentionCooldown)
            .Select(entry => entry.Key).ToArray()) lastAttentionPulse.Remove(key);
    }

    private void ResetAppAttention()
    {
        attentionRetryTimer.Stop();
        attentionWindows.Clear();
        pendingAttention.Clear();
        lastAttentionPulse.Clear();
    }
}
