using System.IO.MemoryMappedFiles;

namespace Halo;

// Same event-driven snapshot pattern as AIEnter, isolated by Halo's data-directory prefix.
internal sealed class RingAppearanceBridge : IDisposable
{
    readonly MemoryMappedFile mapping;
    readonly MemoryMappedViewAccessor view;
    readonly EventWaitHandle changed;
    int sequence;
    internal event Action? Changed;
    internal uint ImportPulse { get; private set; }
    internal RingAppearanceBridge(string prefix)
    {
        mapping = MemoryMappedFile.CreateOrOpen(prefix + ".RingAppearance.v3", 40);
        view = mapping.CreateViewAccessor(0, 40);
        changed = new(false, EventResetMode.AutoReset, prefix + ".RingChanged.v3");
    }
    internal void Publish(UiAppearance value)
    {
        value.Validate(); view.Write(12, ++sequence); Thread.MemoryBarrier();
        view.Write(0, 0x48414C33); view.Write(4, value.TaskbarIconSize); view.Write(8, value.RingThickness);
        view.Write(16, UiAppearance.ColorValue(value.RingColor, 0xFFE6E6E6));
        view.Write(20, value.RingHoverFlow ? 1 : 0); view.Write(24, value.RingFlowSpeed);
        view.Write(28, UiAppearance.ColorValue(value.RingLightColor, 0xFF505050)); view.Write(32, value.RingFlowLength);
        Thread.MemoryBarrier(); view.Write(12, ++sequence); changed.Set();
        Changed?.Invoke();
    }
    internal (int Size, int Thickness) ReadForVerification() => (view.ReadInt32(4), view.ReadInt32(8));
    internal (uint Color, bool Flow, int Speed) ReadStyleForVerification() => (view.ReadUInt32(16), view.ReadInt32(20) != 0, view.ReadInt32(24));
    internal (uint LightColor, int Length) ReadThemeForVerification() => (view.ReadUInt32(28), view.ReadInt32(32));
    internal void PublishImportFeedback(bool? failed)
    {
        uint tick = Math.Max(1u, (uint)(Environment.TickCount64 / 16) & 0x7FFFFFFFu);
        ImportPulse = failed switch { true => tick | 0x80000000u, false => tick, null => 0u };
        view.Write(36, ImportPulse);
        changed.Set();
        Changed?.Invoke();
    }
    internal uint ReadImportFeedbackForVerification() => view.ReadUInt32(36);
    public void Dispose() { view.Dispose(); mapping.Dispose(); changed.Dispose(); }
}
