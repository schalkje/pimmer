using System.Text.Json;
using Pimmer.Core;
using Pimmer.Core.Http;
using Pimmer.Core.Providers;
using Pimmer.Core.Services;

namespace Pimmer.Core.Tests;

public class CoreTests
{
    private static readonly AccountRef Account = new("home-1", "me@contoso.com", "oid-1", "tid", "user", "#0f6cbd");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static PimItem Item(string key, PimState state = PimState.Eligible, PolicyInfo? policy = null) => new()
    {
        Key = key, AccountId = "home-1", PrincipalId = "oid-1", Kind = PimKind.EntraRole, DisplayName = "Global Reader",
        ScopeId = "/", ScopeDisplay = "Directory", RoleDefinitionId = "r1", State = state, Policy = policy
    };

    [Fact]
    public void PolicyParser_reads_end_user_assignment_rules()
    {
        var rules = Json("""
        [
          { "id": "Expiration_EndUser_Assignment", "maximumDuration": "PT4H" },
          { "id": "Expiration_Admin_Assignment", "maximumDuration": "P365D" },
          { "id": "Enablement_EndUser_Assignment", "enabledRules": ["Justification", "Ticketing", "MultiFactorAuthentication"] },
          { "id": "AuthenticationContext_EndUser_Assignment", "isEnabled": true, "claimValue": "c1" },
          { "id": "Approval_EndUser_Assignment", "setting": { "isApprovalRequired": true } }
        ]
        """);

        var p = PolicyParser.Parse(rules);

        Assert.Equal(new PolicyInfo(TimeSpan.FromHours(4), true, true, true, "c1", true), p);
        Assert.Contains("\"c1\"", PolicyParser.ClaimsFor(p));
    }

    [Fact]
    public void PolicyParser_defaults_and_ignores_disabled_auth_context()
    {
        var p = PolicyParser.Parse(Json("""[{ "id": "AuthenticationContext_EndUser_Assignment", "isEnabled": false, "claimValue": "c1" }]"""));

        Assert.Equal(PolicyParser.DefaultMaxDuration, p.MaxDuration);
        Assert.Null(p.AuthContextId);
        Assert.False(p.RequiresApproval);
        Assert.Null(PolicyParser.ClaimsFor(p));
    }

    [Fact]
    public void ItemMerger_active_wins_over_pending_and_keeps_eligibility()
    {
        var eligible = Item("a") with { EligibilityScheduleId = "sched", EligibleUntil = DateTimeOffset.Parse("2030-01-01Z") };
        var until = DateTimeOffset.Parse("2025-01-01T12:00Z");

        var merged = ItemMerger.Merge(
            [eligible, Item("b"), Item("c")],
            [Item("a") with { ActiveUntil = until }, Item("x")],
            [Item("a") with { PendingRequestId = "req" }, Item("b") with { PendingRequestId = "req-b" }]);

        var a = merged.Single(i => i.Key == "a");
        Assert.Equal(PimState.Active, a.State);
        Assert.Equal(until, a.ActiveUntil);
        Assert.Equal("sched", a.EligibilityScheduleId);
        Assert.Null(a.PendingRequestId);
        var b = merged.Single(i => i.Key == "b");
        Assert.Equal((PimState.PendingApproval, "req-b"), (b.State, b.PendingRequestId));
        Assert.Equal(PimState.Eligible, merged.Single(i => i.Key == "c").State);
        Assert.Equal(PimState.Active, merged.Single(i => i.Key == "x").State);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("global", true)]
    [InlineData("READER dir", true)]
    [InlineData("reader user", true)]
    [InlineData("reader admin", false)]
    public void SearchMatcher_requires_every_token(string query, bool expected) =>
        Assert.Equal(expected, SearchMatcher.Matches(query, Item("a"), "user"));

