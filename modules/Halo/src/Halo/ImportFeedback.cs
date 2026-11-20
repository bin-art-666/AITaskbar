namespace Halo;

internal sealed record ImportIssue(string Message, string? BatchId = null);

internal sealed partial class StationWindow
{
    readonly List<ImportIssue> pendingDropFailures = [];

    internal void ReportImportFailure(string detail) => ReportImportFeedback([new(detail)]);

    internal void ReportImportFeedback(IEnumerable<ImportIssue>? failures = null)
    {
        bool batchFailed = false;
        foreach (var issue in failures ?? []) {
            batchFailed = true;
            if (issue.BatchId is { } id) ResolveDropFailure(id);
            if (!pendingDropFailures.Contains(issue)) pendingDropFailures.Add(issue);
        }
        if (pendingDropFailures.Count > 5) pendingDropFailures.RemoveRange(0, pendingDropFailures.Count - 5);
        App.RingBridge?.PublishImportFeedback(batchFailed);
    }

    internal void ResolveDropFailure(string batchId) => pendingDropFailures.RemoveAll(issue => issue.BatchId == batchId);

    internal bool ShowPendingDropFailures()
    {
        if (pendingDropFailures.Count == 0 || ModalActive || DragActive) return false;
        Open();
        string message = "拖入失败\n" + string.Join("\n", pendingDropFailures.Select(issue => issue.Message).Distinct());
        if (folderBrowser is { } browser) browser.ShowImportFailure(message);
        else Notify(message, true);
        pendingDropFailures.Clear();
        App.RingBridge?.PublishImportFeedback(null);
        return true;
    }

    internal static string ImportFailure(Exception error, string? source = null)
    {
        string reason = error.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "读取失败";
        return (source == null ? "" : source + "：") + reason[..Math.Min(reason.Length, 120)];
    }
}
