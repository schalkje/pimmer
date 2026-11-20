using System.Collections.Concurrent;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using Pimmer.Core.Storage;

namespace Pimmer.Core.Auth;

public interface ITokenService
{
    Task<AccountRef> SignInNewAccountAsync(CancellationToken ct);
    Task<string> GetTokenAsync(AccountRef account, Audience audience, string? claims, CancellationToken ct);
    Task SignOutAsync(AccountRef account);
}

/// <summary>Runs interactive sign-in on the UI thread (required for the WAM parent window).</summary>
public interface IUiDispatcher
{
    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

/// <summary>
/// MSAL public-client token service. One PublicClientApplication per audience/client id, WAM broker on Windows,
/// persistent (DPAPI) cache, multiple accounts, and claims-challenge (step-up) support.
/// </summary>
public sealed class MsalTokenService(PimmerSettings settings, string cacheDirectory, Func<IntPtr> parentWindow, IUiDispatcher ui) : ITokenService
{
    // ".default" = only the permissions already granted to the client in the tenant, so no consent prompt appears.
    // Requesting explicit scopes that aren't pre-granted triggers "Approval required" in tenants that block user consent.
    // Delegated tokens never exceed what the signed-in user can do.
    public static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    public static readonly string[] ArmScopes = ["https://management.azure.com/.default"];

    private readonly SemaphoreSlim _interactiveLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<IPublicClientApplication>>> _apps = new();
    private readonly ConcurrentDictionary<string, AuthenticationResult> _stepUpTokens = new();

    public async Task<AccountRef> SignInNewAccountAsync(CancellationToken ct)
    {
        var app = await GetAppAsync(Audience.Graph);
        var result = await RunInteractiveAsync(app.AcquireTokenInteractive(GraphScopes).WithPrompt(Prompt.SelectAccount), ct);
        var account = result.Account;
        var upn = account.Username;
        var isAdmin = upn.Contains("adm", StringComparison.OrdinalIgnoreCase);
        return new AccountRef(
            account.HomeAccountId.Identifier,
            upn,
            account.HomeAccountId.ObjectId,
            result.TenantId ?? account.HomeAccountId.TenantId,
            isAdmin ? "Admin" : "User",
            isAdmin ? "#d13438" : "#0f6cbd");
    }

    public async Task<string> GetTokenAsync(AccountRef account, Audience audience, string? claims, CancellationToken ct)
    {
        var stepUpKey = $"{account.HomeAccountId}|{audience}|{claims}";
        if (claims is not null && _stepUpTokens.TryGetValue(stepUpKey, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
            return cached.AccessToken;

        var app = await GetAppAsync(audience);
        var scopes = audience == Audience.Graph ? GraphScopes : ArmScopes;

        AuthenticationResult result;
        try
        {
            result = await AcquireSilentAsync(app, scopes, account, claims, ct);
        }
        catch (MsalUiRequiredException)
        {
            await _interactiveLock.WaitAsync(ct);
            try
            {
                if (claims is not null && _stepUpTokens.TryGetValue(stepUpKey, out cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(2))
                    return cached.AccessToken;
                try
                {
                    // Another caller may have completed an interactive sign-in while we waited.
                    result = await AcquireSilentAsync(app, scopes, account, claims, ct);
                }
                catch (MsalUiRequiredException)
                {
                    var msalAccount = await FindAccountAsync(app, account);
                    var interactive = app.AcquireTokenInteractive(scopes);
                    interactive = msalAccount is not null ? interactive.WithAccount(msalAccount) : interactive.WithLoginHint(account.Upn);
                    if (claims is not null) interactive = interactive.WithClaims(claims);
                    result = await RunInteractiveAsync(interactive, ct);
                }
            }
            finally
            {
                _interactiveLock.Release();
            }
        }

        if (claims is not null) _stepUpTokens[stepUpKey] = result;
        return result.AccessToken;
    }

    public async Task SignOutAsync(AccountRef account)
    {
        foreach (var audience in new[] { Audience.Graph, Audience.Arm })
        {
            var app = await GetAppAsync(audience);
            if (await FindAccountAsync(app, account) is { } a) await app.RemoveAsync(a);
        }
        foreach (var key in _stepUpTokens.Keys.Where(k => k.StartsWith(account.HomeAccountId + "|", StringComparison.Ordinal)))
            _stepUpTokens.TryRemove(key, out _);
    }

    private static async Task<AuthenticationResult> AcquireSilentAsync(IPublicClientApplication app, string[] scopes, AccountRef account, string? claims, CancellationToken ct)
    {
        var msalAccount = await FindAccountAsync(app, account);
        var silent = msalAccount is not null ? app.AcquireTokenSilent(scopes, msalAccount) : app.AcquireTokenSilent(scopes, account.Upn);
        if (claims is not null) silent = silent.WithClaims(claims);
        return await silent.ExecuteAsync(ct);
    }

    private static async Task<IAccount?> FindAccountAsync(IPublicClientApplication app, AccountRef account) =>
        (await app.GetAccountsAsync()).FirstOrDefault(a =>
            a.HomeAccountId?.Identifier == account.HomeAccountId ||
            string.Equals(a.Username, account.Upn, StringComparison.OrdinalIgnoreCase));

    private Task<AuthenticationResult> RunInteractiveAsync(AcquireTokenInteractiveParameterBuilder builder, CancellationToken ct) =>
        ui.InvokeAsync(() => builder.ExecuteAsync(ct));

    private Task<IPublicClientApplication> GetAppAsync(Audience audience)
    {
        var clientId = audience == Audience.Graph ? settings.GraphClientId : settings.ArmClientId;
        return _apps.GetOrAdd(clientId, id => new Lazy<Task<IPublicClientApplication>>(() => CreateAppAsync(id))).Value;
    }

    private async Task<IPublicClientApplication> CreateAppAsync(string clientId)
    {
        var builder = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, settings.TenantId)
            .WithRedirectUri("http://localhost")
            .WithParentActivityOrWindow(parentWindow)
            .WithClientName("Pimmer");

        if (settings.UseBroker)
            builder = builder.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Pimmer" });

        var app = builder.Build();
        var props = new StorageCreationPropertiesBuilder($"msal_{clientId}.cache", cacheDirectory).Build();
        var helper = await MsalCacheHelper.CreateAsync(props);
        helper.RegisterCache(app.UserTokenCache);
        return app;
    }
}
