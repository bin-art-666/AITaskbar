using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

[Flags]
internal enum FolderMenuAction
{
    Separator = 0,
    Open = 1, Preview = 2, Copy = 4, Explorer = 8, CopyPath = 16,
    Rename = 32, Color = 64, Dissolve = 128, Trash = 256,
    Move = 512, All = 1023,
    Default = Open | Copy | Explorer | Rename | Color,
}

internal sealed record FolderMenuLayout(FolderMenuAction[] Primary, FolderMenuAction[] More)
{
    internal static FolderMenuLayout FromLegacy(FolderMenuAction primary) => new(
        StationWindow.FolderMenuOrder.Select(item => item.Action).Where(action => primary.HasFlag(action)).ToArray(),
        StationWindow.FolderMenuOrder.Select(item => item.Action).Where(action => !primary.HasFlag(action)).ToArray());

    internal FolderMenuAction PrimaryActions => Primary.Aggregate(FolderMenuAction.Separator, (actions, action) => actions | action);

    internal FolderMenuLayout WithMove() => Primary == null || More == null || Primary.Contains(FolderMenuAction.Move) || More.Contains(FolderMenuAction.Move)
        ? this : this with { More = [.. More, FolderMenuAction.Move] };

    internal void Validate()
    {
        if (Primary == null || More == null || Primary.Length > 19 || More.Length > 19
            || Primary.Count(action => action == FolderMenuAction.Separator) > 9
            || More.Count(action => action == FolderMenuAction.Separator) > 9
            || !Primary.Concat(More).Where(action => action != FolderMenuAction.Separator).Order()
                .SequenceEqual(StationWindow.FolderMenuOrder.Select(item => item.Action).Order()))
            throw new InvalidDataException("文件夹右键菜单布局无效。");
    }

    public bool Equals(FolderMenuLayout? other) => ReferenceEquals(this, other)
        || other != null && Primary.SequenceEqual(other.Primary) && More.SequenceEqual(other.More);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Primary.Length);
        foreach (var action in Primary.Concat(More)) hash.Add(action);
        return hash.ToHashCode();
    }
}

internal sealed partial class StationWindow
{
    internal const string MoreFolderActions = "…";
    internal static readonly (FolderMenuAction Action, string Label)[] FolderMenuOrder = [
        (FolderMenuAction.Open, "打开文件夹"),
        (FolderMenuAction.Preview, "快速预览"),
        (FolderMenuAction.Copy, "复制文件夹"),
        (FolderMenuAction.Explorer, "在资源管理器中打开"),
        (FolderMenuAction.CopyPath, "复制路径"),
        (FolderMenuAction.Rename, "重命名…"),
        (FolderMenuAction.Color, "设置文件夹颜色"),
        (FolderMenuAction.Dissolve, "解散文件夹"),
        (FolderMenuAction.Trash, "移到 Halo 回收站"),
        (FolderMenuAction.Move, "移动到…"),
    ];

    internal sealed record FolderMenuEdits(Func<StationItem, Task> Rename, Func<StationItem, Task> Color,
        Func<StationItem, Task> Dissolve, Func<Task> Trash, Func<Task>? OwnReference = null);

    internal static string? FolderMenuShortcut(FolderMenuAction action) => action switch {
        FolderMenuAction.Open => "Enter / 空格",
        FolderMenuAction.Copy => "Ctrl+C",
        FolderMenuAction.Rename => "F2",
        FolderMenuAction.Trash => "Delete",
        _ => null,
    };

    internal static void PopulateFolderMenu(MenuFlyout menu, StationItem[] items, Func<Func<Task>, Task> run,
        ItemMenuCommands commands, FolderMenuEdits edits, bool writable, bool colorWritable,
        bool allowReferenceRemoval = false)
    {
        bool single = items.Length == 1, references = items.All(item => !item.Owned);
        var first = items[0];
        MenuHeading(menu, single ? references ? "原文件引用操作" : "文件夹操作" : $"已选 {items.Length} 项");
        MenuFlyoutItem? Command(FolderMenuAction kind)
        {
            string label = FolderMenuOrder.Single(entry => entry.Action == kind).Label;
            var (action, name, enabled) = kind switch {
                FolderMenuAction.Open when single => (new Func<Task>(() => commands.Open(first)), references ? "打开引用…" : label, true),
                FolderMenuAction.Preview when single => (() => commands.Preview(first), label, true),
                FolderMenuAction.Copy => (commands.Copy, single ? label : $"复制所选 {items.Length} 项", true),
                FolderMenuAction.Explorer when single => (() => commands.OpenInExplorer(first), label, true),
                FolderMenuAction.CopyPath => (commands.CopyPaths, references ? "复制原路径" : label, true),
                FolderMenuAction.Rename when single && first.Owned => (() => edits.Rename(first), label, writable),
                FolderMenuAction.Color when single => (() => edits.Color(first), label, colorWritable),
                FolderMenuAction.Dissolve when single && StationStore.CanDissolveFolder(first) => (() => edits.Dissolve(first), label, writable),
                FolderMenuAction.Trash when !references || allowReferenceRemoval => (edits.Trash,
                    references ? single ? "移除引用" : $"移除 {items.Length} 个引用" : single ? label : $"将所选 {items.Length} 项移到回收站", writable),
                FolderMenuAction.Move when items.All(item => item.Owned) && commands.Move != null => (commands.Move, label, writable),
                _ => ((Func<Task>?)null, label, false),
            };
            return action == null ? null : MenuCommand(name, run, action, enabled, FolderMenuShortcut(kind));
        }
        var extra = single && references && edits.OwnReference is { } own
            ? MenuCommand("保存为 Halo 副本（保留位置）", run, own, writable) : null;
        AppendFolderMenu(menu, App.Settings.Data.EffectiveFolderMenu, Command, extra);
    }

    internal static void AppendFolderMenu(MenuFlyout menu, FolderMenuLayout layout, Func<FolderMenuAction, MenuFlyoutItem?> command,
        MenuFlyoutItem? extra = null) =>
        AppendMenuLayers(menu, layout.Primary, layout.More, FolderMenuAction.Separator, command, extra);

    internal static void AppendMenuLayers<T>(MenuFlyout menu, T[] primary, T[] overflow, T separatorMarker,
        Func<T, MenuFlyoutItem?> command, MenuFlyoutItem? extra = null) where T : struct, Enum
    {
        var more = new MenuFlyoutSubItem { Text = MoreFolderActions };
        AutomationProperties.SetName(more, "更多操作");
        AppendGroupRows(menu.Items, primary);
        AppendGroupRows(more.Items, overflow);
        if (extra != null) more.Items.Add(extra);
        if (more.Items.Count > 0) menu.Items.Add(more);

        void AppendGroupRows(IList<MenuFlyoutItemBase> target, T[] rows)
        {
            bool hasCommand = false, separator = false;
            foreach (var row in rows) {
                if (EqualityComparer<T>.Default.Equals(row, separatorMarker)) { separator = hasCommand; continue; }
                if (command(row) is not { } item) continue;
                if (separator) target.Add(new MenuFlyoutSeparator());
                target.Add(item);
                hasCommand = true;
                separator = false;
            }
        }
    }
}
