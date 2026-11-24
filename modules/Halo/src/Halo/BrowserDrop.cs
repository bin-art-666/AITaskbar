using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Halo;

// Read only formats supplied by the drag source while its COM data object is valid.
internal sealed record BrowserDrop(string? Text, string? Html, Uri? Link, byte[]? Image)
{
    const int ImageLimit = 32 * 1024 * 1024;
    const short UnicodeText = 13, FileDrop = 15;
    static readonly short HtmlFormat = Register("HTML Format");
    static readonly short UrlFormat = Register("UniformResourceLocatorW");
    static readonly short PngFormat = Register("PNG");
    static readonly short FileDescriptorFormat = Register("FileGroupDescriptorW");
    static readonly short FileContentsFormat = Register("FileContents");

    internal static bool Supports(IDataObject data) => !Has(data, FileDrop)
        && (Has(data, HtmlFormat) || Has(data, UrlFormat) || Has(data, UnicodeText) || Has(data, PngFormat));

    internal static BrowserDrop? Read(IDataObject data)
    {
        string? text = Decode(ReadBytes(data, UnicodeText, StationStore.MaximumText * 2 + 2), Encoding.Unicode);
        string? html = Decode(ReadBytes(data, HtmlFormat, 2_000_000), Encoding.UTF8);
        var link = StationStore.WebUri(Decode(ReadBytes(data, UrlFormat, 64 * 1024), Encoding.Unicode)?.Trim() ?? "");
        string? imageSource = html is null ? null : RichContent.SingleImageSource(html);
        bool imageOnly = imageSource is not null || html is null && Has(data, PngFormat);
        if (imageOnly)
        {
            byte[]? image = ReadBytes(data, PngFormat, ImageLimit);
            // Chromium can offer an image as a promised file instead of clipboard pixels.
            if (image is null && imageSource is not null && ReadBytes(data, FileDescriptorFormat, 64 * 1024) is { Length: >= 4 } descriptor
                && BitConverter.ToUInt32(descriptor) == 1)
                image = ReadBytes(data, FileContentsFormat, ImageLimit, 0);
            if (image is null && imageSource is not null)
                image = EmbeddedImage(imageSource);
            if (image is { Length: > 0 }) return new(null, null, null, image);
            // Some sources supply only an address. Keep it as a normal link without fetching remote media.
            if (imageSource is not null)
            {
                link = StationStore.WebUri(imageSource) ?? link;
                html = null;
            }
        }
        return string.IsNullOrWhiteSpace(text) && html is null && link is null ? null : new(text, html, link, null);
    }

    internal async Task<DataPackageView> Package(InMemoryRandomAccessStream? bitmap)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (!string.IsNullOrWhiteSpace(Text)) package.SetText(Text);
        if (Html is not null) package.SetHtmlFormat(Html);
        if (Link is not null) package.SetWebLink(Link);
        if (Image is not null && bitmap is not null)
        {
            using (var writer = new DataWriter(bitmap))
            {
                writer.WriteBytes(Image);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            bitmap.Seek(0);
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(bitmap));
        }
        return package.GetView();
    }

    static byte[]? EmbeddedImage(string source)
    {
        int comma = source.IndexOf(',');
        if (comma < 0 || !source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            || !source.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            || source.Length - comma - 1 > ImageLimit * 4L / 3 + 4) return null;
        try
        {
            byte[] bytes = Convert.FromBase64String(source[(comma + 1)..]);
            return bytes.Length <= ImageLimit ? bytes : null;
        }
        catch (FormatException) { return null; }
    }

    static string? Decode(byte[]? bytes, Encoding encoding) => bytes is null ? null : encoding.GetString(bytes).Split('\0', 2)[0];
    static short Register(string name) => unchecked((short)RegisterClipboardFormat(name));
    static FORMATETC Format(short id, int index = -1) => new()
    {
        cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index,
        tymed = index < 0 ? TYMED.TYMED_HGLOBAL : TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTREAM
    };
    static bool Has(IDataObject data, short id)
    {
        var format = Format(id);
        return id != 0 && data.QueryGetData(ref format) == 0;
    }

    static byte[]? ReadBytes(IDataObject data, short id, int maximum, int index = -1)
    {
        var format = Format(id, index);
        if (id == 0 || data.QueryGetData(ref format) != 0) return null;
        try
        {
            data.GetData(ref format, out var medium);
            try
            {
                return medium.tymed switch
                {
                    TYMED.TYMED_HGLOBAL => CopyGlobal(medium.unionmember, maximum),
                    TYMED.TYMED_ISTREAM => CopyStream(medium.unionmember, maximum),
                    _ => null
                };
            }
            finally { ReleaseStgMedium(ref medium); }
        }
        catch (COMException) { return null; }
    }

    static byte[]? CopyGlobal(nint handle, int maximum)
    {
        nuint size = GlobalSize(handle);
        if (size == 0 || size > (nuint)maximum) return null;
        nint address = GlobalLock(handle);
        if (address == 0) return null;
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(address, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(handle); }
    }

    static byte[]? CopyStream(nint pointer, int maximum)
    {
        if (pointer == 0 || Marshal.GetObjectForIUnknown(pointer) is not IStream source) return null;
        using var result = new MemoryStream();
        var buffer = new byte[64 * 1024];
        nint count = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            while (result.Length <= maximum)
            {
                int requested = (int)Math.Min(buffer.Length, maximum - result.Length + 1);
                source.Read(buffer, requested, count);
                int read = Marshal.ReadInt32(count);
                if (read < 0 || read > requested) return null;
                if (read == 0) return result.ToArray();
                result.Write(buffer, 0, read);
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(count); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
}
