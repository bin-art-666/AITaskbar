using System.Text.Json;

namespace AIEnter;

public partial class App
{
    async Task VerifyAiModes()
    {
        AiWindow? window = null;
        AiOptionsStore? store = null;
        try
        {
            // Require an isolated directory so this check never touches a live profile.
            if (!Environment.GetCommandLineArgs().Contains("--data-dir"))
                throw new InvalidOperationException("Mode verification requires --data-dir.");
            Directory.CreateDirectory(DataDirectory);
            store = new AiOptionsStore(Path.Combine(DataDirectory, "ai.json"));
            if (store.ReadOnly) throw new IOException(store.Warning);
            store.Save(store.Data with { FloatingPanel = true, DockTopOffset = 20000 });
            var display = Native.Monitors().First(m => m.Primary);
            window = new AiWindow(store, DataDirectory, display, () => { }, verification: true);
            store.Changed += window.ApplyOptions;
            await window.Open(display, display.Info.Work.Top);
            var result = await window.VerifyModeSwitches();
            File.WriteAllText(Path.Combine(DataDirectory, "mode-result.json"), JsonSerializer.Serialize(result));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(Path.Combine(DataDirectory, "mode-result.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
        }
        finally
        {
            if (window != null)
            {
                if (store != null) store.Changed -= window.ApplyOptions;
                window.Destroy();
            }
            Exit();
        }
    }
}

internal sealed partial class AiWindow
{
    internal async Task<object> VerifyModeSwitches()
    {
        if (!verification || current?.View.CoreWebView2 is not { } core)
            throw new InvalidOperationException("Only the local verification page supports this check.");
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (await core.ExecuteScriptAsync("!!document.getElementById('draft')") == "true") break;
            await Task.Delay(100);
        }
        const string draft = "模式切换后保留这份草稿";
        await core.ExecuteScriptAsync("document.getElementById('draft').value=" + JsonSerializer.Serialize(draft));
        var view = current.View;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task CheckDraft()
        {
            Check(ReferenceEquals(view, current?.View) && ReferenceEquals(core, current?.View.CoreWebView2), "Switching recreated the browser.");
            Check(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.getElementById('draft').value")) == draft, "Switching lost the draft.");
        }
        Check(!Detached && !AppWindow.IsShownInSwitchers, "Default floating panel was not restored.");
        void CheckPlacement()
        {
            var work = display.Info.Work;
            var expected = AiPolicy.DockBounds(dockWidth, dockHeight,
                new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), display.Dpi);
            Check(AppWindow.Position.X == expected.X && AppWindow.Position.Y == expected.Y
                && AppWindow.Size.Width == expected.Width && AppWindow.Size.Height == expected.Height,
                "Panel did not open at bottom-right or a legacy top offset leaked into positioning.");
        }
        CheckPlacement();
        Check(root.RowDefinitions.Count == 2 && statusHost.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed,
            "Compact layout retained a title row or permanent status footer.");
        var originalWidth = options.Data.DockWidth;
        App.PreviewAppearance(new UiAppearance(20, 44, 16, 36, true, 12, 100));
        options.Save(options.Data with { DockWidth = 360 });
        await Task.Delay(180); root.UpdateLayout();
        Check(aiHeader.ActualHeight <= 45 && providerViewport.ActualWidth > 0,
            "Largest controls did not fit a single narrow toolbar row.");
        var selected = providerLabels.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().First(b => b.IsChecked == true);
        Check(selected.Opacity == 1 && selected.ActualWidth <= providerViewport.ActualWidth + 1, "Current AI service was clipped at the narrow size.");
        Check(selected.CornerRadius.TopLeft == 20 && expand.Height == 44, "Appearance preview did not change button size and radius.");
        foreach(var button in providerLabels.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Where(b=>b.Visibility==Microsoft.UI.Xaml.Visibility.Visible))
        {
            Check(button.IsChecked==true || button.Width==(double)button.Resources["AIEnterNaturalWidth"], "A service button was shrunk instead of placed in overflow.");
            double x=button.TransformToVisual(providerLabels).TransformPoint(new()).X;
            Check(x>=-.5 && x+button.ActualWidth<=providerViewport.ActualWidth+1,"A visible AI service button was only partly shown.");
        }
        await CheckDraft();
        App.PreviewAppearance(null);
        options.Save(options.Data with { DockWidth = originalWidth });
        await Task.Delay(120); root.UpdateLayout();
        Check(expand.Height == UiDefaults.ButtonSize && root.Padding.Left == UiDefaults.WindowPadding, "Cancelling preview did not restore saved defaults.");
        await CheckDraft();
        App.PreviewAppearance(new UiAppearance(BackplateRadius: 40));
        await Task.Delay(120); VerifyBackplateCorners(); await CheckDraft();
        App.PreviewAppearance(new UiAppearance(BackplateRadius: 12));
        await Task.Delay(120); VerifyBackplateCorners(); await CheckDraft();
        App.PreviewAppearance(null); VerifyBackplateCorners();
        var originalOrder = options.Data.ProviderOrder;
        var reversedOrder = AiPolicy.AvailableProviders(options.Data).Reverse().ToArray();
        options.Save(options.Data with { ProviderOrder = reversedOrder });
        await Task.Delay(120); root.UpdateLayout();
        Check(providerChoices.Select(c => c.Id).SequenceEqual(reversedOrder), "Provider choices did not follow saved order.");
        Check(providerLabels.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Select(b => ((Choice)b.Tag).Id).SequenceEqual(providerChoices.Select(c => c.Id)), "Toolbar buttons did not follow saved order.");
        RefreshProviderOverflow();
        var hiddenNames = providerLabels.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().Where(b => b.Visibility == Microsoft.UI.Xaml.Visibility.Collapsed).Select(b => ((Choice)b.Tag).Name).ToArray();
        if (hiddenNames.Length > 0)
            Check(((Microsoft.UI.Xaml.Controls.MenuFlyout)providerMore.Flyout).Items.OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutItem>().Select(i => i.Text).SequenceEqual(hiddenNames), "Overflow menu did not follow saved order.");
        await CheckDraft();
        options.Save(options.Data with { ProviderOrder = originalOrder });
        await Task.Delay(120); await CheckDraft();
        App.PreviewAppearance(new UiAppearance(ButtonColor: "#FF203040", SelectedButtonColor: "#FFAA2255"));
        await Task.Delay(120); root.UpdateLayout();
        var colorButtons = providerLabels.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>().ToArray();
        Check(((Microsoft.UI.Xaml.Media.SolidColorBrush)expand.Background).Color == Windows.UI.Color.FromArgb(255,32,48,64), "Normal button color preview did not apply.");
        var checkedButton = colorButtons.First(b => b.IsChecked == true);
        Check(((Microsoft.UI.Xaml.Media.SolidColorBrush)checkedButton.Resources["ToggleButtonBackgroundChecked"]).Color == Windows.UI.Color.FromArgb(255,170,34,85), "Selected AI color preview did not apply.");
        Check(((Microsoft.UI.Xaml.Media.SolidColorBrush)expand.Foreground).Color == Microsoft.UI.Colors.White, "Dark button did not get readable text.");
        await CheckDraft();
        App.PreviewAppearance(null); await Task.Delay(120);
        Check(((Microsoft.UI.Xaml.Media.SolidColorBrush)expand.Background).Color != Windows.UI.Color.FromArgb(255,32,48,64), "Button color preview did not roll back.");
        await CheckDraft();
        int originalTop = AppWindow.Position.Y;
        BeginDockResize(0, AiPanelEdge.Bottom);
        MoveDockResize(-80);
        EndDockResize(false);
        Check(AppWindow.Position.Y == originalTop, "Bottom resize moved the opposite edge.");
        await CheckDraft();
        Hide(); await Open(display, dockTop);
        CheckPlacement();
        await CheckDraft();

        int floatingRadius = backplateShape!.Value.Radius;
        ToggleDetached();
        Check(Detached && AppWindow.IsShownInSwitchers && !options.Data.FloatingPanel, "Expansion did not enter and save independent mode.");
        await VerifyFloatingCaption(true); VerifyBackplateCorners();
        Check(backplateShape!.Value.Radius == floatingRadius, "Expanded mode changed the configured backplate radius.");
        var expandedSize = AppWindow.Size;
        AppWindow.Resize(new(expandedSize.Width - 60, expandedSize.Height - 40));
        await Task.Delay(180); VerifyBackplateCorners();
        AppWindow.Resize(expandedSize);
        await Task.Delay(180); VerifyBackplateCorners();
        await CheckDraft();
        ToggleDetached();
        Check(!Detached && !AppWindow.IsShownInSwitchers && options.Data.FloatingPanel, "Return did not restore floating mode.");
        await VerifyFloatingCaption(false); VerifyBackplateCorners();
        // Exercise settled presenter state, including unchanged-size HRGN loss.
        for (int cycle = 0; cycle < 3; cycle++)
        {
            ToggleDetached(); await Task.Delay(250); VerifyBackplateCorners(); await CheckDraft();
            SetWindowRgn(WinRT.Interop.WindowNative.GetWindowHandle(this), IntPtr.Zero, true);
            QueueFrameRefresh(); await Task.Delay(150); VerifyBackplateCorners();
            ToggleDetached(); await Task.Delay(250); VerifyBackplateCorners(); await CheckDraft();
        }
        CheckPlacement();
        await CheckDraft();
        Hide();
        options.Save(options.Data with { FloatingPanel = false });
        Check(!IsOpen && Detached, "Changing settings unexpectedly reopened the hidden window.");
        await Open(display, dockTop);
        await CheckDraft();
        options.Save(options.Data with { FloatingPanel = true });
        Check(!Detached && options.Data.FloatingPanel, "Settings did not apply floating mode.");
        CheckPlacement();
        await CheckDraft();
        await VerifyMultipleProviders(); await CheckDraft();
        await VerifyToggleModes(); await CheckDraft();
        await VerifyFloatFadeMotion(); await CheckDraft();
        await VerifyDropInputs(); await CheckDraft();
        return new { passed = true, localPageOnly = true, floatFadeMotion = true, exitReopenCancellation = true, exitInputDisabled = true, reducedMotion = true, taskbarToggle = true, minimizedRestore = true, sameGestureDismissal = true, windowDrop = true, resizedDrop = true, expandedDrop = true, bitmapDrop = true, taskbarPacket = true, currentAiDropTarget = true, noAutoSend = true, dropRetryPreserved = true, sameBrowser = true, draftPreserved = true, multipleProviders = true, addedDraftsPreserved = true, lazyProviderLoading = true, removedProviderFallback = true,
            independentMode = true, floatingMode = true, hiddenSettingsStayHidden = true, nativeHeaderDrag = true, headerButtonsRemainInteractive = true,
            buttonColorPreview = true, buttonColorRollback = true, providerOrder = true, providerOverflowOrder = true, flushConversationEdges = true, headerOnlyGlass = true, fullContentCornerClip = true, transparentWindowCanvas = true, boundedGlassCorners = true, consistentModeCorners = true, expandedResizeEdges = true, expandedResizeClip = true, backplateCorners = true, backplatePreviewRollback = true, compactToolbar = true, hiddenFooter = true, maximumAppearanceOnNarrowWindow = true, previewRollback = true,
            bottomRight = true, legacyOffsetIgnored = true, resizeOppositeEdge = true, reanchorsOnOpen = true };
    }
}
