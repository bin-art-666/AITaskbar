using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using AITaskbar;

var browser = args.FirstOrDefault() == BrowserRecentProtocol.ChromeOrigin ? RecentBrowser.Chrome : RecentBrowser.Edge;
using var lifetime = new CancellationTokenSource();
using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
var replies = Channel.CreateBounded<BrowserRecentReply>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest });
_ = Task.Run(async () =>
{
    try
    {
        while (await BrowserRecentProtocol.ReadAsync<BrowserRecentReply>(input, lifetime.Token) is { } reply)
            await replies.Writer.WriteAsync(reply, lifetime.Token);
    }
    catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException) { }
    finally { lifetime.Cancel(); }
});

try
{
    using var pipe = new NamedPipeServerStream(BrowserRecentProtocol.PipeName(browser), PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
    while (!lifetime.IsCancellationRequested)
    {
        await pipe.WaitForConnectionAsync(lifetime.Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(1000);
        try
        {
            var request = await BrowserRecentProtocol.ReadAsync<BrowserRecentRequest>(pipe, deadline.Token);
            if (request is not { Id.Length: > 0 and <= 64, Action: "list" or "restore" }) continue;
            await BrowserRecentProtocol.WriteAsync(output, request, deadline.Token);
            BrowserRecentReply reply;
            do { reply = await replies.Reader.ReadAsync(deadline.Token); } while (reply.Id != request.Id);
            await BrowserRecentProtocol.WriteAsync(pipe, reply, deadline.Token);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or OperationCanceledException) { }
        finally { pipe.Disconnect(); }
    }
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
