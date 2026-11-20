namespace Halo;

internal enum StationSort { Manual, Newest, Oldest }
internal sealed partial class StationStore
{
    internal StationSort Sort(bool newest) => data.Sort ?? (newest ? StationSort.Newest : StationSort.Oldest);
    static StationItem[] ManualItems(StationDocument document)
    {
        var positions = (document.ManualOrder ?? []).Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        return document.Items.OrderBy(i => positions.GetValueOrDefault(i.Id, int.MaxValue)).ToArray();
    }
    internal StationItem[] Ordered(bool newest) => Sort(newest) switch {
        StationSort.Manual => ManualItems(data),
        StationSort.Newest => data.Items.OrderByDescending(i => i.Created).ToArray(),
        _ => data.Items.OrderBy(i => i.Created).ToArray()
    };
    internal Task SetSort(StationSort sort, bool newest) {
        var baseline = Ordered(newest).Select(i => i.Id).ToArray();
        return Change(d => d with { Sort = sort, ManualOrder = sort == StationSort.Manual && d.ManualOrder == null ? baseline : d.ManualOrder });
    }
    internal Task Reorder(Guid[] moving, Guid? before, Guid[] baseline) => Change(d => {
        var live = d.Items.Select(i => i.Id).ToHashSet(); var selected = moving.ToHashSet();
        if (moving.Length == 0 || selected.Count != moving.Length || moving.Any(id => !live.Contains(id)) || before is {} target && (!live.Contains(target) || selected.Contains(target)))
            throw new InvalidDataException("拖动项目已变化，请重试。");
        var current = (d.Sort == StationSort.Manual ? ManualItems(d).Select(i => i.Id) : baseline).Where(live.Contains).Distinct().ToList();
        current.AddRange(d.Items.Select(i => i.Id).Where(id => !current.Contains(id)));
        var block = current.Where(selected.Contains).ToArray(); current.RemoveAll(selected.Contains);
        int index = before is {} anchor ? current.IndexOf(anchor) : current.Count;
        current.InsertRange(index, block);
        return d with { Sort = StationSort.Manual, ManualOrder = current.ToArray() };
    });
}
