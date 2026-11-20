using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AIEnter;

internal sealed class FirstOpenTiming(uint pressed, uint dispatched)
{
    internal static uint Now => unchecked((uint)Environment.TickCount);
    readonly Dictionary<string, uint> milliseconds = new() { ["gestureMs"] = unchecked(dispatched - pressed) };
    internal void Mark(string stage) => milliseconds.TryAdd(stage, unchecked(Now - pressed));

    internal async Task RecordComposer(CoreWebView2 core, string directory, Func<bool> closed)
    {
        string result = "composer-timeout";
        try
        {
            long deadline = Environment.TickCount64 + 30000;
            while (!closed() && Environment.TickCount64 < deadline)
            {
                const string probe = """
                    [...document.querySelectorAll('textarea:not([disabled]):not([readonly]),[contenteditable=true]:not([aria-hidden=true])')]
                        .some(e => e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden')
                    """;
                try
                {
                    if (await core.ExecuteScriptAsync(probe).AsTask().WaitAsync(TimeSpan.FromSeconds(2)) == "true")
                    {
                        Mark("composerReadyMs");
                        result = "composer-visible";
                        break;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* The document can navigate during a probe. */ }
                await Task.Delay(100);
            }
            if (closed()) result = "closed";
        }
        finally { Save(directory, result); }
    }

    internal void Save(string directory, string result)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "first-open-timing.json"),
                JsonSerializer.Serialize(new { recordedAt = DateTimeOffset.Now, result, milliseconds }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
