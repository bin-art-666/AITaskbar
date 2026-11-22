using System.Buffers.Binary;
using System.Text.Json;

namespace AITaskbar;

internal sealed record EdgeRecentEntry(string SessionId, string Title);
internal sealed record EdgeRecentRequest(string Id, string Action, string? SessionId = null);
internal sealed record EdgeRecentReply(string Id, bool Ok, EdgeRecentEntry[]? Items = null);

internal static class EdgeRecentProtocol
{
    internal const int Limit = 5;
    private const int MaximumMessageBytes = 16 * 1024;
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    internal static string PipeName
    {
        get
        {
            string? instance = Environment.GetEnvironmentVariable("AITASKBAR_EDGE_RECENT_INSTANCE");
            return instance is { Length: > 0 and <= 40 } && instance.All(char.IsAsciiLetterOrDigit)
                ? $"AITaskbar.EdgeRecent.v1.{instance}" : "AITaskbar.EdgeRecent.v1";
        }
    }

    internal static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken token) where T : class
    {
        byte[] header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), token) == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(1), token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaximumMessageBytes) throw new InvalidDataException("Invalid message size.");
        byte[] message = new byte[size];
        await stream.ReadExactlyAsync(message, token);
        return JsonSerializer.Deserialize<T>(message, json);
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] message = JsonSerializer.SerializeToUtf8Bytes(value, json);
        if (message.Length > MaximumMessageBytes) throw new InvalidDataException("Message too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, message.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(message, token);
        await stream.FlushAsync(token);
    }
}
