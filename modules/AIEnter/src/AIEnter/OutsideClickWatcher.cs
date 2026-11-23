using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace AIEnter;

// Active only while a panel is open. No input is recorded. An optional panel
// handler may consume wheel input strictly within its own interaction region.
internal sealed class OutsideClickWatcher : IDisposable
{
    delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);
    readonly HookProcedure procedure;
    IntPtr hook;
    bool disposed;

    internal OutsideClickWatcher(IntPtr panel, DispatcherQueue dispatcher, Action dismiss, Func<Native.Point,int,bool,bool>? wheel=null, Action<uint, Native.Point>? outsidePress=null, bool consumeOutside=false, Action? insideLeftPress=null)
    {
        bool swallowingRelease = false;
        procedure = (code, message, data) =>
        {
            if (code >= 0 && swallowingRelease && message.ToInt64() is 0x0202 or 0x0205 or 0x0208 or 0x020C)
            { swallowingRelease = false; return new IntPtr(1); }
            if(code>=0&&wheel!=null&&message.ToInt64() is 0x020A or 0x020E)
            {
                var point=Marshal.PtrToStructure<Native.Point>(data);
                if(Native.IsPanelWindow(panel,WindowFromPoint(point)))
                {
                    int delta=unchecked((short)(Marshal.ReadInt32(data,8)>>16));
                    if(wheel(point,delta,message.ToInt64()==0x020E))return new IntPtr(1);
                }
            }
            if (code >= 0 && message.ToInt64() is 0x0201 or 0x0204 or 0x0207 or 0x020B)
            {
                // POINT is the first member of MSLLHOOKSTRUCT; no other input data is needed.
                var point = Marshal.PtrToStructure<Native.Point>(data);
                bool toggleClick = message.ToInt64() == 0x0201 && PanelToggleTargets.Contains(point);
                var target = WindowFromPoint(point);
                if (insideLeftPress is not null && message.ToInt64() == 0x0201 && Native.GetAncestor(target, 2) == panel)
                    dispatcher.TryEnqueue(() => { if (!disposed) insideLeftPress(); });
                if (!toggleClick && !Native.IsPanelWindow(panel, target))
                {
                    outsidePress?.Invoke(unchecked((uint)Marshal.ReadInt32(data, 16)), point);
                    dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => { if (!disposed) dismiss(); });
                    if (consumeOutside) { swallowingRelease = true; return new IntPtr(1); }
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        };
        hook = SetWindowsHookEx(14, procedure, GetModuleHandle(null), 0); // WH_MOUSE_LL
        // Window activation still provides dismissal if Windows cannot install this observer.
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        GC.KeepAlive(procedure);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetWindowsHookEx(int id, HookProcedure procedure, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.Point point);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
