using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Halo;

internal sealed partial class StationWindow
{
    sealed record SearchHit(StationItem Item, StationItem? RootFolder = null, string? Directory = null, string? ImageMatch = null)
    {
        internal string Location => RootFolder == null ? "中转站"
            : RootFolder.Value.Equals(Directory, StringComparison.OrdinalIgnoreCase) ? DesktopName(RootFolder)
            : DesktopName(RootFolder) + " › " + Path.GetRelativePath(RootFolder.Value, Directory!).Replace("\\", " › ");
    }

    const int NestedSearchLimit = 200;
    readonly ListView searchResults = new() { SelectionMode = ListViewSelectionMode.None, Padding = new(10, 0, 10, 0) };
    readonly TextBlock searchStatus = new() { Margin = new(12, 4, 12, 8), TextWrapping = TextWrapping.Wrap, Opacity = .7 };
    readonly Grid searchView = new() { Margin = new(0, 44, 0, 0), Visibility = Visibility.Collapsed };
    CancellationTokenSource? searchRequest;
    readonly CheckBox includeImageText = new() { Content = "搜索图片文字", Visibility = Visibility.Collapsed };
    readonly ImageTextSearch imageText = new();

    void InitializeSearchResults()
    {
        var searchBar = new Grid { VerticalAlignment = VerticalAlignment.Top };
        searchBar.ColumnDefinitions.Add(new());
        searchBar.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        includeImageText.Margin = new(0, 4, 12, 4);
        includeImageText.MinWidth = 0;
        ToolTipService.SetToolTip(includeImageText, "使用 Windows 在本机识别 Halo 文件库中的图片");
        Grid.SetColumn(includeImageText, 1);
        searchBar.Children.Add(desktopSearch);
        searchBar.Children.Add(includeImageText);
        Grid.SetRow(searchBar, 1);
        root.Children.Add(searchBar);
        includeImageText.Checked += (_, _) => _ = RefreshSearch(debounce: false);
        includeImageText.Unchecked += (_, _) => _ = RefreshSearch(debounce: false);
        AutomationProperties.SetName(searchResults, "搜索结果");
        AutomationProperties.SetLiveSetting(searchStatus, AutomationLiveSetting.Polite);
        searchView.RowDefinitions.Add(new());
        searchView.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(searchStatus, 1);
        searchView.Children.Add(searchResults);
        searchView.Children.Add(searchStatus);
        Grid.SetRow(searchView, 1);
        root.Children.Insert(root.Children.IndexOf(statusHost), searchView);
    }

    void UpdateStationContent()
    {
        bool show = !ShowingSettings && !App.Station.LibraryOffline;
        files.Visibility = show && !Searching ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = show && !Searching && files.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchView.Visibility = show && Searching && desktopSearch.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        includeImageText.Visibility = show && desktopSearch.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectionDetails();
    }

