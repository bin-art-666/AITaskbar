using System.Drawing;
using System.Runtime.InteropServices;

namespace AITaskbar;

internal static class PackagedAppIcon
{
    private const uint ShellIcon = 0x100, ItemIdList = 0x8;

    internal static Bitmap? Load(string appId)
    {
        if (SHParseDisplayName(@"shell:AppsFolder\" + appId, 0, out nint item, 0, out _) < 0 || item == 0)
            return null;

        try
        {
            SHGetFileInfo(item, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShellIcon | ItemIdList);
            if (info.Icon == 0) return null;
            try
            {
                using var icon = Icon.FromHandle(info.Icon);
                return icon.ToBitmap();
            }
            finally { DestroyIcon(info.Icon); }
        }
        finally { Marshal.FreeCoTaskMem(item); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        internal nint Icon;
        private int iconIndex;
        private uint attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] private string displayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] private string typeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint bindingContext, out nint item, uint mask, out uint attributes);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(nint item, uint attributes, out ShellFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}
