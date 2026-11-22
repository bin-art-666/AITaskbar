using System.Runtime.InteropServices;
using System.Text;

namespace AITaskbar;

internal static class BrowserRecentDocuments
{
    private const int Limit = 5, Candidates = 24;
    private const uint Recent = 0, FileSystemPath = 0x80058000, RawLinkPath = 4;
    private static readonly Guid documentListsClass = new("86BEC222-30F2-47E0-9F25-60D11CD75C28");
    private static readonly Guid unknownId = new("00000000-0000-0000-C000-000000000046");
    private static readonly HashSet<string> supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".html", ".htm", ".mhtml", ".txt", ".png", ".jpg", ".jpeg", ".gif", ".webp"
    };

    internal static string[] Read(AppGroup group, RecentBrowser browser)
    {
        string baseId = browser == RecentBrowser.Chrome ? "Chrome" : "MSEdge";
        string[] ids = group.Windows.Select(window => RunningWindows.WindowAppId(window.Handle))
            .OfType<string>().Where(id => id.Equals(baseId, StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith(baseId + ".", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        string? appId = ids.Length == 1 ? ids[0] : null;
        if (ids.Length == 0 && IsMachineBrowser(group.Executable, browser)) appId = baseId;
        return appId is null ? [] : ReadLocalFiles(appId);
    }

    private static bool IsMachineBrowser(string? executable, RecentBrowser browser)
    {
        string relativePath = browser == RecentBrowser.Chrome
            ? @"Google\Chrome\Application\chrome.exe" : @"Microsoft\Edge\Application\msedge.exe";
        return IsUnder(Environment.SpecialFolder.ProgramFiles) || IsUnder(Environment.SpecialFolder.ProgramFilesX86);

        bool IsUnder(Environment.SpecialFolder folder) => StringComparer.OrdinalIgnoreCase.Equals(executable,
            Path.Combine(Environment.GetFolderPath(folder), relativePath));
    }

    internal static bool IsAvailable(string path) =>
        path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' &&
        supportedExtensions.Contains(Path.GetExtension(path)) && File.Exists(path);

    internal static string[] ReadLocalFiles(string appId)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var type = Type.GetTypeFromCLSID(documentListsClass, throwOnError: true)!;
            var lists = (IApplicationDocumentLists)Activator.CreateInstance(type)!;
            try
            {
                var iid = typeof(IObjectArray).GUID;
                if (lists.SetAppID(appId) < 0 || lists.GetList(Recent, Candidates, in iid, out var objects) < 0 || objects is null)
                    return [];
                try
                {
                    if (objects.GetCount(out uint count) < 0) return [];
                    for (uint index = 0; index < Math.Min(count, Candidates) && files.Count < Limit; index++)
                    {
                        if (objects.GetAt(index, in unknownId, out var entry) < 0 || entry is null) continue;
                        try
                        {
                            if (LocalPath(entry) is { } path && IsAvailable(path) && seen.Add(path))
                                files.Add(path);
                        }
                        finally { Marshal.ReleaseComObject(entry); }
                    }
                }
                finally { Marshal.ReleaseComObject(objects); }
            }
            finally { Marshal.ReleaseComObject(lists); }
        }
        catch (COMException) { }
        return files.ToArray();
    }

    private static string? LocalPath(object entry)
    {
        if (entry is IShellItem item && item.GetDisplayName(FileSystemPath, out var path) >= 0)
            return path;
        if (entry is IShellLinkW link)
        {
            var buffer = new StringBuilder(32768);
            if (link.GetPath(buffer, buffer.Capacity, 0, RawLinkPath) == 0) return buffer.ToString();
        }
        return null;
    }

    [ComImport, Guid("3C594F9F-9F30-47A1-979A-C9E83D3D0A06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationDocumentLists
    {
        [PreserveSig] int SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int GetList(uint listType, uint count, in Guid iid, out IObjectArray? items);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, in Guid handler, in Guid iid, out nint result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint format, [MarshalAs(UnmanagedType.LPWStr)] out string path);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, nint fileData, uint flags);
    }
}
