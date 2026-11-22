using System.Buffers.Binary;
using System.Text.Json;

namespace AITaskbar;

internal enum RecentBrowser { Edge, Chrome }

internal sealed record BrowserRecentEntry(string SessionId, string Title);
internal sealed record BrowserRecentRequest(string Id, string Action, string? SessionId = null);
internal sealed record BrowserRecentReply(string Id, bool Ok, BrowserRecentEntry[]? Items = null);

internal static class BrowserRecentProtocol
{
    internal const int Limit = 5;
    internal const string ChromeOrigin = "chrome-extension://ejgcgkmboopnpecjhmppoehaacinokeb/";
    internal const string EdgeOrigin = "chrome-extension://ejfpgkpgggjobadkfedhkoimfedhggan/";
    private const int MaximumMessageBytes = 16 * 1024;
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    internal static string PipeName(RecentBrowser browser)
    {
        var name = $"AITaskbar.{browser}Recent.v1";
        string? instance = Environment.GetEnvironmentVariable($"AITASKBAR_{browser.ToString().ToUpperInvariant()}_RECENT_INSTANCE");
        return instance is { Length: > 0 and <= 40 } && instance.All(char.IsAsciiLetterOrDigit)
            ? $"{name}.{instance}" : name;
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
