using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class SettingsPage
{
    internal bool HandleShortcut(bool saveChanges)
    {
        if (fileMenuSample.IsDropDownOpen || sortMode.IsDropDownOpen || dragMode.IsDropDownOpen
            || allButtons.Any(button => button.Flyout?.IsOpen == true)) return false;
        if (!PendingOperation.IsCompleted) return true;
        if (saveChanges) StartSave();
        else owner.ReturnFromSettings();
        return true;
    }

    static void BuildShortcutSettings(StackPanel body)
    {
        var content = new StackPanel { Spacing = 6 };
        AddGroup("主网格和文件夹", [
            ("方向键 / Shift+方向键", "移动选择 / 连选"),
            ("Enter / 空格", "打开选中项 / 预览文件或进入文件夹"),
            ("F2", "重命名选中项"),
            ("Ctrl+A / Ctrl+C / Ctrl+V", "全选 / 复制 / 粘贴"),
            ("Ctrl+X → Ctrl+V", "在 Halo 文件夹间移动副本"),
            ("Delete", "移到 Halo 回收站"),
            ("Ctrl+Shift+N", "新建文件夹"),
            ("Ctrl+F → ↓ → Enter", "搜索、进入结果并打开选中项"),
            ("Ctrl+Z", "撤销移动、移除或当前文件夹内的合并与解散"),
            ("Shift+F10 / 菜单键", "打开右键菜单"),
            ("Esc / Alt+←", "退出当前交互 / 返回上一级文件夹"),
        ]);
        AddGroup("设置", [("Ctrl+S", "保存并留在设置页"), ("Esc", "取消未保存的修改并返回")]);
        body.Children.Add(new Expander {
            Header = "键盘快捷键", Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        });

        void AddGroup(string title, (string Keys, string Action)[] shortcuts)
        {
            content.Children.Add(Section(title));
            foreach (var (keys, action) in shortcuts)
                content.Children.Add(new TextBlock { Text = $"{keys}   {action}", TextWrapping = TextWrapping.Wrap });
        }
    }
}
