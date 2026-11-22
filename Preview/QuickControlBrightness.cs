using System.Runtime.InteropServices;

namespace AITaskbar;

internal static class QuickControlBrightness
{
    internal sealed record Display(nint Monitor, string? Instance, int Level, uint Min = 0, uint Max = 100, byte[]? Steps = null);

    internal static Display? Read(nint monitor, Display? previous = null)
    {
        if (monitor == 0) return null;
        if (ReadBuiltIn(monitor) is { } panel) return panel;
        return WithPhysicalMonitor<Display?>(monitor, previous is not { Instance: null } || previous.Monitor != monitor, handle =>
            GetMonitorBrightness(handle, out uint min, out uint value, out uint max) && max > min
                ? new(monitor, null, (int)Math.Clamp(Math.Round((value - (double)min) * 100 / (max - min)), 0, 100), min, max)
                : null);
    }

    internal static int? Set(Display display, int level)
    {
        level = Math.Clamp(level, 0, 100);
        if (display.Instance is { } instance)
        {
            if (System.Windows.Forms.Screen.AllScreens.Length != 1) return null;
            int requested = display.Steps is { Length: > 0 } steps
                ? steps.MinBy(step => Math.Abs(step - level)) : level;
            bool invoked = WithWmi("WmiMonitorBrightnessMethods", item =>
            {
                if (!WmiProperty<bool>(item, "Active") || !StringComparer.OrdinalIgnoreCase.Equals(
                    WmiProperty<string>(item, "InstanceName"), instance)) return null;
                return WriteBuiltIn(item, (byte)requested) ? "yes" : "no";
            }) == "yes";
            if (!invoked) return null;
            // Some display providers expose no output parameters. Confirm the actual level either way.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt != 0) Thread.Sleep(50);
                if (ReadBuiltIn(display.Monitor, instance)?.Level == requested) return requested;
            }
            return null;
        }
        return WithPhysicalMonitor<int?>(display.Monitor, verifyCapabilities: false, handle =>
            SetMonitorBrightness(handle, (uint)Math.Round(display.Min + (display.Max - display.Min) * level / 100d))
                ? level : null);
    }

    private static Display? ReadBuiltIn(nint monitor, string? expectedInstance = null)
    {
        // WMI identifies built-in panels by instance, not by HMONITOR. Avoid
        // using an unassociated instance when the taskbar can be on another display.
        if (System.Windows.Forms.Screen.AllScreens.Length != 1) return null;
        return WithWmi("WmiMonitorBrightness", item =>
        {
            if (!WmiProperty<bool>(item, "Active")) return null;
            string instance = WmiProperty<string>(item, "InstanceName");
            if (expectedInstance is not null && !StringComparer.OrdinalIgnoreCase.Equals(instance, expectedInstance))
                return null;
            var levels = ReadWmiProperty(item, "Level") as Array;
            byte[]? steps = levels?.Cast<object>().Select(value => Convert.ToByte(value)).ToArray();
            return new Display(monitor, instance, WmiProperty<byte>(item, "CurrentBrightness"), Steps: steps);
        });
    }

    private static T WmiProperty<T>(object item, string name) =>
        (T)Convert.ChangeType(ReadWmiProperty(item, name), typeof(T), System.Globalization.CultureInfo.InvariantCulture);

    private static object? ReadWmiProperty(object item, string name)
    {
        object properties = ((dynamic)item).Properties_;
        try
        {
            object property = ((dynamic)properties).Item(name);
            try { return ((dynamic)property).Value; }
            finally { Marshal.ReleaseComObject(property); }
        }
        finally { Marshal.ReleaseComObject(properties); }
    }

    private static bool WriteBuiltIn(dynamic item, byte level)
    {
        object? methods = null, method = null, definition = null, input = null, properties = null,
            timeout = null, brightness = null, output = null;
        try
        {
            methods = item.Methods_;
            method = ((dynamic)methods).Item("WmiSetBrightness");
            definition = ((dynamic)method).InParameters;
            input = ((dynamic)definition).SpawnInstance_();
            properties = ((dynamic)input).Properties_;
            timeout = ((dynamic)properties).Item("Timeout");
            brightness = ((dynamic)properties).Item("Brightness");
            ((dynamic)timeout).Value = 0u;
            ((dynamic)brightness).Value = level;
            output = item.ExecMethod_("WmiSetBrightness", input);
            return output is null || ReadWmiProperty(output, "ReturnValue") is not { } status ||
                Convert.ToUInt32(status) == 0;
        }
        finally
        {
            foreach (var resource in new[] { output, brightness, timeout, properties, input, definition, method, methods })
                if (resource is not null) Marshal.ReleaseComObject(resource);
        }
    }

    private static T? WithWmi<T>(string className, Func<dynamic, T?> inspect) where T : class
    {
        object? locator = null, service = null, collection = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
            if (type is null || (locator = Activator.CreateInstance(type)) is null) return null;
            service = ((dynamic)locator).ConnectServer(".", @"root\wmi");
            collection = ((dynamic)service).ExecQuery("SELECT * FROM " + className);
            foreach (object item in (System.Collections.IEnumerable)collection)
            {
                try { if (inspect((dynamic)item) is T result) return result; }
                finally { Marshal.ReleaseComObject(item); }
            }
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or
            Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally
        {
            foreach (var item in new[] { collection, service, locator })
                if (item is not null) Marshal.ReleaseComObject(item);
        }
        return null;
    }

    private static T? WithPhysicalMonitor<T>(nint monitor, bool verifyCapabilities, Func<nint, T> action)
    {
        if (monitor == 0 || !GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) || count != 1)
            return default;
        var monitors = new PhysicalMonitor[1];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors)) return default;
        try
        {
            var handle = monitors[0].Handle;
            return !verifyCapabilities || GetMonitorCapabilities(handle, out uint capabilities, out _) && (capabilities & 2) != 0
                ? action(handle) : default;
        }
        finally { DestroyPhysicalMonitors(count, monitors); }
    }

    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] PhysicalMonitor[] physicalMonitors);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] physicalMonitors);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorCapabilities(nint monitor, out uint capabilities, out uint colorTemperatures);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(nint monitor, out uint min, out uint level, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetMonitorBrightness(nint monitor, uint level);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
}
