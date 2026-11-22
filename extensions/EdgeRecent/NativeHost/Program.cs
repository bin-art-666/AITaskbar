using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using AITaskbar;

using var lifetime = new CancellationTokenSource();
using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
var replies = Channel.CreateBounded<EdgeRecentReply>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
_ = Task.Run(async () =>
{
    try
    {
        while (await EdgeRecentProtocol.ReadAsync<EdgeRecentReply>(input, lifetime.Token) is { } reply)
            await replies.Writer.WriteAsync(reply, lifetime.Token);
    }
    catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException) { }
    finally { lifetime.Cancel(); }
});

try
{
    using var pipe = new NamedPipeServerStream(EdgeRecentProtocol.PipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
    while (!lifetime.IsCancellationRequested)
    {
        await pipe.WaitForConnectionAsync(lifetime.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(1000);
        try
        {
            var request = await EdgeRecentProtocol.ReadAsync<EdgeRecentRequest>(pipe, deadline.Token);
            if (request is not { Id.Length: > 0 and <= 64, Action: "list" or "restore" }) continue;
            await EdgeRecentProtocol.WriteAsync(output, request, deadline.Token);
            EdgeRecentReply reply;
            do { reply = await replies.Reader.ReadAsync(deadline.Token); } while (reply.Id != request.Id);
            await EdgeRecentProtocol.WriteAsync(pipe, reply, deadline.Token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException) { }
        finally { pipe.Disconnect(); }
    }
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
