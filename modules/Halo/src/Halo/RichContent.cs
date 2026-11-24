using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;

namespace Halo;

// Convert a bounded fragment into inert Markdown. No browser, scripts, styles or remote resources.
internal static partial class RichContent
{
    static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    static Regex Pattern(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline, MatchTimeout);
    internal static bool IsSingleAnchor(string html) => Pattern(@"^\s*<a\b[^>]*>.*?</a>\s*$").IsMatch(Fragment(html));
    internal static string? SingleImageSource(string html)
    {
        try
        {
            var match = Pattern("""^\s*(?:<a\b[^>]*>\s*)?<img\b[^>]*?\s+src\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))[^>]*>\s*(?:</a\s*>)?\s*$""").Match(Fragment(html));
            return match.Success ? WebUtility.HtmlDecode(match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value) : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or RegexMatchTimeoutException) { return null; }
    }
    static string Fragment(string html) {
        if (html.Length > 2_000_000) throw new InvalidDataException("网页摘录过大，请缩小选区。");
        try { return HtmlFormatHelper.GetStaticFragment(html); }
        catch (ArgumentException) { if (html.TrimStart().StartsWith('<')) return html; throw new InvalidDataException("网页摘录格式无效。"); }
    }
    internal static StationInput FromHtml(string html, string title = "", string fallback = "", string sourceUrl = "")
    {
        string source = Pattern(@"(?:^|\r?\n)SourceURL:([^\r\n]+)").Match(html).Groups[1].Value.Trim();
        if (StationStore.WebUri(source) == null) source = "";
        if (source.Length == 0 && StationStore.WebUri(sourceUrl) is {} providedSource) source = providedSource.AbsoluteUri;
        string fragment = Fragment(html);
        fragment = Pattern(@"<!--.*?-->|<(script|style|iframe|object|svg)\b[^>]*>.*?(?:</\1\s*>|$)").Replace(fragment, "");
        var output = new StringBuilder(); var links = new Stack<string>(); bool pre = false;
        string Attribute(string tag, string name) {
            var match = Pattern("\\b" + name + "\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))").Match(tag);
            return WebUtility.HtmlDecode(match.Groups.Cast<Group>().Skip(1).FirstOrDefault(group => group.Success)?.Value ?? "");
        }
        string Url(string value) => StationStore.WebUri(value)?.AbsoluteUri
            ?? (source.Length > 0 && Uri.TryCreate(new Uri(source), value, out var relative) && StationStore.WebUri(relative.AbsoluteUri) != null ? relative.AbsoluteUri : "");
        foreach (Match token in Pattern(@"<[^>]*>|[^<]+|<").Matches(fragment)) {
            string value = token.Value;
            if (!value.StartsWith('<')) { output.Append(pre ? WebUtility.HtmlDecode(value) : Pattern(@"\s+").Replace(WebUtility.HtmlDecode(value), " ")); continue; }
            var tag = Pattern(@"^<\s*(/?)\s*([a-z0-9]+)").Match(value); if (!tag.Success) continue;
            string name = tag.Groups[2].Value.ToLowerInvariant(); bool end = tag.Groups[1].Length > 0;
            switch (name) {
                case "br": output.AppendLine(); break;
                case "p": case "div": case "section": case "article": case "ul": case "ol": output.Append("\n\n"); break;
                case "li": output.Append(end ? "\n" : "\n- "); break;
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6": output.Append(end ? "\n\n" : "\n\n" + new string('#', name[1] - '0') + " "); break;
                case "strong": case "b": output.Append("**"); break;
                case "em": case "i": output.Append('*'); break;
                case "pre": output.Append(end ? "\n```\n" : "\n```\n"); pre = !end; break;
                case "code": if (!pre) output.Append('`'); break;
                case "a":
                    if (!end) { string url = Url(Attribute(value, "href")); links.Push(url); if (url.Length > 0) output.Append('['); }
                    else if (links.TryPop(out var url) && url.Length > 0) output.Append("](" + url.Replace("(", "%28").Replace(")", "%29") + ")");
                    break;
            }
        }
        string text = output.ToString().Replace("\r", "").Trim();
        if (string.IsNullOrWhiteSpace(text)) text = fallback;
        if (string.IsNullOrWhiteSpace(text) || text.Length > StationStore.MaximumText) throw new InvalidDataException("网页摘录不能为空，最多 20,000 字符。");
        return new(StationKind.Text, text, title, TextStyle: StationTextStyle.Excerpt, SourceUrl: source);
    }
    internal static string[]? LinkLines(string text) {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || lines.Length > StationStore.MaximumCapacity || lines.Any(line => StationStore.WebUri(line) == null)) return null;
        return lines.Select(line => StationStore.WebUri(line)!.AbsoluteUri).Distinct().ToArray();
    }
    internal static string Label(StationItem item) => item.Kind == StationKind.Text ? item.TextStyle switch {
        StationTextStyle.Markdown => "MD", StationTextStyle.Code => "代码", StationTextStyle.Excerpt => "摘录", _ => ""
    } : item.Kind == StationKind.File && IsShortcut(item.Value) ? "快捷" : "";
    internal static string StyleName(StationTextStyle style) => style switch { StationTextStyle.Markdown => "Markdown", StationTextStyle.Code => "代码", StationTextStyle.Excerpt => "网页摘录", _ => "普通文字" };
    internal static bool IsShortcut(string path) => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase);
}
