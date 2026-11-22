using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AITaskbar;

internal sealed partial class TaskbarSettingsWindow
{
    private void BuildBrowserRecentConnection(StackPanel page, RecentBrowser browser, ToggleSwitch toggle)
    {
        var row = SettingRow($"显示 {browser} 最近关闭", "在右键菜单显示最近关闭的网页。", toggle);
        var connection = new TextBlock { Text = "正在检查连接…", FontSize = 12, Opacity = .72, TextWrapping = TextWrapping.Wrap };
        var extensionPath = Path.Combine(AppContext.BaseDirectory, "BrowserCompanions", browser.ToString());
        var install = ActionButton("安装扩展", () => OpenBrowserExtensionSettings(browser), $"安装 {browser} 扩展");
        var copy = ActionButton("复制扩展目录", () => CopyBrowserExtensionPath(browser, extensionPath), $"复制 {browser} 扩展目录");
        var actions = Actions(install, copy);
        actions.Visibility = Visibility.Collapsed;
        var details = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        details.Children.Add(connection);
        details.Children.Add(actions);
        row.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(details, 1);
        Grid.SetColumnSpan(details, 2);
        row.Children.Add(details);
        page.Children.Add(row);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        bool checking = false;
        int consecutiveMisses = 0;
        async Task RefreshAsync()
        {
            if (checking || !row.IsLoaded || !AppWindow.IsVisible) return;
            checking = true;
            try
            {
                bool connected = await BrowserRecentSessions.IsConnectedAsync(browser);
                if (!row.IsLoaded || !AppWindow.IsVisible) return;
                consecutiveMisses = connected ? 0 : Math.Min(consecutiveMisses + 1, 2);
                // A simultaneous menu request can briefly occupy the only pipe instance.
                if (!connected && connection.Text == "已连接" && consecutiveMisses < 2) return;
                connection.Text = connected ? "已连接" : "未连接。请在浏览器扩展页选择「加载已解压的扩展」。";
                actions.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            }
            finally { checking = false; }
        }

        timer.Tick += async (_, _) => await RefreshAsync();
        row.Loaded += async (_, _) =>
        {
            timer.Start();
            await RefreshAsync();
        };
        row.Unloaded += (_, _) => timer.Stop();
    }

    private void CopyBrowserExtensionPath(RecentBrowser browser, string path)
    {
        if (!File.Exists(Path.Combine(path, "manifest.json")))
        {
            SetStatus($"缺少 {browser} 扩展文件。请重新解压完整的 AITaskbar 安装包。");
            return;
        }
        try
        {
            var data = new DataPackage();
            data.SetText(path);
            Clipboard.SetContent(data);
            SetStatus($"已复制 {browser} 扩展目录。");
        }
        catch (COMException) { SetStatus("无法复制目录，请稍后重试。"); }
    }

    private void OpenBrowserExtensionSettings(RecentBrowser browser)
    {
        var (relativePath, url) = browser == RecentBrowser.Chrome
            ? (@"Google\Chrome\Application\chrome.exe", "chrome://extensions/")
            : (@"Microsoft\Edge\Application\msedge.exe", "edge://extensions/");
        Environment.SpecialFolder[] roots =
        [
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData
        ];
        try
        {
            var executable = roots.Select(folder => Environment.GetFolderPath(folder))
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => Path.Combine(path, relativePath))
                .FirstOrDefault(File.Exists);
            if (executable is not null)
            {
                using var process = Process.Start(new ProcessStartInfo(executable, url));
                return;
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
        SetStatus($"无法打开 {browser} 扩展页。请在 {browser} 的地址栏输入 {url}");
    }
}
