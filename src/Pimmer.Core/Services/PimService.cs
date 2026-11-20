using System.Collections.Concurrent;
using Pimmer.Core.Auth;
using Pimmer.Core.Providers;
using Pimmer.Core.Storage;

namespace Pimmer.Core.Services;

/// <summary>
/// Application facade: aggregates all providers for all accounts, keeps the in-memory item list,
/// caches policies, and runs (bulk) activation / deactivation with progress per item.
/// </summary>
public sealed class PimService
{
    public static readonly TimeSpan PolicyTtl = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan[] PollDelays = new[] { 5, 10, 10, 15, 20, 30, 30 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();
    private const int MaxParallel = 4;

    private readonly ITokenService _tokens;
    private readonly Dictionary<PimKind, IPimProvider> _providers;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<PimItem>> _buckets = [];
    private readonly ConcurrentDictionary<string, string> _errors = new();
    private readonly ConcurrentDictionary<string, OperationStatus> _operations = new();
    private readonly ConcurrentDictionary<string, PimState> _awaiting = new();
    private Task? _refreshTask;
    private int _polling;

    public PimService(ITokenService tokens, LocalStore store, IEnumerable<IPimProvider> providers)
    {
        _tokens = tokens;
        Store = store;
        _providers = providers.ToDictionary(p => p.Kind);
        foreach (var (key, items) in store.Cache.Buckets) _buckets[key] = items;
        LastRefreshed = store.Cache.RefreshedAt;
        Rebuild();
    }

    public LocalStore Store { get; }
    public IReadOnlyList<PimItem> Items { get; private set; } = [];
    public IReadOnlyList<AccountRef> Accounts => Store.Read(d => d.Accounts.ToList());
    public IReadOnlyDictionary<string, string> SourceErrors => _errors;
    public IReadOnlyDictionary<string, OperationStatus> Operations => _operations;
    public bool IsRefreshing { get; private set; }
    public DateTimeOffset? LastRefreshed { get; private set; }

    public event Action? Changed;

    public AccountRef? AccountFor(PimItem item) => AccountById(item.AccountId);

    public AccountRef? AccountById(string accountId) => Store.Read(d => d.Accounts.FirstOrDefault(a => a.HomeAccountId == accountId));

    public static string BucketKey(string accountId, PimKind kind) => $"{accountId}|{kind}";

    // ---------- Accounts ----------

    public async Task<AccountRef> AddAccountAsync(CancellationToken ct = default)
    {
        var account = await _tokens.SignInNewAccountAsync(ct);
        Store.Update(d =>
        {
            var i = d.Accounts.FindIndex(a => a.HomeAccountId == account.HomeAccountId);
            if (i >= 0) d.Accounts[i] = d.Accounts[i] with { Upn = account.Upn };
            else d.Accounts.Add(account);
        });
        Notify();
        _ = RefreshAsync(ct);
        return account;
    }

    public async Task RemoveAccountAsync(string accountId)
    {
        if (AccountById(accountId) is { } account) await _tokens.SignOutAsync(account);
        Store.Update(d => d.Accounts.RemoveAll(a => a.HomeAccountId == accountId));
        lock (_lock)
        {
            foreach (var k in _buckets.Keys.Where(k => k.StartsWith(accountId + "|", StringComparison.Ordinal)).ToList())
                _buckets.Remove(k);
        }
        foreach (var k in _errors.Keys.Where(k => k.StartsWith(accountId + "|", StringComparison.Ordinal)))
            _errors.TryRemove(k, out _);
        SaveCache();
        Rebuild();
        Notify();
    }

    public void UpdateAccount(AccountRef updated)
    {
        Store.Update(d =>
        {
            var i = d.Accounts.FindIndex(a => a.HomeAccountId == updated.HomeAccountId);
            if (i >= 0) d.Accounts[i] = updated;
        });
        Notify();
    }

    public void MoveAccount(string accountId, int delta)
    {
        Store.Update(d =>
        {
            var i = d.Accounts.FindIndex(a => a.HomeAccountId == accountId);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= d.Accounts.Count) return;
            (d.Accounts[i], d.Accounts[j]) = (d.Accounts[j], d.Accounts[i]);
        });
        Notify();
    }

