using System.IO.Pipes;
using System.Text.Json;

namespace AITaskbar;

internal static class EdgeRecentFiles
{
    internal static async Task<EdgeRecentEntry[]> ReadAsync()
    {
        var reply = await SendAsync("list");
        return reply?.Items?.Where(item => item is { SessionId.Length: > 0 and <= 128, Title.Length: > 0 and <= 160 })
            .Take(EdgeRecentProtocol.Limit).ToArray() ?? [];
    }

    internal static async Task<bool> RestoreAsync(string sessionId) =>
        await SendAsync("restore", sessionId) is not null;

    private static async Task<EdgeRecentReply?> SendAsync(string action, string? sessionId = null)
    {
        using var timeout = new CancellationTokenSource(850);
        using var pipe = new NamedPipeClientStream(".", EdgeRecentProtocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(120, timeout.Token);
            var request = new EdgeRecentRequest(Guid.NewGuid().ToString("N"), action, sessionId);
            await EdgeRecentProtocol.WriteAsync(pipe, request, timeout.Token);
            var reply = await EdgeRecentProtocol.ReadAsync<EdgeRecentReply>(pipe, timeout.Token);
            return reply is { Ok: true } && reply.Id == request.Id ? reply : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException or
                                      UnauthorizedAccessException or JsonException) { return null; }
    }
}
