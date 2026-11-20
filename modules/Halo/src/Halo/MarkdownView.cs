using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Halo;

// Native, inert rendering of headings, paragraphs, lists, quotes, fences and inline text.
internal static class MarkdownView
{
    static readonly Regex Inline = new(@"(\[[^\]\r\n]+\]\(https?://[^\s)]+\)|\*\*[^*\r\n]+\*\*|`[^`\r\n]+`|\*[^*\r\n]+\*)", RegexOptions.None, TimeSpan.FromMilliseconds(250));
    internal static FrameworkElement Render(string value, bool code = false)
    {
        var body = new StackPanel { Spacing = 8 };
        if (code) { body.Children.Add(Code(value)); return body; }
        var fenced = new StringBuilder(); bool inCode = false;
        foreach (string line in value.Replace("\r", "").Split('\n')) {
            if (line.TrimStart().StartsWith("```")) {
                if (inCode) { body.Children.Add(Code(fenced.ToString().TrimEnd('\n'))); fenced.Clear(); }
                inCode = !inCode; continue;
            }
            if (inCode) { fenced.AppendLine(line); continue; }
            if (line.Length == 0) continue;
            string content = line; double size = 14; var weight = Microsoft.UI.Text.FontWeights.Normal;
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (heading.Success) { content = heading.Groups[2].Value; size = 25 - heading.Groups[1].Length * 2; weight = Microsoft.UI.Text.FontWeights.SemiBold; }
            else if (Regex.IsMatch(line, @"^\s*[-*+]\s+")) content = "• " + Regex.Replace(line, @"^\s*[-*+]\s+", "");
            else if (line.StartsWith('>')) content = "│ " + line.TrimStart('>', ' ');
            var block = new RichTextBlock { FontSize = size, FontWeight = weight, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            var paragraph = new Paragraph(); int offset = 0;
            foreach (Match match in Inline.Matches(content)) {
                if (match.Index > offset) paragraph.Inlines.Add(new Run { Text = content[offset..match.Index] });
                string token = match.Value;
                if (token.StartsWith('[')) {
                    int split = token.IndexOf("](", StringComparison.Ordinal); var uri = StationStore.WebUri(token[(split + 2)..^1]);
                    if (uri != null) { var link = new Hyperlink { NavigateUri = uri }; link.Inlines.Add(new Run { Text = token[1..split] }); paragraph.Inlines.Add(link); }
                    else paragraph.Inlines.Add(new Run { Text = token });
                } else if (token.StartsWith("**")) { var bold = new Bold(); bold.Inlines.Add(new Run { Text = token[2..^2] }); paragraph.Inlines.Add(bold); }
                else if (token.StartsWith('`')) paragraph.Inlines.Add(new Run { Text = token[1..^1], FontFamily = new FontFamily("Consolas") });
                else { var italic = new Italic(); italic.Inlines.Add(new Run { Text = token[1..^1] }); paragraph.Inlines.Add(italic); }
                offset = match.Index + match.Length;
            }
            if (offset < content.Length) paragraph.Inlines.Add(new Run { Text = content[offset..] });
            block.Blocks.Add(paragraph); body.Children.Add(block);
        }
        if (inCode) body.Children.Add(Code(fenced.ToString()));
        return body;
    }
    static Border Code(string value) => new() { Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(24, 128, 128, 128)),
        Child = new TextBlock { Text = value, FontFamily = new FontFamily("Consolas"), FontSize = 13, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap } };
    internal static string ToHtml(string value)
    {
        var result = new StringBuilder(); bool code = false;
        foreach (string line in value.Replace("\r", "").Split('\n')) {
            if (line.TrimStart().StartsWith("```")) { result.Append(code ? "</code></pre>" : "<pre><code>"); code = !code; continue; }
            if (code) { result.Append(WebUtility.HtmlEncode(line)).Append('\n'); continue; }
            int hashes = line.TakeWhile(character => character == '#').Count();
            string content = line; string tag = "p";
            if (hashes is > 0 and <= 6 && line.Length > hashes && line[hashes] == ' ') { tag = "h" + hashes; content = line[(hashes + 1)..]; }
            else if (Regex.IsMatch(line, @"^\s*[-*+]\s+")) { tag = "li"; content = Regex.Replace(line, @"^\s*[-*+]\s+", ""); }
            var inline = new StringBuilder(); int offset = 0;
            foreach (Match match in Inline.Matches(content)) {
                inline.Append(WebUtility.HtmlEncode(content[offset..match.Index])); string token = match.Value;
                if (token.StartsWith('[')) {
                    int split = token.IndexOf("](", StringComparison.Ordinal); var url = StationStore.WebUri(token[(split + 2)..^1]);
                    inline.Append(url == null ? WebUtility.HtmlEncode(token) : "<a href=\"" + WebUtility.HtmlEncode(url.AbsoluteUri) + "\">" + WebUtility.HtmlEncode(token[1..split]) + "</a>");
                } else if (token.StartsWith("**")) inline.Append("<strong>" + WebUtility.HtmlEncode(token[2..^2]) + "</strong>");
                else inline.Append(token.StartsWith('`') ? "<code>" + WebUtility.HtmlEncode(token[1..^1]) + "</code>" : "<em>" + WebUtility.HtmlEncode(token[1..^1]) + "</em>");
                offset = match.Index + match.Length;
            }
            inline.Append(WebUtility.HtmlEncode(content[offset..]));
            if (tag == "li") result.Append("<ul><li>").Append(inline).Append("</li></ul>");
            else result.Append('<').Append(tag).Append('>').Append(inline).Append("</").Append(tag).Append('>');
        }
        if (code) result.Append("</code></pre>");
        return result.ToString();
    }
}
