using System.Diagnostics;

namespace AITaskbar;

internal static class WindowOperationLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object sync = new();
    private static string? path;

    internal static void Start(string dataRoot)
    {
        path = Path.Combine(dataRoot, "window-operations.log");
        Write($"session started taskbarPid={Environment.ProcessId}");
    }

    internal static void Record(RunningWindow window, string operation, string result, string? details = null)
    {
        string app = Path.GetFileName(window.IconExecutable ?? window.Executable) ?? "(unknown)";
        uint currentPid = RunningWindows.ProcessIdForWindow(window.Handle);
        bool current = currentPid != 0 && currentPid == window.ProcessId;
        Write($"{operation} {result} app=\"{app}\" hwnd=0x{window.Handle:X} pid={window.ProcessId} " +
            $"currentPid={currentPid} visible={current && RunningWindows.IsVisible(window)} " +
            $"minimized={current && RunningWindows.IsMinimized(window)}" +
            (details is null ? "" : $" {details}"));
    }

    private static void Write(string entry)
    {
        if (path is not { } file) return;
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (new FileInfo(file) is { Exists: true, Length: >= MaxBytes })
                    File.Move(file, file + ".1", overwrite: true);
                File.AppendAllText(file, $"{DateTimeOffset.Now:O} {entry}{Environment.NewLine}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(error);
            }
        }
    }
}
