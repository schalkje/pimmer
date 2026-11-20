using Microsoft.Identity.Client;
using Pimmer.Core.Http;

namespace Pimmer.Core.Services;

public static class ErrorMapper
{
    public static string ToFriendly(Exception ex) => ex switch
    {
        PimApiException api => ToFriendly(api.Code, api.Message, api.StatusCode),
        MsalClientException { ErrorCode: "authentication_canceled" } => "Sign-in was cancelled.",
        MsalException msal => $"Sign-in failed: {msal.Message}",
        OperationCanceledException => "Cancelled.",
        HttpRequestException http => $"Network error: {http.Message}",
        _ => ex.Message
    };

    public static string ToFriendly(string? code, string? message, int status = 0)
    {
        var c = code ?? "";
        var m = message ?? "";
        if (c.Contains("RoleAssignmentExists", StringComparison.OrdinalIgnoreCase)) return "Already active.";
        if (c.Contains("ActiveDurationTooShort", StringComparison.OrdinalIgnoreCase)) return "Too soon to deactivate: wait at least 5 minutes after activation.";
        if (c.Contains("RoleAssignmentDoesNotExist", StringComparison.OrdinalIgnoreCase)) return "Not active (anymore).";
        if (c.Contains("AcrsValidationFailed", StringComparison.OrdinalIgnoreCase)) return "Extra sign-in (authentication context) required and not satisfied.";
        if (c.Contains("PendingRoleAssignmentRequest", StringComparison.OrdinalIgnoreCase)) return "There is already a pending request for this item.";
        if (m.Contains("ExpirationRule", StringComparison.OrdinalIgnoreCase)) return "Requested duration exceeds the policy maximum.";
        if (m.Contains("JustificationRule", StringComparison.OrdinalIgnoreCase)) return "A justification is required.";
        if (m.Contains("TicketingRule", StringComparison.OrdinalIgnoreCase)) return "A ticket number is required.";
        if (m.Contains("MfaRule", StringComparison.OrdinalIgnoreCase)) return "Multi-factor authentication is required.";
        if (c.Contains("AuthorizationFailed", StringComparison.OrdinalIgnoreCase) || status == 403) return $"Access denied: {m}";
        if (status == 429) return "Throttled by the service; try again shortly.";
        return string.IsNullOrWhiteSpace(m) ? $"Request failed ({status} {c})." : m;
    }
}
