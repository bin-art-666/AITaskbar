using Microsoft.UI.Xaml;

namespace AITaskbar;

internal static class QuickControlWheel
{
    internal static void Attach(UIElement surface, Action<int> adjust, Func<bool>? canAdjust = null)
    {
        const int WheelNotch = 120, PointsPerNotch = 2;
        int remainder = 0;
        surface.PointerWheelChanged += (_, args) =>
        {
            var pointer = args.GetCurrentPoint(surface).Properties;
            int delta = pointer.MouseWheelDelta;
            if (pointer.IsHorizontalMouseWheel || delta == 0 || canAdjust?.Invoke() == false) return;
            args.Handled = true;

            if (Math.Sign(delta) != Math.Sign(remainder)) remainder = 0;
            remainder += delta;
            int notches = remainder / WheelNotch;
            remainder %= WheelNotch;
            if (notches != 0) adjust(notches * PointsPerNotch);
        };
    }
}
