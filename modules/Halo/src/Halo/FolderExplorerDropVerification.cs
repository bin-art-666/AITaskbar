using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using InputFixture = AIEnter.AiWindow.DesktopInputFixture;

namespace Halo;

public partial class App
{
    sealed record ExplorerFolderPoint(long Handle, int X, int Y);

    async Task VerifyFolderExplorerDrop(List<string> checks)
    {
        string parent = Path.GetDirectoryName(DataDirectory)!;
        var fixture = JsonSerializer.Deserialize<ExplorerFolderPoint>(File.ReadAllText(Path.Combine(parent, "folder-explorer-point.json")))!;
        const string fileName = "HaloNestedCopy.txt";
        string seed = Directory.CreateDirectory(Path.Combine(DataDirectory, "HaloNestedFolder")).FullName;
        await File.WriteAllTextAsync(Path.Combine(seed, fileName), "Halo nested export");
        if (Station.Items.Count == 0) await Station.ImportCopies([new(StationKind.Folder, seed)]);
        var monitor = Native.Monitors().First(item => item.Primary);
        var work = monitor.Info.Work;
        double scale = monitor.Dpi / 96d;
        Settings.Save(Settings.Data with { Detached = true, Pinned = true, DetachedHideOnBlur = false,
            ExpandedWidth = 450, ExpandedHeight = 420, ExpandedX = work.Right - (int)(470 * scale),
            ExpandedY = work.Top + (int)(80 * scale) });
        var anchor = new Button { Content = "Halo" };
        var owner = AIEnter.App.FixtureWindow(anchor, new(work.Right - 210, work.Bottom - 68, 170, 58));
        try
        {
            SetFixtureLayer((nint)fixture.Handle, -1, 0, 0, 0, 0, 0x0013);
            AttachTaskbar(anchor);
            Panel = CreatePanel();
            await Task.Delay(250);
            Panel.Open();
            await Panel.VerifyFolderExplorerDrop(Station.Items.Single(), (nint)fixture.Handle, InputCoordinate(fixture.X, fixture.Y),
                fileName, Path.Combine(parent, "HaloExplorerOutput", fileName), checks);
        }
        finally
        {
            SetFixtureLayer((nint)fixture.Handle, -2, 0, 0, 0, 0, 0x0013);
            Panel?.Destroy(); Panel = null; owner.Close();
        }
    }
}

internal sealed partial class StationWindow
{
    internal async Task VerifyFolderExplorerDrop(StationItem folder, nint explorer, AIEnter.Native.Point target,
        string fileName, string exportedPath, List<string> checks)
    {
        await Task.Delay(400);
        await OpenFolderBrowser(folder);
        await Task.Delay(350);
        var grid = folderBrowser!.GridForWindowLayout;
        grid.UpdateLayout();
        var card = grid.Items.OfType<GridViewItem>().Single(item => item.IsLoaded && item.ActualWidth > 10);
        var center = card.TransformToVisual(root).TransformPoint(new(card.ActualWidth / 2, card.ActualHeight / 2));
        var bounds = CurrentBounds();
        var source = InputPoint(bounds.X + (int)Math.Round(center.X * WindowLayoutScale),
            bounds.Y + (int)Math.Round(center.Y * WindowLayoutScale));
        var sourceWindow = At(source.X, source.Y);
        var targetWindow = At(target.X, target.Y);
        if (sourceWindow != InteractionWindow || targetWindow != explorer)
            throw new InvalidOperationException($"Source {source.X},{source.Y}: {sourceWindow}/{InteractionWindow}; Explorer {target.X},{target.Y}: {targetWindow}/{explorer}.");
        await Task.Run(() => InputFixture.Drag(source, target, cancel: true, sendNativeMovement: true));
        await Task.Delay(700);
        if (!File.Exists(exportedPath)) checks.Add("NestedExplorerExportCancelledBySimulatedEscape");
        else File.Move(exportedPath, Path.ChangeExtension(exportedPath, ".escape.txt"));
        await Task.Run(() => InputFixture.Drag(source, target, sendNativeMovement: true));
        for (int retry = 0; retry < 30 && !File.Exists(exportedPath); retry++) await Task.Delay(100);
        string stored = Path.Combine(folder.Value, fileName);
        if (!File.Exists(exportedPath) || !File.Exists(stored) || File.ReadAllText(exportedPath) != File.ReadAllText(stored))
            throw new InvalidOperationException("Explorer did not receive the file from the opened Halo folder.");
        checks.Add("OpenedHaloFolderExportsFileToExplorer");
    }
}