    [Theory]
    [InlineData("/", "Root")]
    [InlineData("/providers/Microsoft.Management/managementGroups/mg1", "MG: Tenant Root")]
    [InlineData("/subscriptions/ABC", "Sub: Prod")]
    [InlineData("/subscriptions/abc/resourceGroups/rg1", "Prod / rg1")]
    [InlineData("/subscriptions/zzz/resourceGroups/rg1/providers/Microsoft.KeyVault/vaults/kv1", "zzz / rg1 / kv1")]
    public void ScopeDisplay_formats_arm_scopes(string scope, string expected)
    {
        var name = scope.Contains("managementGroups") ? "Tenant Root" : null;
        Assert.Equal(expected, AzureResourceProvider.ScopeDisplay(scope, name, new Dictionary<string, string> { ["abc"] = "Prod" }));
    }

    [Fact]
    public void AzureResourceProvider_parses_eligibility_instance()
    {
        var e = Json("""
        { "properties": {
            "scope": "/subscriptions/abc",
            "roleDefinitionId": "/subscriptions/abc/providers/Microsoft.Authorization/roleDefinitions/B24988AC-6180-42A0-AB88-20F7382DD24C",
            "roleEligibilityScheduleId": "/subscriptions/abc/providers/Microsoft.Authorization/roleEligibilitySchedules/s1",
            "endDateTime": "2030-01-01T00:00:00Z",
            "expandedProperties": { "roleDefinition": { "displayName": "Contributor" }, "scope": { "displayName": "Prod sub" } } } }
        """);

        var item = AzureResourceProvider.Parse(e, Account, new Dictionary<string, string>());

        Assert.Equal("azureresource|oid-1|b24988ac-6180-42a0-ab88-20f7382dd24c|/subscriptions/abc|", item.Key);
        Assert.Equal("Contributor", item.DisplayName);
        Assert.Equal("Sub: Prod sub", item.ScopeDisplay);
        Assert.Equal("Prod sub", item.ScopeName);
        Assert.EndsWith("/s1", item.EligibilityScheduleId);
    }

    [Theory]
    [InlineData("/subscriptions/abc", "Prod")]
    [InlineData("/subscriptions/abc/resourceGroups/rg1", "rg1")]
    [InlineData("/subscriptions/abc/resourceGroups/rg1/providers/Microsoft.KeyVault/vaults/kv1", "kv1")]
    [InlineData("/", null)]
    public void Azure_ScopeName_is_most_specific_target(string scope, string? expected) =>
        Assert.Equal(expected, AzureResourceProvider.ScopeName(scope, null, new Dictionary<string, string> { ["abc"] = "Prod" }));

    [Fact]
    public void EntraRoleProvider_names_app_registration_scope_from_expand()
    {
        var e = Json("""
        { "roleDefinitionId": "r1", "directoryScopeId": "/c8e07977",
          "roleDefinition": { "displayName": "Application Owner" },
          "directoryScope": { "@odata.type": "#microsoft.graph.application", "id": "/c8e07977", "displayName": "datapilot-app" } }
        """);

        var item = EntraRoleProvider.Parse(e, Account);

        Assert.Equal("datapilot-app", item.ScopeName);
        Assert.Equal("App registration: datapilot-app", item.ScopeDisplay);
        Assert.True(SearchMatcher.Matches("datapilot owner", item, null));
        Assert.Equal((null, "Directory"), EntraRoleProvider.DescribeScope("/", null));
        Assert.Equal((null, "Scope c8e07977"), EntraRoleProvider.DescribeScope("/c8e07977", null));
    }

    [Fact]
    public void FindClaimsInError_extracts_acrs_claims_and_detects_mfa()
    {
        var claims = Uri.EscapeDataString("""{"access_token":{"acrs":{"essential":true,"value":"c2"}}}""");

        Assert.Equal("""{"access_token":{"acrs":{"essential":true,"value":"c2"}}}""",
            PimApi.FindClaimsInError("RoleAssignmentRequestAcrsValidationFailed", $"Validation failed: &claims={claims}"));
        Assert.Equal(PolicyParser.MfaClaims, PimApi.FindClaimsInError("RoleAssignmentRequestPolicyValidationFailed", "Rule MfaRule failed"));
        Assert.Null(PimApi.FindClaimsInError("Other", "nope"));
    }

