using System.IO.MemoryMappedFiles;

namespace AIEnter;

public partial class App
{
    EventWaitHandle? toggleEvent;
    RegisteredWaitHandle? toggleWait;
    MemoryMappedFile? toggleInput;
    MemoryMappedViewAccessor? toggleView;
    long lastToggleAt;
    void InitializeToggleEntry(string prefix)
    {
        toggleEvent = new(false, EventResetMode.AutoReset, prefix + ".Toggle");
        toggleInput = MemoryMappedFile.CreateOrOpen(prefix + ".ToggleInput", 16);
        toggleView = toggleInput.CreateViewAccessor(0, 16);
    }
    void SignalToggle(string[] arguments)
    {
        WriteToggleGesture(arguments, toggleView!);
        toggleEvent!.Set();
    }
    static void WriteToggleGesture(string[] arguments, MemoryMappedViewAccessor view)
    {
        int index = Array.IndexOf(arguments, "--gesture");
        var fields = index >= 0 && index + 1 < arguments.Length ? arguments[index + 1].Split(',') : [];
        uint time = 0, dispatched = 0; int x = 0, y = 0;
        if (fields.Length >= 3 && uint.TryParse(fields[0], out var parsedTime) && int.TryParse(fields[1], out var parsedX) && int.TryParse(fields[2], out var parsedY))
            { time = parsedTime; x = parsedX; y = parsedY; }
        if (fields.Length == 4) uint.TryParse(fields[3], out dispatched);
        view.Write(0, time); view.Write(4, x); view.Write(8, y);
        view.Write(12, dispatched);
        Thread.MemoryBarrier();
    }
    internal async Task ToggleAiWindow(bool fromTaskbar = true)
    {
        long now = Environment.TickCount64;
        if (aiQuitting || now - lastToggleAt < 300) return;
        lastToggleAt = now;
        try
        {
            uint time = fromTaskbar ? toggleView!.ReadUInt32(0) : 0;
            var point = fromTaskbar ? new Native.Point { X = toggleView!.ReadInt32(4), Y = toggleView.ReadInt32(8) } : default;
            uint dispatched = fromTaskbar ? toggleView!.ReadUInt32(12) : 0;
            var timing = time != 0 && dispatched != 0 ? new FirstOpenTiming(time, dispatched) : null;
            timing?.Mark("appReceivedMs");
            await Ai.ToggleFromTaskbar(Native.Monitors().First(m => m.Primary), time, point, timing);
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "ai-launch.log"), $"{DateTimeOffset.Now:O} {error}\n");
        }
    }
    void DisposeToggleEntry()
    {
        toggleWait?.Unregister(null); toggleEvent?.Dispose(); toggleView?.Dispose(); toggleInput?.Dispose();
    }
}
