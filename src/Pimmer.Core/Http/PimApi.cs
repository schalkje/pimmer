using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pimmer.Core.Auth;
using Pimmer.Core.Services;

namespace Pimmer.Core.Http;

public sealed class PimApiException(int statusCode, string? code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}

/// <summary>
/// Thin REST client for Graph and ARM: bearer tokens per account, paging, retry on throttling,
/// and step-up on claims challenges (401 insufficient_claims or PIM AcrsValidationFailed / MfaRule).
/// </summary>
public sealed partial class PimApi(HttpClient http, ITokenService tokens)
{
    public const string GraphBase = "https://graph.microsoft.com/v1.0";
    public const string ArmBase = "https://management.azure.com";
    private const int MaxAttempts = 4;

    public async Task<JsonElement> SendAsync(AccountRef account, Audience audience, HttpMethod method, string url, object? body = null,
        string? claims = null, CancellationToken ct = default)
    {
        var claimsRetried = false;
        for (var attempt = 1; ; attempt++)
        {
            var token = await tokens.GetTokenAsync(account, audience, claims, ct);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);

            if (response.IsSuccessStatusCode)
                return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();

            var status = (int)response.StatusCode;
            if ((status == 429 || status == 503 || status == 504) && attempt < MaxAttempts)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, ct);
                continue;
            }

            var (code, message) = ParseError(text);
            if (!claimsRetried && FindClaimsChallenge(response, code, message) is { } challenge)
            {
                claims = challenge;
                claimsRetried = true;
                continue;
            }

            throw new PimApiException(status, code ?? response.StatusCode.ToString(), message ?? response.ReasonPhrase ?? "Request failed");
        }
    }

    /// <summary>GETs a collection and follows @odata.nextLink (Graph) / nextLink (ARM).</summary>
    public async Task<List<JsonElement>> GetAllAsync(AccountRef account, Audience audience, string url, CancellationToken ct = default)
    {
        var items = new List<JsonElement>();
        string? next = url;
        while (next is not null)
        {
            var page = await SendAsync(account, audience, HttpMethod.Get, next, ct: ct);
            items.AddRange(page.Arr("value"));
            next = page.Str("@odata.nextLink") ?? page.Str("nextLink");
        }
        return items;
    }

    public static (string? Code, string? Message) ParseError(string text)
    {
        try
        {
            var root = JsonDocument.Parse(text).RootElement;
            var error = root.Obj("error");
            return (error?.Str("code"), error?.Str("message"));
        }
        catch (JsonException)
        {
            return (null, string.IsNullOrWhiteSpace(text) ? null : text);
        }
    }

    internal static string? FindClaimsChallenge(HttpResponseMessage response, string? code, string? message)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            foreach (var header in response.Headers.WwwAuthenticate)
            {
                var p = header.Parameter ?? "";
                if (!p.Contains("insufficient_claims", StringComparison.OrdinalIgnoreCase)) continue;
                var m = WwwAuthClaimsRegex().Match(p);
                if (m.Success) return DecodeBase64Claims(m.Groups[1].Value);
            }
        }
        return FindClaimsInError(code, message);
    }

    public static string? FindClaimsInError(string? code, string? message)
    {
        if (code?.Contains("AcrsValidationFailed", StringComparison.OrdinalIgnoreCase) == true && message is not null)
        {
            var m = MessageClaimsRegex().Match(message);
            if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        }
        if (message?.Contains("MfaRule", StringComparison.OrdinalIgnoreCase) == true)
            return PolicyParser.MfaClaims;
        return null;
    }

    private static string DecodeBase64Claims(string value)
    {
        try
        {
            var padded = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        }
        catch (FormatException)
        {
            return value;
        }
    }

    [GeneratedRegex("claims=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex WwwAuthClaimsRegex();

    [GeneratedRegex("claims=([^&\\s\"]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MessageClaimsRegex();
}
