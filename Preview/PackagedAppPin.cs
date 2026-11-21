using System.Runtime.InteropServices;

namespace AITaskbar;

internal static class PackagedAppPin
{
    internal const string SettingsId = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
    internal const string ChatGptId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private static readonly Guid activationManagerClass = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");
    private static readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase)
    {
        [SettingsId] = "设置",
        ["Microsoft.Paint_8wekyb3d8bbwe!App"] = "画图",
        [ChatGptId] = "ChatGPT",
        ["Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"] = "Windows 终端"
    };

    internal static string? Name(string? appId) => appId is null ? null : names.GetValueOrDefault(appId);

    internal static void Launch(string appId)
    {
        var managerType = Type.GetTypeFromCLSID(activationManagerClass, throwOnError: true)!;
        var manager = (IApplicationActivationManager)Activator.CreateInstance(managerType)!;
        try { Marshal.ThrowExceptionForHR(manager.ActivateApplication(appId, null, 0, out _)); }
        finally { Marshal.ReleaseComObject(manager); }
    }

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
    }
}
