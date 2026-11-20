namespace Pimmer.Core;

public enum PimKind { EntraRole, Group, AzureResource }

public enum PimState { Eligible, Active, PendingApproval }

public enum GroupAccess { Member, Owner }

public enum Audience { Graph, Arm }

public sealed record AccountRef(string HomeAccountId, string Upn, string ObjectId, string TenantId, string Label, string Color);

public sealed record PolicyInfo(
    TimeSpan MaxDuration,
    bool RequiresJustification,
    bool RequiresTicket,
    bool RequiresMfa,
    string? AuthContextId,
    bool RequiresApproval);

public sealed record PimItem
{
    /// <summary>Stable identity used by favorites, groups and history.</summary>
    public required string Key { get; init; }
    public required string AccountId { get; init; }
    public required string PrincipalId { get; init; }
    public required PimKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    /// <summary>Entra: directoryScopeId; Group: groupId; Azure: ARM scope.</summary>
    public required string ScopeId { get; init; }
    public required string ScopeDisplay { get; init; }
    /// <summary>Name of the scoped target (app registration, Azure resource/RG/subscription); shown as the headline when set.</summary>
    public string? ScopeName { get; init; }
    public GroupAccess? Access { get; init; }
    /// <summary>Entra: role definition id; Group: groupId; Azure: full ARM role definition id.</summary>
    public required string RoleDefinitionId { get; init; }
    public string? EligibilityScheduleId { get; init; }
    public PimState State { get; init; }
    public DateTimeOffset? EligibleUntil { get; init; }
    public DateTimeOffset? ActiveSince { get; init; }
    public DateTimeOffset? ActiveUntil { get; init; }
    public string? PendingRequestId { get; init; }
    public PolicyInfo? Policy { get; init; }
}

public sealed record ActivationRequest(TimeSpan Duration, string? Justification, string? TicketNumber, string? TicketSystem);

public enum OperationState { Running, Succeeded, Pending, Failed }

public sealed record OperationStatus(OperationState State, string Message, DateTimeOffset At);

public sealed record ItemResult(PimItem Item, OperationState State, string Message);

public static class PimKeys
{
    public static string For(PimKind kind, string principalId, string targetId, string scope, GroupAccess? access = null) =>
        string.Join('|', kind, principalId, targetId, scope, access?.ToString() ?? "").ToLowerInvariant();

    public static string KindLabel(PimKind kind) => kind switch
    {
        PimKind.EntraRole => "Entra",
        PimKind.Group => "Group",
        PimKind.AzureResource => "Azure",
        _ => kind.ToString()
    };
}
