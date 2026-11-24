namespace Pimmer.Core.Storage;

public sealed class PimmerSettings
{
    public const string GraphPowerShellClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
    public const string AzurePowerShellClientId = "1950a258-227b-4e31-a9cf-717495945fc2";

    /// <summary>Tenant id or domain; "organizations" signs in to the account's home tenant.</summary>
    public string TenantId { get; set; } = "organizations";
    public string GraphClientId { get; set; } = GraphPowerShellClientId;
    public string ArmClientId { get; set; } = AzurePowerShellClientId;
    public bool UseBroker { get; set; } = true;
    public int DefaultDurationMinutes { get; set; } = 60;
    public int RefreshSeconds { get; set; } = 60;
    public int ExpiryWarningMinutes { get; set; } = 10;
}

public sealed class PimGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Name { get; set; } = "";
    public List<string> ItemKeys { get; set; } = [];
    public int? DefaultDurationMinutes { get; set; }
    public string? DefaultJustification { get; set; }
    /// <summary>Message last used when activating this group as a whole.</summary>
    public string? LastJustification { get; set; }
}

/// <summary>Messages remembered for one PIM object, keyed by <see cref="PimItem.Key"/>.</summary>
public sealed class ItemMessages
{
    public string? Last { get; set; }
    public string? Default { get; set; }
    /// <summary>Duration (clamped to policy) of the last activation of this item.</summary>
    public int? LastDurationMinutes { get; set; }
}

public sealed record HistoryEntry(
    string ItemKey,
    string AccountId,
    string DisplayName,
    string ScopeDisplay,
    PimKind Kind,
    string Action,
    int DurationMinutes,
    string? Justification,
    string? Ticket,
    string Result,
    DateTimeOffset At);

public sealed class StoreData
{
    public PimmerSettings Settings { get; set; } = new();
    public List<AccountRef> Accounts { get; set; } = [];
    public List<string> Favorites { get; set; } = [];
    public List<PimGroup> Groups { get; set; } = [];
    public List<HistoryEntry> History { get; set; } = [];
    /// <summary>Saved messages, most recently used first. Every used message is added automatically.</summary>
    public List<string> RecentJustifications { get; set; } = [];
    public Dictionary<string, ItemMessages> ItemMessages { get; set; } = [];
    public int? LastDurationMinutes { get; set; }
}

public sealed record CachedPolicy(PolicyInfo Policy, DateTimeOffset FetchedAt);

public sealed class CacheData
{
    /// <summary>Last known items per "{accountId}|{kind}" bucket, shown immediately on startup.</summary>
    public Dictionary<string, List<PimItem>> Buckets { get; set; } = [];
    public Dictionary<string, CachedPolicy> Policies { get; set; } = [];
    public DateTimeOffset? RefreshedAt { get; set; }
}
