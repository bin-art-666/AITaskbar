namespace AITaskbar;

internal sealed class AppAttentionDiagnosticLog
{
    private const int Capacity = 200;
    private readonly Queue<string> entries = new(Capacity);
    private bool enabled;

    internal void SetEnabled(bool value)
    {
        if (value && !enabled) entries.Clear();
        enabled = value;
    }

    internal void Record(string result, nint window, uint processId, AppGroup? group = null)
    {
        if (!enabled) return;
        if (entries.Count == Capacity) entries.Dequeue();
        var app = Path.GetFileName(group?.Executable);
        entries.Enqueue($"{DateTimeOffset.Now:HH:mm:ss.fff} {result} hwnd=0x{window:X} pid={processId}" +
            (string.IsNullOrEmpty(app) ? "" : $" app={app}"));
    }

    internal string GetText() => string.Join(Environment.NewLine, entries);
}
