using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

public partial class App
{
    async Task VerifySettingsSort(Action<string, bool> check)
    {
        using var lease = Panel!.Retain();
        var original = Station.Sort(Settings.Data.NewestFirst);
        var next = original == StationSort.Oldest ? StationSort.Newest : StationSort.Oldest;
        string path = Path.Combine(DataDirectory, "station.json");
        string before = await File.ReadAllTextAsync(path);
        OpenSettings();
        var window = Panel.SettingsForVerification;
        var sort = window.SortControlForVerification;
        void Select() => sort.SelectedItem = sort.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, next));
        void Click(string label) => ((IInvokeProvider)new ButtonAutomationPeer(window.FindButton(label)).GetPattern(PatternInterface.Invoke)).Invoke();
        Select();
        check("SettingsSortSelectionIsPending", Station.Sort(Settings.Data.NewestFirst) == original);
        Click("取消");
        check("SettingsSortCancelDoesNotWrite", Station.Sort(Settings.Data.NewestFirst) == original && await File.ReadAllTextAsync(path) == before);
        OpenSettings();
        check("SettingsSortReopensWithSavedChoice", sort.SelectedItem is ComboBoxItem item && Equals(item.Tag, original));
        Select(); Click("保存");
        await window.PendingSave;
        check("SettingsSortSavePersists", Panel.ShowingSettings && Station.Sort(Settings.Data.NewestFirst) == next && new StationStore(path).Sort(Settings.Data.NewestFirst) == next);
        await Station.SetSort(original, Settings.Data.NewestFirst);
        Panel.ReturnFromSettings(); await Task.Delay(120);
    }
}
