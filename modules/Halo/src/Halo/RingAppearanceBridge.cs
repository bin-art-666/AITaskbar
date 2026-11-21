namespace Halo;

internal sealed class RingAppearanceBridge
{
    UiAppearance appearance = new();
    internal event Action? Changed;
    internal uint ImportPulse { get; private set; }
    internal void Publish(UiAppearance value)
    {
        value.Validate();
        appearance = value;
        Changed?.Invoke();
    }
    internal (int Size, int Thickness) ReadForVerification() => (appearance.TaskbarIconSize, appearance.RingThickness);
    internal (uint Color, bool Flow, int Speed) ReadStyleForVerification() =>
        (UiAppearance.ColorValue(appearance.RingColor, 0xFFE6E6E6), appearance.RingHoverFlow, appearance.RingFlowSpeed);
    internal (uint LightColor, int Length) ReadThemeForVerification() =>
        (UiAppearance.ColorValue(appearance.RingLightColor, 0xFF505050), appearance.RingFlowLength);
    internal void PublishImportFeedback(bool? failed)
    {
        uint tick = Math.Max(1u, (uint)(Environment.TickCount64 / 16) & 0x7FFFFFFFu);
        ImportPulse = failed switch { true => tick | 0x80000000u, false => tick, null => 0u };
        Changed?.Invoke();
    }
    internal uint ReadImportFeedbackForVerification() => ImportPulse;
}
