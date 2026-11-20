using System.Text.Json;

namespace Pimmer.Core.Services;

/// <summary>
/// Parses PIM policy rules (Graph unifiedRoleManagementPolicy rules or ARM roleManagementPolicy rules).
/// Both APIs use the same rule ids, and only the EndUser/Assignment rules matter for self-activation.
/// </summary>
public static class PolicyParser
{
    public static readonly TimeSpan DefaultMaxDuration = TimeSpan.FromHours(8);

    public static PolicyInfo Parse(JsonElement rules)
    {
        var max = DefaultMaxDuration;
        bool justification = false, ticket = false, mfa = false, approval = false;
        string? authContext = null;

        if (rules.ValueKind != JsonValueKind.Array)
            return new PolicyInfo(max, justification, ticket, mfa, authContext, approval);

        foreach (var rule in rules.EnumerateArray())
        {
            switch (rule.Str("id"))
            {
                case "Expiration_EndUser_Assignment":
                    if (Iso8601.TryParse(rule.Str("maximumDuration"), out var d) && d > TimeSpan.Zero) max = d;
                    break;
                case "Enablement_EndUser_Assignment":
                    foreach (var e in rule.Arr("enabledRules"))
                    {
                        switch (e.GetString())
                        {
                            case "Justification": justification = true; break;
                            case "Ticketing": ticket = true; break;
                            case "MultiFactorAuthentication": mfa = true; break;
                        }
                    }
                    break;
                case "AuthenticationContext_EndUser_Assignment":
                    var claim = rule.Str("claimValue");
                    if (rule.Bool("isEnabled") && !string.IsNullOrWhiteSpace(claim)) authContext = claim;
                    break;
                case "Approval_EndUser_Assignment":
                    approval = rule.Obj("setting")?.Bool("isApprovalRequired") ?? false;
                    break;
            }
        }

        return new PolicyInfo(max, justification, ticket, mfa, authContext, approval);
    }

    public static string? ClaimsFor(PolicyInfo? policy) =>
        policy?.AuthContextId is { } ctx ? AuthContextClaims(ctx) : null;

    public static string AuthContextClaims(string authContextId) =>
        "{\"access_token\":{\"acrs\":{\"essential\":true,\"value\":\"" + authContextId + "\"}}}";

    public const string MfaClaims = """{"access_token":{"amr":{"essential":true,"values":["mfa"]}}}""";
}
