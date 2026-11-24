using Pimmer.Core.Storage;

namespace Pimmer.Core.Services;

/// <summary>Decides which justification the activation dialog prefills.</summary>
public static class MessageResolver
{
    /// <summary>Strictest justification length we enforce in the UI.</summary>
    public const int MaxLength = 500;

    /// <summary>
    /// Group activation: the group's default, else its last message.
    /// Single item: the item's default, else its last message, else the last message used overall.
    /// Ad-hoc multi-select: empty.
    /// </summary>
    public static string Resolve(IReadOnlyList<PimItem> items, PimGroup? group, StoreData data)
    {
        if (group is not null) return Pick(group.DefaultJustification, group.LastJustification) ?? "";
        if (items.Count != 1) return "";
        data.ItemMessages.TryGetValue(items[0].Key, out var m);
        return Pick(m?.Default, m?.Last, data.RecentJustifications.FirstOrDefault()) ?? "";
    }

    private static string? Pick(params string?[] candidates) => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
