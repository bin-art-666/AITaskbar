using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using InputFixture = AIEnter.AiWindow.DesktopInputFixture;

namespace Halo;

public partial class App
{
    sealed record BrowserPoints(long Handle, int TextX, int TextY, int LinkX, int LinkY, int ImageX, int ImageY);

    async Task VerifyBrowserDrops(List<string> checks)
    {
        var path = Path.Combine(Path.GetDirectoryName(DataDirectory)!, "browser-points.json");
        var fixture = JsonSerializer.Deserialize<BrowserPoints>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Browser fixture coordinates are missing.");
        var monitor = Native.Monitors().First(item => item.Primary);
        var work = monitor.Info.Work;
        double scale = monitor.Dpi / 96d;
        Settings.Save(Settings.Data with { Detached = true, Pinned = true, DetachedHideOnBlur = false,
            ExpandedWidth = 450, ExpandedHeight = 420, ExpandedX = work.Right - (int)(470 * scale),
            ExpandedY = work.Top + (int)(80 * scale) });
        var button = new Button { Content = "Halo" };
        var owner = AIEnter.App.FixtureWindow(button, new(work.Right - 210, work.Bottom - 68, 170, 58));
        try
        {
            SetFixtureLayer((nint)fixture.Handle, -1, 0, 0, 0, 0, 0x0013);
            AttachTaskbar(button);
            Panel = CreatePanel();
            await Task.Delay(250);
            Panel.Open();
            await Panel.VerifyBrowserDrops((nint)fixture.Handle, InputCoordinate(fixture.TextX, fixture.TextY),
                InputCoordinate(fixture.LinkX, fixture.LinkY), InputCoordinate(fixture.ImageX, fixture.ImageY), checks);
        }
        finally
        {
            SetFixtureLayer((nint)fixture.Handle, -2, 0, 0, 0, 0, 0x0013);
            Panel?.Destroy(); Panel = null; owner.Close();
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    static extern bool SetFixtureLayer(nint window, nint after, int x, int y, int width, int height, uint flags);
}

internal sealed partial class StationWindow
{
    internal async Task VerifyBrowserDrops(nint browser, AIEnter.Native.Point text, AIEnter.Native.Point link, AIEnter.Native.Point image, List<string> checks)
    {
        await Task.Delay(450);
        var bounds = CurrentBounds();
        var target = InputPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var fixtures = new[]
        {
            ("BrowserSelectedText", text, (Func<StationItem, bool>)(item => item.Kind == StationKind.Text && item.Value.Contains("Halo 摘录验证"))),
            ("BrowserHyperlink", link, item => item.Kind == StationKind.Link && item.Value == "https://example.com/halo-drop"),
            ("BrowserImage", image, item => item is { Kind: StationKind.File, Owned: true } && File.Exists(item.Value))
        };
        var failures = new List<string>();
        foreach (var (name, source, matches) in fixtures)
        {
            var atSource = At(source.X, source.Y);
            var atTarget = At(target.X, target.Y);
            if (atSource != browser || atTarget != InteractionWindow)
            {
                failures.Add($"{name}: source {atSource}/{browser}, target {atTarget}/{InteractionWindow}.");
                continue;
            }
            await Task.Run(() => InputFixture.Drag(source, target, sendNativeMovement: true));
            for (int retry = 0; retry < 35 && !App.Station.Items.Any(matches); retry++) await Task.Delay(100);
            var item = App.Station.Items.FirstOrDefault(matches);
            if (item is null)
            {
                failures.Add($"{name}: missing item. Status={status.Text}; items={string.Join(", ", App.Station.Items.Select(item => $"{item.Kind}: {item.Value}"))}");
                continue;
            }
            if (item.Kind == StationKind.File)
            {
                var file = await StorageFile.GetFileFromPathAsync(item.Value);
                using var stream = await file.OpenReadAsync();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                if (decoder.PixelWidth != 120 || decoder.PixelHeight != 72)
                    throw new InvalidOperationException("Browser image dimensions did not survive the drop.");
            }
            checks.Add(name);
            await Task.Delay(450);
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }
}
