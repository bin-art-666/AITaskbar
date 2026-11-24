using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace AIEnter;

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IFlyoutOleDropTarget
{
    [PreserveSig] int DragEnter(IDataObject data, uint keys, Native.Point point, ref uint effect);
    [PreserveSig] int DragOver(uint keys, Native.Point point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(IDataObject data, uint keys, Native.Point point, ref uint effect);
}

// Windowed WinUI flyouts can miss XAML drag events. Register their own HWND for shell files.
sealed class FlyoutFileDrop(Func<bool> canAccept, Action<bool> showHint, Func<string[], bool> receive, int maximumFiles = 12) : IFlyoutOleDropTarget, IDisposable
{
    const uint Copy = 1;
    const short FileDropFormat = 15;
    IntPtr window;
    bool hasFiles;

    static FORMATETC FileFormat => new()
    {
        cfFormat = FileDropFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL
    };

    internal int Register(IntPtr hwnd)
    {
        int result = OleInitialize(IntPtr.Zero);
        if (result < 0) return result;
        try
        {
            result = RegisterDragDrop(hwnd, this);
            if (result >= 0) window = hwnd;
            return result;
        }
        finally { if (window == IntPtr.Zero) OleUninitialize(); }
    }

    public int DragEnter(IDataObject data, uint keys, Native.Point point, ref uint effect)
    {
        var format = FileFormat;
        hasFiles = data.QueryGetData(ref format) == 0;
        return DragOver(keys, point, ref effect);
    }

    public int DragOver(uint keys, Native.Point point, ref uint effect)
    {
        effect &= hasFiles && canAccept() ? Copy : 0;
        showHint(effect != 0);
        return 0;
    }

    public int DragLeave()
    {
        hasFiles = false;
        showHint(false);
        return 0;
    }

    public int Drop(IDataObject data, uint keys, Native.Point point, ref uint effect)
    {
        bool accept = hasFiles && (effect & Copy) != 0 && canAccept();
        effect = 0;
        DragLeave();
        if (!accept) return 0;
        try
        {
            var format = FileFormat;
            data.GetData(ref format, out var storage);
            try
            {
                uint count = DragQueryFile(storage.unionmember, uint.MaxValue, null, 0);
                if (count < 1 || count > maximumFiles) return 0;
                var paths = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    uint length = DragQueryFile(storage.unionmember, i, null, 0);
                    if (length == 0) return 0;
                    var path = new StringBuilder((int)length + 1);
                    if (DragQueryFile(storage.unionmember, i, path, (uint)path.Capacity) != length) return 0;
                    paths[i] = path.ToString();
                }
                if (receive(paths)) effect = Copy;
            }
            finally { ReleaseStgMedium(ref storage); }
        }
        catch (COMException) { }
        return 0;
    }

    public void Dispose()
    {
        if (window == IntPtr.Zero) return;
        RevokeDragDrop(window);
        window = IntPtr.Zero;
        OleUninitialize();
    }

    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] static extern void OleUninitialize();
    [DllImport("ole32.dll")] static extern int RegisterDragDrop(IntPtr window, [MarshalAs(UnmanagedType.Interface)] IFlyoutOleDropTarget target);
    [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr window);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint capacity);
}
