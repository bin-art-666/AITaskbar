using System.Text.Json;

namespace AIEnter;

internal sealed partial class DropPayload
{
    internal static async Task<(DropPayload Payload, string Provider)> ReadPacket(string packetPath, string directory)
    {
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(packetPath).StartsWith(root, StringComparison.OrdinalIgnoreCase) || new FileInfo(packetPath).Length > 256 * 1024)
            throw new IOException("拖放数据无效或过大。");
        var packet = JsonSerializer.Deserialize<TaskbarDropPacket>(await File.ReadAllTextAsync(packetPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IOException("拖放数据无效。");
        if (!string.IsNullOrEmpty(packet.Error)) throw new IOException(packet.Error);
        if (packet.Version != 1 || packet.Text == null || packet.Text.Length > 20000 || packet.Provider == null || (packet.Files?.Length ?? 0) > 12)
            throw new IOException("一次最多 12 个文件、20000 字。");
        var result = new DropPayload { Text = packet.Text, Files = packet.Files ?? [] };
        string batch = Path.Combine(directory, Path.GetFileNameWithoutExtension(packetPath)) + Path.DirectorySeparatorChar;
        bool Managed(string path) => Path.IsPathFullyQualified(path) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(batch), StringComparison.OrdinalIgnoreCase);
        try
        {
            if ((packet.OwnedFiles ?? []).Any(p => !Managed(p)) || (packet.Bitmap != null && !Managed(packet.Bitmap))) throw new IOException("临时附件位置无效。");
            foreach (var file in packet.OwnedFiles ?? []) result.OwnFile(file);
            if (result.Files.Any(p => !Path.IsPathFullyQualified(p) || !File.Exists(p))) throw new IOException("附件不存在或无法读取，不支持文件夹。");
            if (packet.Bitmap != null)
            {
                if (result.Files.Length > 0) throw new IOException("拖放图片数据格式冲突。");
                string image = await ImageAttachments.SaveFile(packet.Bitmap); result.OwnFile(image); result.Files = [image];
            }
            if (result.Files.Length == 0 && string.IsNullOrWhiteSpace(result.Text)) throw new IOException("拖放内容为空。");
            return (result, packet.Provider);
        }
        catch { result.Dispose(); throw; }
    }
}