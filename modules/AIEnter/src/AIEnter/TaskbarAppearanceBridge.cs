using System.IO.MemoryMappedFiles;

namespace AIEnter;

internal sealed class TaskbarAppearanceBridge : IDisposable
{
    readonly MemoryMappedFile mapping = MemoryMappedFile.CreateOrOpen("Local\\AITaskbar.AIEnter.TaskbarAppearance.v2", 40);
    readonly MemoryMappedViewAccessor view;
    readonly EventWaitHandle changed = new(false, EventResetMode.AutoReset, "Local\\AITaskbar.AIEnter.TaskbarAppearanceChanged");
    int sequence;
    internal TaskbarAppearanceBridge() => view = mapping.CreateViewAccessor(0, 40);
    internal void Publish(UiAppearance value, bool open)
    {
        value.Validate();
        view.Write(28, ++sequence); // odd: write in progress
        Thread.MemoryBarrier();
        view.Write(0, 0x41494531); view.Write(4, Environment.ProcessId);
        view.Write(8, value.TaskbarIconSize); view.Write(12, value.GlowEnabled ? 1 : 0);
        view.Write(16, value.GlowRange); view.Write(20, value.GlowIntensity); view.Write(24, open ? 1 : 0);
        view.Write(32, value.FlowSpeed); view.Write(36, value.ScreenshotDoubleClick ? 1 : 2);
        Thread.MemoryBarrier();
        view.Write(28, ++sequence); // even: complete snapshot
        changed.Set();
    }
    public void Dispose() { view.Dispose(); mapping.Dispose(); changed.Dispose(); }
}
