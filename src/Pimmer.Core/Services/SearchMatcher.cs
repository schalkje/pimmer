namespace Pimmer.Core.Services;

public static class SearchMatcher
{
    /// <summary>Every whitespace-separated query token must occur (case-insensitive) in at least one field.</summary>
    public static bool Matches(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.All(t => fields.Any(f => f is not null && f.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool Matches(string? query, PimItem item, string? accountLabel) =>
        Matches(query, item.DisplayName, item.ScopeName, item.ScopeDisplay, item.ScopeId, item.Description, accountLabel,
            PimKeys.KindLabel(item.Kind), item.Access?.ToString());
}