    [Fact]
    public void Validate_and_Clamp_follow_policy()
    {
        var policy = new PolicyInfo(TimeSpan.FromHours(2), true, true, false, null, false);
        var item = Item("a", policy: policy);

        Assert.NotNull(PimService.Validate(item, new ActivationRequest(TimeSpan.FromHours(1), " ", "T1", null)));
        Assert.NotNull(PimService.Validate(item, new ActivationRequest(TimeSpan.FromHours(1), "work", null, null)));
        Assert.Null(PimService.Validate(item, new ActivationRequest(TimeSpan.FromHours(1), "work", "T1", null)));
        Assert.Equal(TimeSpan.FromHours(2), PimService.Clamp(TimeSpan.FromHours(8), policy));
        Assert.Equal(TimeSpan.FromHours(8), PimService.Clamp(TimeSpan.FromHours(8), null));
    }

    [Fact]
    public void MessageResolver_follows_precedence()
    {
        var data = new Pimmer.Core.Storage.StoreData { RecentJustifications = ["overall"] };
        var a = Item("a");
        var group = new Pimmer.Core.Storage.PimGroup { LastJustification = "group last" };

        Assert.Equal("overall", MessageResolver.Resolve([a], null, data));
        data.ItemMessages["a"] = new() { Last = "item last" };
        Assert.Equal("item last", MessageResolver.Resolve([a], null, data));
        data.ItemMessages["a"].Default = "item default";
        Assert.Equal("item default", MessageResolver.Resolve([a], null, data));
        Assert.Equal("group last", MessageResolver.Resolve([a], group, data));
        group.DefaultJustification = "group default";
        Assert.Equal("group default", MessageResolver.Resolve([a], group, data));
        Assert.Equal("", MessageResolver.Resolve([a, Item("b")], null, data));
    }

    private static Pimmer.Core.Storage.HistoryEntry Entry(string key, string action, int minutes, string result = "Succeeded") =>
        new(key, "home-1", "n", "s", PimKind.EntraRole, action, minutes, null, null, result, DateTimeOffset.Now);

    [Fact]
    public void LocalStore_remembers_messages_per_item_and_group()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pimmer-test-" + Guid.NewGuid().ToString("n"));
        try
        {
            var store = new Pimmer.Core.Storage.LocalStore(dir);
            var group = store.AddGroup("g");

            store.RememberMessage("  work  ", "a", null, setAsDefault: true);
            store.RememberMessage("other", "a", null, setAsDefault: false);
            store.RememberMessage("for group", "a", group.Id, setAsDefault: true);
            store.RememberMessage("", "a", null, setAsDefault: false);

            var reloaded = new Pimmer.Core.Storage.LocalStore(dir).Data;
            Assert.Equal("other", reloaded.ItemMessages["a"].Last);
            Assert.Equal("work", reloaded.ItemMessages["a"].Default);
            Assert.Equal("for group", reloaded.Groups[0].LastJustification);
            Assert.Equal(["for group", "other", "work"], reloaded.RecentJustifications);

            store.RecordActivation([Entry("a", "Activate", 120), Entry("b", "Activate", 30, "Failed"), Entry("c", "Extend", 60)], null, 120);
            Assert.Equal(120, store.Data.ItemMessages["a"].LastDurationMinutes);
            Assert.False(store.Data.ItemMessages.ContainsKey("b"));
            Assert.False(store.Data.ItemMessages.ContainsKey("c"));

            store.RemoveSavedMessage("work");
            Assert.Null(store.Data.ItemMessages["a"].Default);
            Assert.DoesNotContain("work", store.Data.RecentJustifications);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
