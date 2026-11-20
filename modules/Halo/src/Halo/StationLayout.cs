namespace Halo;

internal sealed record StationCell(Guid Id, int Column, int Row);
internal sealed record StationLayout(int Columns, StationCell[] Cells);
internal sealed partial class StationStore
{
    static void ValidateLayout(StationLayout? layout) {
        if (layout == null) return;
        if (layout.Columns is < 1 or > 64 || layout.Cells == null || layout.Cells.Length > MaximumCapacity || layout.Cells.Any(c => c == null || c.Id == Guid.Empty || c.Column < 0 || c.Column >= layout.Columns || c.Row is < 0 or > 4095) || layout.Cells.Select(c => c.Id).Distinct().Count() != layout.Cells.Length || layout.Cells.Select(c => (c.Column,c.Row)).Distinct().Count() != layout.Cells.Length)
            throw new InvalidDataException("网格位置记录无效。");
    }
    static StationLayout? SyncLayout(StationLayout? layout, IReadOnlyList<StationItem> items) {
        if (layout == null) return null;
        var ids = items.Select(i => i.Id).ToHashSet();
        var cells = layout.Cells.Where(c => ids.Contains(c.Id)).ToList(); var placed = cells.Select(c => c.Id).ToHashSet(); var used = cells.Select(c => c.Row * layout.Columns + c.Column).ToHashSet(); int slot = 0;
        foreach (var item in items.Where(i => !placed.Contains(i.Id))) {
            while (used.Contains(slot)) slot++;
            cells.Add(new(item.Id, slot % layout.Columns, slot / layout.Columns)); used.Add(slot);
        }
        return layout with { Cells = cells.ToArray() };
    }
    static StationLayout Packed(IEnumerable<Guid> ids, int columns) => new(columns, ids.Select((id, i) => new StationCell(id, i % columns, i / columns)).ToArray());
    internal bool HasLayout(bool expanded) => (expanded ? data.ExpandedLayout : data.CompactLayout) != null;
    static StationLayout Widen(StationLayout layout, int columns) => layout with { Columns = Math.Max(layout.Columns, Math.Clamp(columns, 1, 64)) };
    internal StationLayout Layout(bool expanded, int columns, bool newest) => SyncLayout((expanded ? data.ExpandedLayout : data.CompactLayout) ?? (expanded ? data.CompactLayout : data.ExpandedLayout), data.Items) is {} saved ? Widen(saved, columns) : Packed(Ordered(newest).Select(i => i.Id), Math.Clamp(columns, 1, 64));
    internal Task EnsureLayout(bool expanded, int columns, bool newest) {
        var layout = Layout(expanded, columns, newest);
        return Change(d => (expanded ? d.ExpandedLayout : d.CompactLayout) != null ? d : expanded ? d with { ExpandedLayout = layout } : d with { CompactLayout = layout });
    }
    internal Task ArrangeGrid(bool expanded, int columns, bool newest) {
        var layout = Packed(Layout(expanded, columns, newest).Cells.OrderBy(c => c.Row).ThenBy(c => c.Column).Select(c => c.Id), columns);
        return Change(d => expanded ? d with { Sort = StationSort.Manual, ExpandedLayout = layout } : d with { Sort = StationSort.Manual, CompactLayout = layout });
    }
    internal Task MoveGrid(Guid[] moving, Guid anchor, int column, int row, bool expanded, int columns, bool newest) {
        var baseline = Ordered(newest).Select(i => i.Id).ToArray();
        return Change(d => {
            var original = d.Sort == StationSort.Manual ? (expanded ? d.ExpandedLayout : d.CompactLayout) ?? (expanded ? d.CompactLayout : d.ExpandedLayout) : null;
            var layout = SyncLayout(original == null ? Packed(baseline, columns) : Widen(original, columns), d.Items)!;
            var updated = MoveLayout(layout, moving, anchor, column, row); ValidateLayout(updated);
            var order = updated.Cells.OrderBy(c => c.Row).ThenBy(c => c.Column).Select(c => c.Id).ToArray();
            return expanded ? d with { Sort = StationSort.Manual, ManualOrder = order, ExpandedLayout = updated } : d with { Sort = StationSort.Manual, ManualOrder = order, CompactLayout = updated };
        });
    }
    internal static StationLayout MoveLayout(StationLayout layout, Guid[] moving, Guid anchor, int column, int row)
    {
        var selected = moving.ToHashSet();
        var origins = layout.Cells.Where(cell => selected.Contains(cell.Id)).ToArray();
        if (moving.Length == 0 || moving.Length != selected.Count || !selected.Contains(anchor) || origins.Length != moving.Length)
            throw new InvalidDataException("拖动项目已变化，请重试。");
        var origin = origins.First(cell => cell.Id == anchor);
        int dx = column - origin.Column, dy = row - origin.Row;
        if (dx == 0 && dy == 0) return layout;
        var moved = origins.Select(cell => cell with { Column = cell.Column + dx, Row = cell.Row + dy }).ToArray();
        if (moved.Any(cell => cell.Column < 0 || cell.Column >= layout.Columns || cell.Row is < 0 or > 4095))
            throw new InvalidDataException("目标位置放不下所选项目。");
        var targets = moved.Select(cell => (cell.Column, cell.Row)).ToHashSet();
        var collisions = layout.Cells.Where(cell => !selected.Contains(cell.Id) && targets.Contains((cell.Column, cell.Row))).ToArray();
        if (moving.Length > 1 && collisions.Length > 0) throw new InvalidDataException("目标位置已有项目，请选择空位。");
        var cells = layout.Cells.Where(cell => !selected.Contains(cell.Id) && !collisions.Any(other => other.Id == cell.Id)).Concat(moved).ToList();
        if (collisions.Length == 1) cells.Add(collisions[0] with { Column = origin.Column, Row = origin.Row });
        return layout with { Cells = cells.ToArray() };
    }
}
