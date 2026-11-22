using System.IO.Pipes;
using System.Text.Json;

namespace AITaskbar;

internal static class BrowserRecentSessions
{
    internal static async Task<BrowserRecentEntry[]> ReadAsync(RecentBrowser browser)
    {
        var reply = await SendAsync(browser, "list");
        return reply?.Items?.Where(item => item is { SessionId.Length: > 0 and <= 128, Title.Length: > 0 and <= 160 })
            .Take(BrowserRecentProtocol.Limit).ToArray() ?? [];
    }

    internal static async Task<bool> RestoreAsync(RecentBrowser browser, string sessionId) =>
        await SendAsync(browser, "restore", sessionId) is not null;

    private static async Task<BrowserRecentReply?> SendAsync(RecentBrowser browser, string action, string? sessionId = null)
    {
        using var timeout = new CancellationTokenSource(850);
        using var pipe = new NamedPipeClientStream(".", BrowserRecentProtocol.PipeName(browser), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(120, timeout.Token);
            var request = new BrowserRecentRequest(Guid.NewGuid().ToString("N"), action, sessionId);
            await BrowserRecentProtocol.WriteAsync(pipe, request, timeout.Token);
            var reply = await BrowserRecentProtocol.ReadAsync<BrowserRecentReply>(pipe, timeout.Token);
            return reply is { Ok: true } && reply.Id == request.Id ? reply : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException or
                                      UnauthorizedAccessException or JsonException) { return null; }
    }
}
