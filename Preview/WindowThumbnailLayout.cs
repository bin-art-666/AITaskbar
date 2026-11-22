namespace AITaskbar;

internal static class WindowThumbnailLayout
{
    internal const int Padding = 6, Gap = 6, FooterHeight = 28, PagingHeight = 32, AnchorGap = 8;
    private const int MinCardWidth = 82, MaxCards = 5;

    internal sealed record Card(int ImageWidth)
    {
        internal int Width => Math.Max(MinCardWidth, ImageWidth + 2 * Padding);
    }

    internal sealed record Page(int Start, int ImageHeight, Card[] Cards)
    {
        internal int Width => 2 * Padding + Cards.Sum(card => card.Width) + (Cards.Length - 1) * Gap;
        internal int CardHeight => 2 * Padding + ImageHeight + FooterHeight;
        internal int Height(bool paging) => 2 * Padding + CardHeight + (paging ? PagingHeight : 0);
    }

    internal static Page[] Build(IReadOnlyList<double> aspects, int width, int maxWidth, int maxHeight)
    {
        if (maxWidth < MinCardWidth + 2 * Padding || maxHeight <= 4 * Padding + FooterHeight) return [];
        var pages = Paginate(maxHeight - 4 * Padding - FooterHeight);
        if (pages.Length <= 1) return pages;
        int withNavigation = maxHeight - 4 * Padding - FooterHeight - PagingHeight;
        return withNavigation > 0 ? Paginate(withNavigation) : [];

        Page[] Paginate(int maxImageHeight)
        {
            var result = new List<Page>();
            int maxImageWidth = Math.Min(width, maxWidth - 4 * Padding);
            for (int start = 0; start < aspects.Count;)
            {
                Page best = MakePage(start, 1, aspects[start]);
                double widest = aspects[start];
                for (int count = 2; count <= MaxCards && start + count <= aspects.Count; count++)
                {
                    widest = Math.Max(widest, aspects[start + count - 1]);
                    var next = MakePage(start, count, widest);
                    if (next.Width > maxWidth) break;
                    best = next;
                }
                result.Add(best);
                start += best.Cards.Length;
            }
            return result.ToArray();

            Page MakePage(int start, int count, double widest)
            {
                int height = Math.Max(1, Math.Min(maxImageHeight, (int)Math.Floor(maxImageWidth / widest)));
                var cards = new Card[count];
                for (int index = 0; index < count; index++)
                    cards[index] = new(Math.Clamp((int)Math.Round(height * aspects[start + index]), 1, maxImageWidth));
                return new(start, height, cards);
            }
        }
    }
}
