namespace Pimmer.Core.Services;

public static class ItemMerger
{
    /// <summary>Combines eligible, active and pending items by key. Active wins over pending, pending over eligible.</summary>
    public static List<PimItem> Merge(IEnumerable<PimItem> eligible, IEnumerable<PimItem> active, IEnumerable<PimItem> pending)
    {
        var byKey = new Dictionary<string, PimItem>();
        foreach (var e in eligible)
            byKey.TryAdd(e.Key, e with { State = PimState.Eligible });

        foreach (var p in pending)
        {
            byKey[p.Key] = byKey.TryGetValue(p.Key, out var existing)
                ? existing with { State = PimState.PendingApproval, PendingRequestId = p.PendingRequestId }
                : p with { State = PimState.PendingApproval };
        }

        foreach (var a in active)
        {
            byKey[a.Key] = byKey.TryGetValue(a.Key, out var existing)
                ? existing with { State = PimState.Active, ActiveSince = a.ActiveSince, ActiveUntil = a.ActiveUntil, PendingRequestId = null }
                : a with { State = PimState.Active };
        }

        return [.. byKey.Values];
    }
}
