using System.Text.Json;
using Pimmer.Core.Http;
using Pimmer.Core.Services;
using static Pimmer.Core.Providers.ProviderHelpers;

namespace Pimmer.Core.Providers;

/// <summary>PIM for Groups (member/owner) via Graph /identityGovernance/privilegedAccess/group.</summary>
public sealed class GroupPimProvider(PimApi api) : IPimProvider
{
    private const string Base = PimApi.GraphBase + "/identityGovernance/privilegedAccess/group";

    public PimKind Kind => PimKind.Group;

    public async Task<PlaneSnapshot> ListAsync(AccountRef account, CancellationToken ct)
    {
        var eligible = GetWithOptionalExpandAsync(api, account, $"{Base}/eligibilityScheduleInstances/filterByCurrentUser(on='principal')", ct, "group");
        var active = GetWithOptionalExpandAsync(api, account, $"{Base}/assignmentScheduleInstances/filterByCurrentUser(on='principal')", ct, "group");
        var pending = api.GetAllAsync(account, Audience.Graph,
            $"{Base}/assignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter={Filter("status eq 'PendingApproval'")}", ct);
        await Task.WhenAll(eligible, active, pending);

        return new PlaneSnapshot(
            [.. eligible.Result.Select(e => Parse(e, account))],
            [.. active.Result.Where(EntraRoleProvider.IsActivated).Select(e => Parse(e, account))],
            [.. pending.Result.Select(e => Parse(e, account) with { PendingRequestId = e.Str("id") })]);
    }

    public static PimItem Parse(JsonElement e, AccountRef account)
    {
        var groupId = e.Str("groupId") ?? "";
        var access = string.Equals(e.Str("accessId"), "owner", StringComparison.OrdinalIgnoreCase) ? GroupAccess.Owner : GroupAccess.Member;
        var group = e.Obj("group");
        return new PimItem
        {
            Key = PimKeys.For(PimKind.Group, account.ObjectId, groupId, groupId, access),
            AccountId = account.HomeAccountId,
            PrincipalId = account.ObjectId,
            Kind = PimKind.Group,
            DisplayName = group?.Str("displayName") ?? groupId,
            Description = group?.Str("description"),
            ScopeId = groupId,
            ScopeDisplay = access == GroupAccess.Owner ? "owner" : "member",
            Access = access,
            RoleDefinitionId = groupId,
            EligibilityScheduleId = e.Str("eligibilityScheduleId"),
            EligibleUntil = e.Date("endDateTime"),
            ActiveSince = e.Date("startDateTime"),
            ActiveUntil = e.Date("endDateTime"),
        };
    }

    private static string AccessId(PimItem item) => item.Access == GroupAccess.Owner ? "owner" : "member";

    public string PolicyKey(PimItem item) => $"group|{item.ScopeId}|{AccessId(item)}";

    public async Task<PolicyInfo> GetPolicyAsync(AccountRef account, PimItem item, CancellationToken ct)
    {
        var filter = Filter($"scopeId eq '{item.ScopeId}' and scopeType eq 'Group' and roleDefinitionId eq '{AccessId(item)}'");
        var result = await api.SendAsync(account, Audience.Graph, HttpMethod.Get,
            $"{PimApi.GraphBase}/policies/roleManagementPolicyAssignments?$filter={filter}&$expand=policy($expand=rules)", ct: ct);
        var assignment = result.Arr("value").FirstOrDefault();
        return PolicyParser.Parse(assignment.ValueKind == JsonValueKind.Object && assignment.Obj("policy") is { } p && p.TryGetProperty("rules", out var rules) ? rules : default);
    }

    public Task<string> ActivateAsync(AccountRef account, PimItem item, ActivationRequest request, CancellationToken ct) =>
        SubmitAsync(account, item, "selfActivate", request, ct);

    public Task<string> ExtendAsync(AccountRef account, PimItem item, ActivationRequest request, CancellationToken ct) =>
        SubmitAsync(account, item, "selfExtend", request, ct);

    public Task<string> DeactivateAsync(AccountRef account, PimItem item, CancellationToken ct) =>
        SubmitAsync(account, item, "selfDeactivate", null, ct);

    public async Task CancelAsync(AccountRef account, PimItem item, CancellationToken ct) =>
        await api.SendAsync(account, Audience.Graph, HttpMethod.Post, $"{Base}/assignmentScheduleRequests/{item.PendingRequestId}/cancel", ct: ct);

    private async Task<string> SubmitAsync(AccountRef account, PimItem item, string action, ActivationRequest? request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["accessId"] = AccessId(item),
            ["principalId"] = item.PrincipalId,
            ["groupId"] = item.ScopeId,
            ["action"] = action,
        };
        if (request is not null)
        {
            body["justification"] = request.Justification ?? "";
            body["scheduleInfo"] = ScheduleInfo(request.Duration);
            if (TicketInfo(request) is { } t) body["ticketInfo"] = t;
        }
        var claims = request is null ? null : PolicyParser.ClaimsFor(item.Policy);
        var response = await api.SendAsync(account, Audience.Graph, HttpMethod.Post, $"{Base}/assignmentScheduleRequests", body, claims, ct);
        return Status(response);
    }
}
