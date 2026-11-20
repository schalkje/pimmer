using System.Text.Json;
using Pimmer.Core.Http;
using Pimmer.Core.Services;
using static Pimmer.Core.Providers.ProviderHelpers;

namespace Pimmer.Core.Providers;

/// <summary>Azure resource roles via ARM Microsoft.Authorization (management group, subscription, RG, resource).</summary>
public sealed class AzureResourceProvider(PimApi api) : IPimProvider
{
    public const string ApiVersion = "2020-10-01";
    private const string Provider = "/providers/Microsoft.Authorization";

    public PimKind Kind => PimKind.AzureResource;

    public async Task<PlaneSnapshot> ListAsync(AccountRef account, CancellationToken ct)
    {
        var subscriptions = GetSubscriptionNamesAsync(account, ct);
        var eligible = ListAsTargetAsync(account, "roleEligibilityScheduleInstances", subscriptions, ct);
        var active = ListAsTargetAsync(account, "roleAssignmentScheduleInstances", subscriptions, ct);
        var pending = ListAsTargetAsync(account, "roleAssignmentScheduleRequests", subscriptions, ct);
        await Task.WhenAll(eligible, active, pending);
        var subs = await subscriptions;

        return new PlaneSnapshot(
            [.. eligible.Result.Select(e => Parse(e, account, subs))],
            [.. active.Result.Where(e => string.Equals(e.Obj("properties")?.Str("assignmentType"), "Activated", StringComparison.OrdinalIgnoreCase))
                .Select(e => Parse(e, account, subs))],
            [.. pending.Result.Where(e => string.Equals(e.Obj("properties")?.Str("status"), "PendingApproval", StringComparison.OrdinalIgnoreCase))
                .Select(e => Parse(e, account, subs) with { PendingRequestId = e.Str("id") })]);
    }

    /// <summary>Lists at tenant root with asTarget(); falls back to per-subscription listing if root is not allowed.</summary>
    private async Task<List<JsonElement>> ListAsTargetAsync(AccountRef account, string collection, Task<Dictionary<string, string>> subscriptions, CancellationToken ct)
    {
        var query = $"?api-version={ApiVersion}&$filter={Filter("asTarget()")}";
        try
        {
            return await api.GetAllAsync(account, Audience.Arm, $"{PimApi.ArmBase}{Provider}/{collection}{query}", ct);
        }
        catch (PimApiException ex) when (ex.StatusCode is 400 or 403 or 404)
        {
            var subs = await subscriptions;
            var results = new List<JsonElement>();
            using var gate = new SemaphoreSlim(4);
            var tasks = subs.Keys.Select(async subId =>
            {
                await gate.WaitAsync(ct);
                try { return await api.GetAllAsync(account, Audience.Arm, $"{PimApi.ArmBase}/subscriptions/{subId}{Provider}/{collection}{query}", ct); }
                catch (PimApiException) { return []; }
                finally { gate.Release(); }
            });
            foreach (var r in await Task.WhenAll(tasks)) results.AddRange(r);
            return [.. results.DistinctBy(e => e.Str("id"))];
        }
    }

    private async Task<Dictionary<string, string>> GetSubscriptionNamesAsync(AccountRef account, CancellationToken ct)
    {
        try
        {
            var subs = await api.GetAllAsync(account, Audience.Arm, $"{PimApi.ArmBase}/subscriptions?api-version=2022-12-01", ct);
            return subs.Where(s => s.Str("subscriptionId") is not null)
                .ToDictionary(s => s.Str("subscriptionId")!.ToLowerInvariant(), s => s.Str("displayName") ?? s.Str("subscriptionId")!);
        }
        catch (PimApiException)
        {
            return [];
        }
    }

    public static PimItem Parse(JsonElement e, AccountRef account, IReadOnlyDictionary<string, string> subscriptionNames)
    {
        var p = e.Obj("properties") ?? default;
        var scope = p.Str("scope") ?? "";
        var roleDefinitionId = p.Str("roleDefinitionId") ?? "";
        var expanded = p.Obj("expandedProperties");
        var roleGuid = roleDefinitionId.Split('/').LastOrDefault() ?? roleDefinitionId;
        return new PimItem
        {
            Key = PimKeys.For(PimKind.AzureResource, account.ObjectId, roleGuid, scope),
            AccountId = account.HomeAccountId,
            PrincipalId = account.ObjectId,
            Kind = PimKind.AzureResource,
            DisplayName = expanded?.Obj("roleDefinition")?.Str("displayName") ?? roleGuid,
            ScopeId = scope,
            ScopeName = ScopeName(scope, expanded?.Obj("scope")?.Str("displayName"), subscriptionNames),
            ScopeDisplay = ScopeDisplay(scope, expanded?.Obj("scope")?.Str("displayName"), subscriptionNames),
            RoleDefinitionId = roleDefinitionId,
            EligibilityScheduleId = p.Str("roleEligibilityScheduleId") ?? p.Str("linkedRoleEligibilityScheduleId"),
            EligibleUntil = p.Date("endDateTime"),
            ActiveSince = p.Date("startDateTime"),
            ActiveUntil = p.Date("endDateTime"),
        };
    }

