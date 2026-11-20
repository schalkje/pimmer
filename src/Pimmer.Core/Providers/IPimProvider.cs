using System.Text.Json;
using Pimmer.Core.Http;

namespace Pimmer.Core.Providers;

public sealed record PlaneSnapshot(IReadOnlyList<PimItem> Eligible, IReadOnlyList<PimItem> Active, IReadOnlyList<PimItem> Pending);

public interface IPimProvider
{
    PimKind Kind { get; }
    Task<PlaneSnapshot> ListAsync(AccountRef account, CancellationToken ct);
    /// <summary>Key under which the policy is cached (policies are shared across accounts).</summary>
    string PolicyKey(PimItem item);
    Task<PolicyInfo> GetPolicyAsync(AccountRef account, PimItem item, CancellationToken ct);
    Task<string> ActivateAsync(AccountRef account, PimItem item, ActivationRequest request, CancellationToken ct);
    Task<string> ExtendAsync(AccountRef account, PimItem item, ActivationRequest request, CancellationToken ct);
    Task<string> DeactivateAsync(AccountRef account, PimItem item, CancellationToken ct);
    Task CancelAsync(AccountRef account, PimItem item, CancellationToken ct);
}

internal static class ProviderHelpers
{
    public static object ScheduleInfo(TimeSpan duration, string expirationType = "afterDuration") => new
    {
        startDateTime = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        expiration = new { type = expirationType, duration = Iso8601.Format(duration) }
    };

    public static object? TicketInfo(ActivationRequest r) =>
        string.IsNullOrWhiteSpace(r.TicketNumber) ? null : new { ticketNumber = r.TicketNumber, ticketSystem = r.TicketSystem ?? "" };

    public static string Status(JsonElement response) =>
        response.Str("status") ?? response.Obj("properties")?.Str("status") ?? "Submitted";

    /// <summary>Tries each $expand in order (richest first), falling back to the next on 400 and finally to no $expand.</summary>
    public static async Task<List<JsonElement>> GetWithOptionalExpandAsync(PimApi api, AccountRef account, string url, CancellationToken ct, params string[] expands)
    {
        foreach (var expand in expands)
        {
            try
            {
                return await api.GetAllAsync(account, Audience.Graph, $"{url}{(url.Contains('?') ? '&' : '?')}$expand={expand}", ct);
            }
            catch (PimApiException ex) when (ex.StatusCode == 400)
            {
            }
        }
        return await api.GetAllAsync(account, Audience.Graph, url, ct);
    }

    public static string Filter(string filter) => Uri.EscapeDataString(filter);
}
