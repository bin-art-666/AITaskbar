using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal enum FileMenuAction
{
    Separator, Open, Preview, Copy, Rename, Reveal, CopyPath, OwnReference, Trash, Move,
}

internal sealed record FileMenuLayout(FileMenuAction[] Primary, FileMenuAction[] More)
{
    internal static FileMenuLayout Default { get; } = new(
        [FileMenuAction.Open, FileMenuAction.Preview, FileMenuAction.Copy, FileMenuAction.Separator, FileMenuAction.Rename],
        [FileMenuAction.Reveal, FileMenuAction.CopyPath, FileMenuAction.OwnReference, FileMenuAction.Separator, FileMenuAction.Trash, FileMenuAction.Move]);

    internal FileMenuLayout WithMove() => Primary == null || More == null || Primary.Contains(FileMenuAction.Move) || More.Contains(FileMenuAction.Move)
        ? this : this with { More = [.. More, FileMenuAction.Move] };

    internal void Validate()
    {
        if (Primary == null || More == null || Primary.Length > 18 || More.Length > 18
            || Primary.Count(action => action == FileMenuAction.Separator) > 9
            || More.Count(action => action == FileMenuAction.Separator) > 9
            || !Primary.Concat(More).Where(action => action != FileMenuAction.Separator).Order()
                .SequenceEqual(Enum.GetValues<FileMenuAction>().Skip(1)))
            throw new InvalidDataException("文件右键菜单布局无效。");
    }

    public bool Equals(FileMenuLayout? other) => ReferenceEquals(this, other)
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
    internal static string FileMenuLabel(FileMenuAction action) => action switch {
        FileMenuAction.Open => "打开文件",
        FileMenuAction.Preview => "快速预览",
        FileMenuAction.Copy => "复制文件",
        FileMenuAction.Rename => "重命名…",
        FileMenuAction.Reveal => "在资源管理器中定位",
        FileMenuAction.CopyPath => "复制路径",
        FileMenuAction.OwnReference => "保存为 Halo 副本（保留位置）",
        FileMenuAction.Trash => "移到 Halo 回收站",
        FileMenuAction.Move => "移动到…",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    internal static void PopulateFileMenu(MenuFlyout menu, FileMenuLayout layout, StationItem[] items, Func<Func<Task>, Task> run,
        ItemMenuCommands commands, Func<StationItem, Task> rename, Func<Task> trash, bool writable, Func<Task>? ownReference = null)
    {
        var first = items[0];
        bool single = items.Length == 1;
        bool references = items.All(item => !item.Owned);
        bool shortcut = single && RichContent.IsShortcut(first.Value);
        MenuHeading(menu, single ? references ? "原文件引用操作" : shortcut ? "快捷方式操作" : "文件操作" : $"已选 {items.Length} 项");
        AppendMenuLayers(menu, layout.Primary, layout.More, FileMenuAction.Separator, Command);

        MenuFlyoutItem? Command(FileMenuAction kind)
        {
            string label = FileMenuLabel(kind);
            var (action, name, enabled) = kind switch {
                FileMenuAction.Open when single => (new Func<Task>(() => commands.Open(first)),
                    references ? "打开引用…" : shortcut ? "打开快捷方式" : label, true),
                FileMenuAction.Preview when single => (() => commands.Preview(first),
                    shortcut ? "查看快捷方式信息  空格" : HasFileContentPreview(first.Value) ? "快速预览  空格" : "查看文件信息  空格", true),
                FileMenuAction.Copy => (commands.Copy, single ? label : $"复制所选 {items.Length} 项", true),
                FileMenuAction.Rename when single && first.Owned => (() => rename(first), label, writable),
                FileMenuAction.Reveal when single => (() => commands.Reveal(first), label, true),
                FileMenuAction.CopyPath => (commands.CopyPaths, references ? "复制原路径" : label, true),
                FileMenuAction.OwnReference when single && references && ownReference != null => (ownReference, label, writable),
                FileMenuAction.Trash when !references || ownReference != null => (trash,
                    references ? single ? "移除引用" : $"移除 {items.Length} 个引用" : single ? label : $"将所选 {items.Length} 项移到回收站", writable),
                FileMenuAction.Move when items.All(item => item.Owned) && commands.Move != null => (commands.Move, label, writable),
                _ => ((Func<Task>?)null, label, false),
            };
            if (action == null) return null;
            return MenuCommand(name, run, action, enabled, kind switch {
                FileMenuAction.Open => "Enter",
                FileMenuAction.Copy => "Ctrl+C",
                FileMenuAction.Rename => "F2",
                FileMenuAction.Trash => "Delete",
                _ => null,
            });
        }
    }
}