    /// <summary>The most specific name of an ARM scope: resource, resource group, subscription or management group.</summary>
    public static string? ScopeName(string scope, string? scopeName, IReadOnlyDictionary<string, string> subscriptionNames)
    {
        var parts = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        if (scope.Contains("/managementGroups/", StringComparison.OrdinalIgnoreCase)) return scopeName ?? parts[^1];
        if (parts.Length == 2)
            return subscriptionNames.TryGetValue(parts[1].ToLowerInvariant(), out var n) ? n : scopeName ?? parts[1];
        return parts[^1];
    }

    public static string ScopeDisplay(string scope, string? scopeName, IReadOnlyDictionary<string, string> subscriptionNames)
    {
        var parts = scope.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string Seg(string name) => parts.SkipWhile(x => !x.Equals(name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault() ?? "";

        if (scope.Contains("/managementGroups/", StringComparison.OrdinalIgnoreCase))
            return "MG: " + (scopeName ?? Seg("managementGroups"));
        if (parts.Length == 0)
            return "Root";

        var subId = Seg("subscriptions");
        var subName = subscriptionNames.TryGetValue(subId.ToLowerInvariant(), out var n) ? n
            : parts.Length == 2 && scopeName is not null ? scopeName
            : subId;
        if (parts.Length == 2) return "Sub: " + subName;

        var rg = Seg("resourceGroups");
        if (parts.Length == 4) return $"{subName} / {rg}";
        return $"{subName} / {rg} / {parts[^1]}";
    }

    public string PolicyKey(PimItem item) =>
        $"azure|{item.ScopeId.ToLowerInvariant()}|{item.RoleDefinitionId.Split('/').Last().ToLowerInvariant()}";

    public async Task<PolicyInfo> GetPolicyAsync(AccountRef account, PimItem item, CancellationToken ct)
    {
        var url = $"{PimApi.ArmBase}{item.ScopeId}{Provider}/roleManagementPolicyAssignments?api-version={ApiVersion}";
        var assignments = await api.GetAllAsync(account, Audience.Arm, $"{url}&$filter={Filter($"roleDefinitionId eq '{item.RoleDefinitionId}'")}", ct);
        if (assignments.Count == 0)
        {
            var roleGuid = item.RoleDefinitionId.Split('/').Last();
            assignments = [.. (await api.GetAllAsync(account, Audience.Arm, url, ct))
                .Where(a => a.Obj("properties")?.Str("roleDefinitionId")?.EndsWith(roleGuid, StringComparison.OrdinalIgnoreCase) == true)];
        }

        var props = assignments.FirstOrDefault().Obj("properties");
        if (props is null) return PolicyParser.Parse(default);
        if (props.Value.TryGetProperty("effectiveRules", out var effective) && effective.ValueKind == JsonValueKind.Array)
            return PolicyParser.Parse(effective);

        var policyId = props.Value.Str("policyId");
        if (policyId is null) return PolicyParser.Parse(default);
        var policy = await api.SendAsync(account, Audience.Arm, HttpMethod.Get, $"{PimApi.ArmBase}{policyId}?api-version={ApiVersion}", ct: ct);
        var policyProps = policy.Obj("properties");
        if (policyProps?.TryGetProperty("effectiveRules", out var pe) == true && pe.ValueKind == JsonValueKind.Array) return PolicyParser.Parse(pe);
        return PolicyParser.Parse(policyProps?.TryGetProperty("rules", out var rules) == true ? rules : default);
    }

    public Task<string> ActivateAsync(AccountRef account, PimItem item, ActivationRequest request, CancellationToken ct) =>
        SubmitAsync(account, item, "SelfActivate", request, ct);

    public Task<string> ExtendAsync(AccountRef account, PimItem item, ActivationRequest request, CancellationToken ct) =>
        SubmitAsync(account, item, "SelfExtend", request, ct);

    public Task<string> DeactivateAsync(AccountRef account, PimItem item, CancellationToken ct) =>
        SubmitAsync(account, item, "SelfDeactivate", null, ct);

    public async Task CancelAsync(AccountRef account, PimItem item, CancellationToken ct) =>
        await api.SendAsync(account, Audience.Arm, HttpMethod.Post, $"{PimApi.ArmBase}{item.PendingRequestId}/cancel?api-version={ApiVersion}", ct: ct);

    private async Task<string> SubmitAsync(AccountRef account, PimItem item, string requestType, ActivationRequest? request, CancellationToken ct)
    {
        var props = new Dictionary<string, object?>
        {
            ["principalId"] = item.PrincipalId,
            ["roleDefinitionId"] = item.RoleDefinitionId,
            ["requestType"] = requestType,
        };
        if (item.EligibilityScheduleId is not null) props["linkedRoleEligibilityScheduleId"] = item.EligibilityScheduleId;
        if (request is not null)
        {
            props["justification"] = request.Justification ?? "";
            props["scheduleInfo"] = ScheduleInfo(request.Duration, "AfterDuration");
            if (TicketInfo(request) is { } t) props["ticketInfo"] = t;
        }
        var url = $"{PimApi.ArmBase}{item.ScopeId}{Provider}/roleAssignmentScheduleRequests/{Guid.NewGuid()}?api-version={ApiVersion}";
        var claims = request is null ? null : PolicyParser.ClaimsFor(item.Policy);
        var response = await api.SendAsync(account, Audience.Arm, HttpMethod.Put, url, new { properties = props }, claims, ct);
        return Status(response);
    }
}