    // ---------- Refresh ----------

    public Task RefreshAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_refreshTask is { IsCompleted: false }) return _refreshTask;
            _refreshTask = DoRefreshAsync(ct);
            return _refreshTask;
        }
    }

    private async Task DoRefreshAsync(CancellationToken ct)
    {
        IsRefreshing = true;
        Notify();
        try
        {
            var tasks = Accounts.SelectMany(a => _providers.Values.Select(p => RefreshBucketAsync(a, p, ct)));
            await Task.WhenAll(tasks);
            LastRefreshed = DateTimeOffset.Now;
            SaveCache();
        }
        finally
        {
            IsRefreshing = false;
            Rebuild();
            Notify();
        }
        _ = PrefetchPoliciesAsync(ct);
    }

    private async Task RefreshBucketAsync(AccountRef account, IPimProvider provider, CancellationToken ct)
    {
        var key = BucketKey(account.HomeAccountId, provider.Kind);
        try
        {
            var snapshot = await provider.ListAsync(account, ct);
            var merged = ItemMerger.Merge(snapshot.Eligible, snapshot.Active, snapshot.Pending);
            lock (_lock) _buckets[key] = merged;
            _errors.TryRemove(key, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _errors[key] = ErrorMapper.ToFriendly(ex);
        }
    }

    private void Rebuild()
    {
        var accountIds = Store.Read(d => d.Accounts.Select(a => a.HomeAccountId).ToHashSet());
        List<PimItem> items;
        lock (_lock)
        {
            items = [.. _buckets.Where(b => accountIds.Contains(b.Key.Split('|')[0])).SelectMany(b => b.Value)];
        }
        var policies = Store.Cache.Policies;
        for (var i = 0; i < items.Count; i++)
        {
            if (_providers.TryGetValue(items[i].Kind, out var p) && policies.TryGetValue(p.PolicyKey(items[i]), out var cached))
                items[i] = items[i] with { Policy = cached.Policy };
        }
        Items = items;

        var byKey = items.ToDictionary(i => i.Key);
        foreach (var (key, expected) in _awaiting)
        {
            var reached = byKey.TryGetValue(key, out var item) ? item.State == expected : expected != PimState.Active;
            if (!reached) continue;
            _awaiting.TryRemove(key, out _);
            _operations.TryRemove(key, out _);
        }
        foreach (var (key, op) in _operations)
        {
            if (op.State != OperationState.Running && DateTimeOffset.Now - op.At > TimeSpan.FromMinutes(5))
                _operations.TryRemove(key, out _);
        }
    }

    private void SaveCache()
    {
        Dictionary<string, List<PimItem>> copy;
        lock (_lock) copy = _buckets.ToDictionary(b => b.Key, b => b.Value);
        Store.UpdateCache(c =>
        {
            c.Buckets = copy;
            c.RefreshedAt = LastRefreshed;
        });
    }

    private void Notify() => Changed?.Invoke();

    // ---------- Policies ----------

    public async Task<PolicyInfo?> GetPolicyAsync(PimItem item, CancellationToken ct = default)
    {
        var provider = _providers[item.Kind];
        var key = provider.PolicyKey(item);
        if (Store.Cache.Policies.TryGetValue(key, out var cached) && DateTimeOffset.Now - cached.FetchedAt < PolicyTtl)
            return cached.Policy;
        if (AccountFor(item) is not { } account) return cached?.Policy;
        try
        {
            var policy = await provider.GetPolicyAsync(account, item, ct);
            Store.UpdateCache(c => c.Policies[key] = new CachedPolicy(policy, DateTimeOffset.Now));
            Rebuild();
            Notify();
            return policy;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return cached?.Policy;
        }
    }

    public async Task EnsurePoliciesAsync(IEnumerable<PimItem> items, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(items.DistinctBy(i => _providers[i.Kind].PolicyKey(i)).Select(async i =>
        {
            await gate.WaitAsync(ct);
            try { await GetPolicyAsync(i, ct); }
            finally { gate.Release(); }
        }));
    }

    private async Task PrefetchPoliciesAsync(CancellationToken ct)
    {
        var interesting = Store.Read(d => d.Favorites.Concat(d.Groups.SelectMany(g => g.ItemKeys)).ToHashSet());
        var items = Items.Where(i => interesting.Contains(i.Key) || i.State == PimState.Active).ToList();
        try { await EnsurePoliciesAsync(items, ct); }
        catch (OperationCanceledException) { }
    }

    public PimItem WithLatest(PimItem item) => Items.FirstOrDefault(i => i.Key == item.Key) ?? item;

    // ---------- Activation ----------

    public async Task<IReadOnlyList<ItemResult>> ActivateAsync(IEnumerable<PimItem> items, ActivationRequest request, CancellationToken ct = default)
    {
        var list = items.Where(i => i.State == PimState.Eligible).DistinctBy(i => i.Key).ToList();
        foreach (var i in list) SetOp(i.Key, OperationState.Running, "Checking policy…");
        await EnsurePoliciesAsync(list, ct);
        list = [.. list.Select(WithLatest)];

        var results = new ConcurrentBag<ItemResult>();
        var runnable = new List<PimItem>();
        foreach (var item in list)
        {
            if (Validate(item, request) is { } error)
            {
                SetOp(item.Key, OperationState.Failed, error);
                results.Add(new ItemResult(item, OperationState.Failed, error));
            }
            else runnable.Add(item);
        }

        await RunGroupedAsync(runnable, async item =>
        {
            var duration = Clamp(request.Duration, item.Policy);
            var result = await RunOpAsync(item, "Activating…", PimState.Active, account =>
                _providers[item.Kind].ActivateAsync(account, item, request with { Duration = duration }, ct));
            results.Add(result);
        });

        RecordHistory(results, "Activate", request);
        StartPolling();
        return [.. results];
    }

    public async Task<IReadOnlyList<ItemResult>> ExtendAsync(IEnumerable<PimItem> items, ActivationRequest request, CancellationToken ct = default)
    {
        var list = items.Where(i => i.State == PimState.Active).DistinctBy(i => i.Key).ToList();
        await EnsurePoliciesAsync(list, ct);
        var results = new ConcurrentBag<ItemResult>();
        await RunGroupedAsync([.. list.Select(WithLatest)], async item =>
        {
            var duration = Clamp(request.Duration, item.Policy);
            results.Add(await RunOpAsync(item, "Extending…", null, account =>
                _providers[item.Kind].ExtendAsync(account, item, request with { Duration = duration }, ct)));
        });
        RecordHistory(results, "Extend", request);
        StartPolling();
        return [.. results];
    }

    public async Task<IReadOnlyList<ItemResult>> DeactivateAsync(IEnumerable<PimItem> items, CancellationToken ct = default)
    {
        var list = items.Where(i => i.State == PimState.Active).DistinctBy(i => i.Key).ToList();
        var results = new ConcurrentBag<ItemResult>();
        await RunGroupedAsync(list, async item =>
            results.Add(await RunOpAsync(item, "Deactivating…", PimState.Eligible, account =>
                _providers[item.Kind].DeactivateAsync(account, item, ct))));
        RecordHistory(results, "Deactivate", null);
        StartPolling();
        return [.. results];
    }

    public async Task<ItemResult> CancelRequestAsync(PimItem item, CancellationToken ct = default)
    {
        var result = await RunOpAsync(item, "Cancelling…", PimState.Eligible, async account =>
        {
            await _providers[item.Kind].CancelAsync(account, item, ct);
            return "Cancelled";
        });
        StartPolling();
        return result;
    }

    public static string? Validate(PimItem item, ActivationRequest request)
    {
        if (item.Policy?.RequiresJustification == true && string.IsNullOrWhiteSpace(request.Justification))
            return "A justification is required.";
        if (item.Policy?.RequiresTicket == true && string.IsNullOrWhiteSpace(request.TicketNumber))
            return "A ticket number is required.";
        return null;
    }

    public static TimeSpan Clamp(TimeSpan requested, PolicyInfo? policy) =>
        policy is not null && requested > policy.MaxDuration ? policy.MaxDuration : requested;

    /// <summary>
    /// Runs the first item of every (account, auth context) group sequentially so a step-up prompt appears once,
    /// then the remaining items in parallel reusing the step-up token.
    /// </summary>
    private static async Task RunGroupedAsync(List<PimItem> items, Func<PimItem, Task> run)
    {
        var groups = items.GroupBy(i => (i.AccountId, i.Policy?.AuthContextId)).ToList();
        foreach (var g in groups) await run(g.First());

        using var gate = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(groups.SelectMany(g => g.Skip(1)).Select(async item =>
        {
            await gate.WaitAsync();
            try { await run(item); }
            finally { gate.Release(); }
        }));
    }

    private async Task<ItemResult> RunOpAsync(PimItem item, string runningText, PimState? expected, Func<AccountRef, Task<string>> action)
    {
        if (AccountFor(item) is not { } account)
            return Fail(item, "Account is not signed in.");
        SetOp(item.Key, OperationState.Running, runningText);
        try
        {
            var status = await action(account);
            if (status.Contains("Pending", StringComparison.OrdinalIgnoreCase) && status.Contains("Approval", StringComparison.OrdinalIgnoreCase))
            {
                SetOp(item.Key, OperationState.Pending, "Pending approval");
                _awaiting[item.Key] = PimState.PendingApproval;
                return new ItemResult(item, OperationState.Pending, "Pending approval");
            }
            var text = expected switch
            {
                PimState.Active => "Activated, waiting for it to take effect…",
                PimState.Eligible => "Done",
                _ => "Done"
            };
            SetOp(item.Key, OperationState.Succeeded, text);
            if (expected is { } e) _awaiting[item.Key] = e;
            return new ItemResult(item, OperationState.Succeeded, status);
        }
        catch (Exception ex)
        {
            return Fail(item, ErrorMapper.ToFriendly(ex));
        }
    }

    private ItemResult Fail(PimItem item, string message)
    {
        SetOp(item.Key, OperationState.Failed, message);
        return new ItemResult(item, OperationState.Failed, message);
    }

    private void SetOp(string key, OperationState state, string message)
    {
        _operations[key] = new OperationStatus(state, message, DateTimeOffset.Now);
        Notify();
    }

    public void DismissOperation(string key)
    {
        _operations.TryRemove(key, out _);
        Notify();
    }

    private void RecordHistory(IEnumerable<ItemResult> results, string action, ActivationRequest? request)
    {
        var entries = results.Select(r => new HistoryEntry(
            r.Item.Key, r.Item.AccountId, r.Item.DisplayName, r.Item.ScopeDisplay, r.Item.Kind, action,
            (int)Clamp(request?.Duration ?? TimeSpan.Zero, r.Item.Policy).TotalMinutes,
            request?.Justification, request?.TicketNumber, r.State.ToString(), DateTimeOffset.Now)).ToList();
        if (entries.Count == 0) return;
        if (action == "Activate" && request is not null)
            Store.RecordActivation(entries, request.Justification, (int)request.Duration.TotalMinutes);
        else
            Store.RecordActivation(entries, null, Store.Data.LastDurationMinutes ?? Store.Data.Settings.DefaultDurationMinutes);
    }

    private void StartPolling()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delay in PollDelays)
                {
                    await Task.Delay(delay);
                    await RefreshAsync();
                    if (_awaiting.IsEmpty) break;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        });
    }
}
