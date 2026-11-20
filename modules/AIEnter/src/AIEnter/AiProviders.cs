namespace AIEnter;

internal sealed record AiProvider(string Id, string Name, string Url);

internal static partial class AiPolicy
{
    internal static IEnumerable<string> AvailableProviders(AiOptions value) => BuiltInProviders.Concat((value.AddedProviders ?? []).Select(p => p.Id));
    internal static AiOptions Upgrade(AiOptions value)
    {
        if (value.Version is < 1 or > 2) throw new NotSupportedException("AI 设置来自不支持的版本，已停止写入。");
        ValidateAddedProviders(value);
        var added = (value.AddedProviders ?? []).ToList();
        string selected = value.Provider;
        if (!string.IsNullOrEmpty(value.CustomUrl))
        {
            string url = Https(value.CustomUrl);
            if (!added.Any(p => p.Id == "ai-legacy")) added.Add(new("ai-legacy", value.CustomName, url));
            if (selected == "custom") selected = "ai-legacy";
        }
        if (selected == "custom") throw new InvalidDataException("请为自定义 AI 提供 HTTPS 网址。");
        var order = value.ProviderOrder?.Select(id => id == "custom" ? "ai-legacy" : id)
            .Where(id => id != "ai-legacy" || added.Any(p => p.Id == id)).ToArray();
        string preferred = value.DefaultProvider ?? (value.Version == 1 ? selected : UiDefaults.Provider);
        if (preferred == "custom") preferred = added.Any(p => p.Id == "ai-legacy") ? "ai-legacy" : UiDefaults.Provider;
        return value with { Version = 2, AddedProviders = added.ToArray(), Provider = selected,
            DefaultProvider = preferred, ProviderOrder = order, CustomUrl = "" };
    }
    static void ValidateAddedProviders(AiOptions value)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.AddedProviders ?? [])
        {
            if (item == null || item.Id == null || !item.Id.StartsWith("ai-", StringComparison.Ordinal)
                || item.Id.Length > 80 || item.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || !ids.Add(item.Id)
                || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 40 || item.Name.Any(char.IsControl) || item.Url == null)
                throw new InvalidDataException("AI 名称或标识无效，名称需为 1～40 个字符。");
            _ = Https(item.Url);
        }
    }
    internal static AiOptions WithProviderSettings(AiOptions current, AiProvider[] added, string[] order, string preferred)
    {
        var result = current with { AddedProviders = added, CustomUrl = "" };
        var available = AvailableProviders(result).ToHashSet(StringComparer.Ordinal);
        string fallback = available.Contains(preferred) ? preferred : UiDefaults.Provider;
        return result with { DefaultProvider = fallback, Provider = available.Contains(current.Provider) ? current.Provider : fallback,
            ProviderOrder = order.Where(available.Contains).ToArray() };
    }
}