    async Task RefreshSearch(bool debounce = true)
    {
        searchRequest?.Cancel();
        searchResults.Items.Clear();
        UpdateStationContent();
        if (searchView.Visibility != Visibility.Visible) return;

        using var request = new CancellationTokenSource();
        searchRequest = request;
        string query = desktopSearch.Text.Trim();
        var entries = App.Station.Ordered(App.Settings.Data.NewestFirst).ToArray();
        foreach (var item in entries.Where(item => SearchMatches(item, query))) AddSearchResult(new(item));
        searchStatus.Text = "正在搜索文件夹…";
        try {
            if (debounce) await Task.Delay(180, request.Token);
            var (nested, skipped) = await Task.Run(() => FindNestedMatches(App.Station, entries, query, request.Token), request.Token);
            if (request.IsCancellationRequested) return;
            foreach (var hit in nested.Take(NestedSearchLimit)) AddSearchResult(hit);
            string? imageStatus = null;
            if (includeImageText.IsChecked == true) {
                searchStatus.Text = "正在识别图片文字…";
                var displayed = searchResults.Items.Cast<ListViewItem>().Select(row => ((SearchHit)row.Tag).Item.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                try {
                    var (matches, unreadable) = await Task.Run(() => FindImageTextMatches(entries, displayed, query, request.Token), request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    foreach (var hit in matches.Take(NestedSearchLimit)) AddSearchResult(hit);
                    if (matches.Count > NestedSearchLimit) imageStatus = "仅显示前 200 个图片文字匹配项";
                    else if (unreadable) imageStatus = "部分图片无法识别";
                } catch (Exception error) when (error is System.Runtime.InteropServices.COMException or IOException or InvalidOperationException) {
                    imageStatus = "本机图片文字识别暂不可用";
                }
            }
            request.Token.ThrowIfCancellationRequested();
            searchStatus.Text = nested.Count > NestedSearchLimit ? "仅显示前 200 个文件夹内的匹配项，请缩小搜索范围。"
                : searchResults.Items.Count == 0 ? "没有匹配的项目" : $"找到 {searchResults.Items.Count} 项";
            if (skipped) searchStatus.Text += "。部分目录暂不可访问。";
            if (imageStatus != null) searchStatus.Text += "。" + imageStatus + "。";
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        finally { if (searchRequest == request) searchRequest = null; }
    }

    static (List<SearchHit> Hits, bool Skipped) FindNestedMatches(StationStore store, StationItem[] entries, string query, CancellationToken token)
    {
        var hits = new List<SearchHit>();
        bool skipped = false;
        foreach (var node in WalkSearchFolders(store, entries, token, () => skipped = true)) {
            if (node.Entry.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                hits.Add(new(StationStore.FolderEntry(node.Root, node.Entry), node.Root, node.Directory));
            if (hits.Count > NestedSearchLimit) break;
        }
        return (hits, skipped);
    }

    static IEnumerable<(StationItem Root, string Directory, FileSystemInfo Entry)> WalkSearchFolders(
        StationStore store, StationItem[] entries, CancellationToken token, Action? onSkipped = null)
    {
        foreach (var folder in entries.Where(item => item.Owned && item.Kind == StationKind.Folder)) {
            var pending = new Stack<string>();
            pending.Push(folder.Value);
            while (pending.TryPop(out var directory)) {
                token.ThrowIfCancellationRequested();
                using var items = store.EnumerateFolderContents(folder, directory, token).GetEnumerator();
                while (true) {
                    try { if (!items.MoveNext()) break; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { onSkipped?.Invoke(); break; }
                    var entry = items.Current;
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                    yield return (folder, directory, entry);
                }
            }
        }
    }

    async Task<(List<SearchHit> Matches, bool Unreadable)> FindImageTextMatches(StationItem[] entries, HashSet<string> displayed, string query, CancellationToken token)
    {
        var engines = ImageTextSearch.Engines();
        if (engines.Length == 0) throw new InvalidOperationException("Windows 未安装 OCR 语言包。");
        string languages = string.Join(',', engines.Select(engine => engine.RecognizerLanguage.LanguageTag));
        var top = entries.Where(item => item.Owned && item.Kind == StationKind.File).Select(item => new SearchHit(item));
        var nested = WalkSearchFolders(App.Station, entries, token).Where(node => node.Entry is FileInfo && ImageTextSearch.IsImage(node.Entry.Name))
            .Select(node => new SearchHit(StationStore.FolderEntry(node.Root, node.Entry), node.Root, node.Directory));
        var matches = new List<SearchHit>();
        bool unreadable = false;
        foreach (var hit in top.Concat(nested)) {
            token.ThrowIfCancellationRequested();
            if (displayed.Contains(hit.Item.Value) || !ImageTextSearch.IsImage(hit.Item.Value)) continue;
            try {
                if (!App.Station.IsManagedSearchImage(hit.Item.Value)) continue;
                string recognized = await imageText.Read(hit.Item.Value, engines, languages, token);
                if (ImageTextSearch.Match(recognized, query) is { } text) matches.Add(hit with { ImageMatch = text });
                if (matches.Count > NestedSearchLimit) break;
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { unreadable = true; }
        }
        return (matches, unreadable);
    }

    void AddSearchResult(SearchHit hit)
    {
        string name = DesktopName(hit.Item);
        var title = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis };
        var path = new TextBlock { Text = hit.Location, FontSize = 12, Opacity = .7, TextTrimming = TextTrimming.CharacterEllipsis };
        var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(title);
        label.Children.Add(path);
        if (hit.ImageMatch is { } imageMatch)
            label.Children.Add(new TextBlock { Text = "图片文字 · " + imageMatch, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = .8 });
        var locate = new Button { Content = "定位", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(locate, $"在 Halo 中定位 {name}，{hit.Location}");
        PanelTheme.StyleTextAction(locate);
        locate.Click += async (_, _) => await Run(() => LocateSearchResult(hit));
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(locate, 1);
        row.Children.Add(label);
        row.Children.Add(locate);
        var result = new ListViewItem { Content = row, Tag = hit, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(result, $"{name}，{hit.Location}");
        ToolTipService.SetToolTip(label, name + "\n" + hit.Location);
        result.DoubleTapped += async (_, e) => {
            if (!InsideElement(locate, e.OriginalSource as DependencyObject))
                await Run(() => hit.RootFolder == null ? OpenSearchResult(hit) : LocateSearchResult(hit));
        };
        result.KeyDown += async (_, e) => {
            if (InsideElement(locate, e.OriginalSource as DependencyObject)) return;
            if (e.Key == Windows.System.VirtualKey.Space && hit.RootFolder == null) {
                e.Handled = true;
                await Run(() => hit.Item.Kind == StationKind.Folder ? OpenSearchResult(hit) : PreviewItem(hit.Item));
                return;
            }
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            await Run(() => LocateSearchResult(hit));
        };
        if (hit.RootFolder == null) result.ContextRequested += (_, e) => {
            var current = App.Station.Items.FirstOrDefault(item => item.Id == hit.Item.Id);
            if (current == null) return;
            var menu = BuildItemMenu([current]);
            menu.Items.Insert(2, MenuCommand("在 Halo 中定位", Run, () => LocateSearchResult(hit), true));
            if (e.TryGetPosition(result, out var point)) menu.ShowAt(result, new FlyoutShowOptions { Position = point });
            else menu.ShowAt(result);
            e.Handled = true;
        };
        searchResults.Items.Add(result);
    }

    async Task OpenSearchResult(SearchHit hit)
    {
        var current = App.Station.Items.FirstOrDefault(item => item.Id == hit.Item.Id)
            ?? throw new IOException("项目已移动或移除，请重新搜索。");
        await OpenItem(current);
    }

    bool FocusDesktopSearchResult()
    {
        if (searchResults.Items.FirstOrDefault() is not ListViewItem first) return false;
        searchResults.ScrollIntoView(first);
        searchResults.UpdateLayout();
        return first.Focus(FocusState.Keyboard);
    }

    async Task LocateSearchResult(SearchHit hit)
    {
        if (!Searching || ShowingSettings || App.Station.LibraryOffline || folderBrowser != null || changingMode || resizing) return;
        var top = App.Station.Items.FirstOrDefault(item => item.Id == (hit.RootFolder ?? hit.Item).Id);
        if (top == null || hit.RootFolder != null && !top.Value.Equals(hit.RootFolder.Value, StringComparison.OrdinalIgnoreCase))
            throw new IOException("项目已移动或移除，请重新搜索。");
        if (hit.Directory is { } directory) {
            string query = desktopSearch.Text;
            var current = await Task.Run(() => App.Station.ReadFolderContents(top, directory, default));
            if (!current.Any(item => item.Id == hit.Item.Id)) throw new IOException("项目已移动或移除，请重新搜索。");
            if (!string.Equals(query, desktopSearch.Text, StringComparison.Ordinal) || !IsOpen || ShowingSettings || App.Station.LibraryOffline) return;
        }
        HideSearch();
        files.UpdateLayout();
        SelectLocatedCard(files, top.Id, ref selectionAnchor);
        if (hit.Directory != null) {
            await OpenFolderBrowser(top, hit.Directory, hit.Item.Id);
        }
    }

    internal static bool SelectLocatedCard(GridView grid, Guid id, ref Guid? anchor)
    {
        if (grid.Items.Cast<GridViewItem>().FirstOrDefault(card => ((StationItem)card.Tag).Id == id) is not { } match) return false;
        SelectGridCard(grid, match, false, false, ref anchor);
        grid.ScrollIntoView(match);
        grid.UpdateLayout();
        FocusGridSelection(grid);
        return true;
    }
}
