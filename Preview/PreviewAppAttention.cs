using System.Diagnostics;

namespace AITaskbar;

internal sealed partial class PreviewApplicationStrip
{
    private static readonly TimeSpan AttentionCooldown = TimeSpan.FromSeconds(3);
    private readonly HashSet<(nint Handle, uint ProcessId)> attentionWindows = [];
    private readonly Dictionary<string, long> lastAttentionPulse = new(StringComparer.OrdinalIgnoreCase);

    private bool CanPulseAttention => iconLayout.AppAttention == AppAttentionMode.PulseAndDot &&
        system.AnimationsEnabled && !accessibility.HighContrast;

    private bool HasAppAttention(AppGroup group) => attentionWindows.Count > 0 &&
        group.Windows.Any(window => attentionWindows.Contains((window.Handle, window.ProcessId)));

    internal void UpdateAppAttention(nint handle, bool flashing)
    {
        if (iconLayout.AppAttention == AppAttentionMode.Off || !items.IsLoaded) return;
        if (!flashing)
        {
            if (ClearWindowAttention(handle)) UpdateIndicators();
            return;
        }

        nint root = RunningWindows.RootOwnerOrSelf(handle);
        var group = FindAttentionGroup(handle) ?? FindAttentionGroup(root);
        if (group is null)
        {
            Refresh(preserveRangeStart: true);
            group = FindAttentionGroup(handle) ?? FindAttentionGroup(root);
        }
        var window = group?.Windows.FirstOrDefault(candidate => candidate.Handle == handle || candidate.Handle == root);
        if (group is null || window is null || !RunningWindows.IsCurrent(window) ||
            RunningWindows.MatchesForeground(window, RunningWindows.RawForeground())) return;

        bool alreadyPending = HasAppAttention(group);
        if (!attentionWindows.Add((window.Handle, window.ProcessId))) return;
        UpdateIndicators();
        if (alreadyPending || !CanPulseAttention || !HasAppAttention(group) ||
            lastAttentionPulse.TryGetValue(group.Key, out long last) && Stopwatch.GetElapsedTime(last) < AttentionCooldown) return;

        var view = Shown.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item.Group.Key, group.Key));
        if (view is null) return;
        lastAttentionPulse[group.Key] = Stopwatch.GetTimestamp();
        attentionBadges.Pulse(view.IconPlate);
    }

    private AppGroup? FindAttentionGroup(nint handle) => groups.FirstOrDefault(group =>
        group.Windows.Any(window => window.Handle == handle));

    private bool ClearWindowAttention(nint handle)
    {
        if (attentionWindows.Count == 0 || handle == 0) return false;
        nint root = RunningWindows.RootOwnerOrSelf(handle);
        return attentionWindows.RemoveWhere(window => window.Handle == handle || window.Handle == root) > 0;
    }

    private void PruneAppAttention()
    {
        if (attentionWindows.Count > 0)
        {
            var live = groups.SelectMany(group => group.Windows).Select(window => (window.Handle, window.ProcessId)).ToHashSet();
            attentionWindows.RemoveWhere(window => !live.Contains(window));
        }
        foreach (string key in lastAttentionPulse.Where(entry => Stopwatch.GetElapsedTime(entry.Value) >= AttentionCooldown)
            .Select(entry => entry.Key).ToArray()) lastAttentionPulse.Remove(key);
    }
}
