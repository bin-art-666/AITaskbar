using System.Text.Json;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AIEnter;

internal sealed partial class AiWindow
{
    async Task VerifyMultipleProviders()
    {
        var saved = options.Data;
        var original = current!;
        int initialPages = pages.Count;
        var added = Enumerable.Range(0, 80).Select(i => new AiProvider("ai-test-" + i, "测试 AI " + i, "https://example.invalid/ai/" + i)).ToArray();
        void Check(bool pass, string reason) { if (!pass) throw new Exception(reason); }
        async Task Ready()
        {
            await ShowSelected();
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (current!.Failed) throw new Exception(current.Status);
                if (current.View.CoreWebView2 is { } activeCore && await activeCore.ExecuteScriptAsync("!!document.getElementById('draft')") == "true") return;
                await Task.Delay(50);
            }
            throw new Exception("Added AI fixture did not load.");
        }
        async Task<string?> Draft() => JsonSerializer.Deserialize<string>(await current!.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('draft').value"));
        try
        {
            options.Save(AiPolicy.WithProviderSettings(saved, added, AiPolicy.OrderedProviders(saved).Concat(added.Select(p => p.Id)).ToArray(), added[1].Id));
            await Task.Delay(120);
            Check(pages.Count == initialPages && ReferenceEquals(current, original), "Adding AI eagerly loaded websites or replaced current page.");
            Check(providerChoices.Count == 84, "Added AI did not enter dynamic toolbar choices.");
            RefreshProviderOverflow();
            Check(((MenuFlyout)providerMore.Flyout).Items.OfType<MenuFlyoutItem>().Any(item => item.Text == added[^1].Name), "Overflow lost added providers.");
            options.Save(options.Data with { Provider = added[0].Id }); await Ready();
            var first = current!;
            await first.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('draft').value='first draft'");
            options.Save(options.Data with { Provider = added[1].Id }); await Ready();
            var second = current!;
            await second.View.CoreWebView2.ExecuteScriptAsync("document.getElementById('draft').value='second draft'");
            options.Save(options.Data with { Provider = added[0].Id }); await Ready();
            Check(ReferenceEquals(first, current) && await Draft() == "first draft", "Switching added AI discarded its browser or draft.");
            var renamed = added.ToArray(); renamed[0] = renamed[0] with { Name = "重命名 AI" };
            options.Save(options.Data with { AddedProviders = renamed }); await Ready();
            Check(current!.Endpoint.Name == "重命名 AI" && await Draft() == "first draft", "Renaming reloaded the added AI page.");
            options.Save(AiPolicy.WithProviderSettings(options.Data, added[1..], [], added[1].Id)); await Ready();
            Check(ReferenceEquals(current, second) && await Draft() == "second draft", "Removing current AI did not return to default draft.");
            options.Save(AiPolicy.WithProviderSettings(options.Data, [], [], added[1].Id)); await Ready();
            Check(options.Data.Provider == "deepseek" && options.Data.DefaultProvider == "deepseek" && ReferenceEquals(original, current), "Removing default did not safely return to DeepSeek.");
        }
        finally { options.Save(saved); await ShowSelected(); }
    }
}