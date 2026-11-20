using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AIEnter;

public partial class App
{
    async Task VerifyAiSiteCapabilities()
    {
        string reportPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".build", "ai-site-capability-report.json"));
        var rows = new List<object>(); Window? window = null; WebView2? view = null;
        try
        {
            window = new Window { Title = "AIEnter · 网站接收能力检查" };
            view = new WebView2(); window.Content = view;
            _ = WinRT.Interop.WindowNative.GetWindowHandle(window);
            window.AppWindow.MoveAndResize(new(-30000, -30000, 980, 720));
            window.AppWindow.Show(false);
            AiOptionsStore store = Ai.Options;
            var arguments = Environment.GetCommandLineArgs();
            int filterIndex = Array.IndexOf(arguments, "--probe-providers");
            var filter = filterIndex >= 0 && filterIndex + 1 < arguments.Length ? arguments[filterIndex + 1].Split(',').ToHashSet(StringComparer.Ordinal) : null;
            foreach (string id in AiPolicy.OrderedProviders(store.Data).Where(id => filter == null || filter.Contains(id)))
            {
                AiEndpoint endpoint;
                try { endpoint = AiPolicy.Resolve(store.Data with { Provider = id }); }
                catch { continue; }
                object row;
                try
                {
                    string profile = Path.Combine(DataDirectory, "AiWebProfiles", endpoint.Key);
                    var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
                    await view.EnsureCoreWebView2Async(environment).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
                    bool probePaste = Environment.GetCommandLineArgs().Contains("--probe-paste-delivery");
                    if (probePaste)
                        await view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.addEventListener('paste',e=>{if(window.__aiEnterPasteAck){window.__aiEnterPasteAck.received=true;window.__aiEnterPasteAck.count=e.clipboardData.files.length;}e.preventDefault();e.stopImmediatePropagation();window.__aiEnterPasteProbe={trusted:e.isTrusted,files:[...e.clipboardData.files].map(f=>({type:f.type,size:f.size}))};},true);");
                    var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Navigated(object? sender, CoreWebView2NavigationCompletedEventArgs args) => loaded.TrySetResult(args.IsSuccess);
                    view.CoreWebView2.NavigationCompleted += Navigated;
                    view.CoreWebView2.Navigate(endpoint.Url);
                    bool loadSucceeded = await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    await Task.Delay(2500); // Allow single-page apps to render their composer after initial navigation.
                    string host = Uri.TryCreate(view.CoreWebView2.Source, UriKind.Absolute, out var uri) ? uri.Host : "";
                    string script = "(()=>{const v=e=>!!e.getClientRects().length&&getComputedStyle(e).visibility!=='hidden';const ed=[...document.querySelectorAll('textarea:not([disabled]):not([readonly]),[contenteditable=true][role=textbox],[contenteditable=true]')].filter(v);const fi=[...document.querySelectorAll('input[type=file]:not([disabled])')];return {ready:document.readyState,editors:ed.length,fileInputs:fi.length,inputDetails:fi.map(e=>({accept:e.accept,multiple:e.multiple,ariaHidden:e.getAttribute('aria-hidden'),inert:!!e.closest('[inert]'),form:!!e.closest('form'),visible:!!v(e)})),editorDetails:ed.map(e=>({tag:e.tagName,id:e.id,role:e.getAttribute('role')})),loginFields:[...document.querySelectorAll('input[type=password]')].filter(v).length};})()";
                    using var capabilities = JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync(script).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
                    int editorCount = capabilities.RootElement.GetProperty("editors").GetInt32();
                    int inputCount = capabilities.RootElement.GetProperty("fileInputs").GetInt32();
                    bool image = editorCount > 0 && await DetectFileInput(view, ["fixture.png"]);
                    bool document = editorCount > 0 && await DetectFileInput(view, ["fixture.pdf"]);
                    bool multiple = editorCount > 0 && await DetectFileInput(view, ["fixture.png", "fixture.pdf"]);
                    JsonElement? nativePasteDelivery = null;
                    if (probePaste && editorCount > 0)
                    {
                        var pixels = new byte[64*32*4];
                        for(int i=0;i<pixels.Length;i+=4){pixels[i]=200;pixels[i+1]=160;pixels[i+2]=40;pixels[i+3]=255;}
                        string picture = await new ScreenshotFrame(0,0,64,32,pixels).Save(Path.Combine(Path.GetDirectoryName(reportPath)!,"paste-probe"));
                        view.Focus(FocusState.Programmatic);
                        await WebAttachmentPaste.Deliver(view.CoreWebView2,[picture],()=>{});
                        await Task.Delay(250);
                        using var probe=JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync("window.__aiEnterPasteProbe??null"));
                        nativePasteDelivery=probe.RootElement.Clone();
                    }
                    row = new { service = endpoint.Name, host, loaded = loadSucceeded, editors = editorCount, fileInputs = inputCount, nativePasteDelivery,
                        componentDetails = capabilities.RootElement.Clone(), imageInput = image, documentInput = document, multipleFilesInput = multiple, result = nativePasteDelivery is { ValueKind: JsonValueKind.Object } delivery && delivery.TryGetProperty("trusted",out var trusted) && trusted.GetBoolean() && delivery.GetProperty("files").GetArrayLength() > 0 ? "native-paste-delivery-ready" : editorCount > 0 && image && document && multiple ? "composer-and-input-ready" : "login-or-site-state-needs-review" };
                    view.CoreWebView2.NavigationCompleted -= Navigated;
                    view.Close(); view = new WebView2(); window.Content = view;
                }
                catch (Exception error)
                {
                    row = new { service = endpoint.Name, loaded = false, result = error is TimeoutException ? "navigation-timeout" : "probe-failed", errorType = error.GetType().Name };
                    try { view?.Close(); } catch { }
                    view = new WebView2(); window.Content = view;
                }
                rows.Add(row);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                File.WriteAllText(reportPath, JsonSerializer.Serialize(new { nonMessagingProbe = true, sentMessages = 0, filesAttached = false, navigationPerformed = true, sites = rows }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { nonMessagingProbe = true, errorType = error.GetType().Name, navigationPerformed = true, sites = rows }));
        }
        finally
        {
            try { view?.Close(); } catch { }
            try { window?.Close(); } catch { }
            QuitStandaloneAi();
        }
        await Task.CompletedTask;
    }

    static async Task<bool> DetectFileInput(WebView2 view, string[] fileNames)
    {
        string raw = await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
        { expression = DropWebScripts.FileInput(fileNames), returnByValue = false }));
        using var document = JsonDocument.Parse(raw);
        var result = document.RootElement.GetProperty("result");
        if (!result.TryGetProperty("objectId", out var objectId) || result.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "null") return false;
        try { await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.releaseObject", JsonSerializer.Serialize(new { objectId = objectId.GetString() })); } catch { }
        return true;
    }
}
