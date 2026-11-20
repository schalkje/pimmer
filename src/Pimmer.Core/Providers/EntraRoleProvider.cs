using System.Text.Json;
using Pimmer.Core.Http;
using Pimmer.Core.Services;
using static Pimmer.Core.Providers.ProviderHelpers;

namespace Pimmer.Core.Providers;

/// <summary>Microsoft Entra directory roles via Graph /roleManagement/directory.</summary>
public sealed class EntraRoleProvider(PimApi api) : IPimProvider
{
    private const string Base = PimApi.GraphBase + "/roleManagement/directory";

    public PimKind Kind => PimKind.EntraRole;

    public async Task<PlaneSnapshot> ListAsync(AccountRef account, CancellationToken ct)
    {
        string[] expands = ["roleDefinition,directoryScope", "roleDefinition"];
        var eligible = GetWithOptionalExpandAsync(api, account, $"{Base}/roleEligibilityScheduleInstances/filterByCurrentUser(on='principal')", ct, expands);
        var active = GetWithOptionalExpandAsync(api, account, $"{Base}/roleAssignmentScheduleInstances/filterByCurrentUser(on='principal')", ct, expands);
        var pending = GetWithOptionalExpandAsync(api, account,
            $"{Base}/roleAssignmentScheduleRequests/filterByCurrentUser(on='principal')?$filter={Filter("status eq 'PendingApproval'")}", ct, expands);
        await Task.WhenAll(eligible, active, pending);

        return new PlaneSnapshot(
            [.. eligible.Result.Select(e => Parse(e, account))],
            [.. active.Result.Where(IsActivated).Select(e => Parse(e, account))],
            [.. pending.Result.Select(e => Parse(e, account) with { PendingRequestId = e.Str("id") })]);
    }

    public static bool IsActivated(JsonElement e) =>
        string.Equals(e.Str("assignmentType"), "Activated", StringComparison.OrdinalIgnoreCase);

    public static PimItem Parse(JsonElement e, AccountRef account)
    {
        var roleId = e.Str("roleDefinitionId") ?? "";
        var scope = e.Str("directoryScopeId") ?? "/";
        var def = e.Obj("roleDefinition");
        var (scopeName, scopeDisplay) = DescribeScope(scope, e.Obj("directoryScope"));
        return new PimItem
        {
            Key = PimKeys.For(PimKind.EntraRole, account.ObjectId, roleId, scope),
            AccountId = account.HomeAccountId,
            PrincipalId = account.ObjectId,
            Kind = PimKind.EntraRole,
            DisplayName = def?.Str("displayName") ?? roleId,
            Description = def?.Str("description"),
            ScopeId = scope,
            ScopeName = scopeName,
            ScopeDisplay = scopeDisplay,
            RoleDefinitionId = roleId,
            EligibilityScheduleId = e.Str("roleEligibilityScheduleId"),
            EligibleUntil = e.Date("endDateTime"),
            ActiveSince = e.Date("startDateTime"),
            ActiveUntil = e.Date("endDateTime"),
        };
    }

    /// <summary>Name and label of the role's scope, using the expanded directoryScope (application, service principal, admin unit…).</summary>
    public static (string? Name, string Display) DescribeScope(string scope, JsonElement? directoryScope)
    {
        if (scope == "/") return (null, "Directory");
        var name = directoryScope?.Str("displayName");
        var type = directoryScope?.Str("@odata.type")?.Split('.').Last();
        var label = type switch
        {
            "application" => "App registration",
            "servicePrincipal" => "Enterprise app",
            "administrativeUnit" => "Admin unit",
            "group" => "Group",
            "user" => "User",
            _ when scope.StartsWith("/administrativeUnits/", StringComparison.OrdinalIgnoreCase) => "Admin unit",
            _ => "Scope"
        };
        return name is null ? (null, $"{label} {scope.Split('/').Last()}") : (name, $"{label}: {name}");
    }

    public string PolicyKey(PimItem item) => $"entra|{item.RoleDefinitionId}";

    public async Task<PolicyInfo> GetPolicyAsync(AccountRef account, PimItem item, CancellationToken ct)
    {
        var filter = Filter($"scopeId eq '/' and scopeType eq 'DirectoryRole' and roleDefinitionId eq '{item.RoleDefinitionId}'");
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
        await api.SendAsync(account, Audience.Graph, HttpMethod.Post, $"{Base}/roleAssignmentScheduleRequests/{item.PendingRequestId}/cancel", ct: ct);

    private async Task<string> SubmitAsync(AccountRef account, PimItem item, string action, ActivationRequest? request, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["principalId"] = item.PrincipalId,
            ["roleDefinitionId"] = item.RoleDefinitionId,
            ["directoryScopeId"] = item.ScopeId,
        };
        if (request is not null)
        {
            body["justification"] = request.Justification ?? "";
            body["scheduleInfo"] = ScheduleInfo(request.Duration);
            if (TicketInfo(request) is { } t) body["ticketInfo"] = t;
        }
        var claims = request is null ? null : PolicyParser.ClaimsFor(item.Policy);
        var response = await api.SendAsync(account, Audience.Graph, HttpMethod.Post, $"{Base}/roleAssignmentScheduleRequests", body, claims, ct);
        return Status(response);
    }
}
