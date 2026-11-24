using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pimmer.Core.Storage;

/// <summary>JSON-file backed local storage. Holds metadata only, never tokens.</summary>
public sealed class LocalStore
{
    public const int MaxHistory = 300;
    public const int MaxJustifications = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Lock _lock = new();
    private readonly string _dataPath;
    private readonly string _cachePath;

    public LocalStore(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        _dataPath = Path.Combine(dataDirectory, "pimmer.json");
        _cachePath = Path.Combine(dataDirectory, "cache.json");
        Data = Load<StoreData>(_dataPath) ?? new StoreData();
        Cache = Load<CacheData>(_cachePath) ?? new CacheData();
    }

    public string DataDirectory { get; }
    public StoreData Data { get; }
    public CacheData Cache { get; }

    public event Action? Changed;

    public void Update(Action<StoreData> change)
    {
        lock (_lock)
        {
            change(Data);
            Save(_dataPath, Data);
        }
        Changed?.Invoke();
    }

    public void UpdateCache(Action<CacheData> change)
    {
        lock (_lock)
        {
            change(Cache);
            Save(_cachePath, Cache);
        }
    }

    public T Read<T>(Func<StoreData, T> read)
    {
        lock (_lock) return read(Data);
    }

    public bool IsFavorite(string key) => Read(d => d.Favorites.Contains(key));

    public void ToggleFavorite(string key) => Update(d =>
    {
        if (!d.Favorites.Remove(key)) d.Favorites.Add(key);
    });

    public PimGroup AddGroup(string name)
    {
        var g = new PimGroup { Name = name.Trim() };
        Update(d => d.Groups.Add(g));
        return g;
    }

    public void AddToGroup(string groupId, IEnumerable<string> keys) => Update(d =>
    {
        var g = d.Groups.FirstOrDefault(x => x.Id == groupId);
        if (g is null) return;
        foreach (var k in keys)
            if (!g.ItemKeys.Contains(k)) g.ItemKeys.Add(k);
    });

    public void RemoveFromGroup(string groupId, IEnumerable<string> keys) => Update(d =>
    {
        var set = keys.ToHashSet();
        d.Groups.FirstOrDefault(x => x.Id == groupId)?.ItemKeys.RemoveAll(set.Contains);
    });

    public void MoveGroup(string groupId, int delta) => Update(d =>
    {
        var i = d.Groups.FindIndex(x => x.Id == groupId);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= d.Groups.Count) return;
        (d.Groups[i], d.Groups[j]) = (d.Groups[j], d.Groups[i]);
    });

    public void RecordActivation(IEnumerable<HistoryEntry> entries, string? justification, int durationMinutes) => Update(d =>
    {
        d.History.InsertRange(0, entries);
        if (d.History.Count > MaxHistory) d.History.RemoveRange(MaxHistory, d.History.Count - MaxHistory);
        if (!string.IsNullOrWhiteSpace(justification))
        {
            d.RecentJustifications.Remove(justification);
            d.RecentJustifications.Insert(0, justification);
            if (d.RecentJustifications.Count > MaxJustifications) d.RecentJustifications.RemoveAt(d.RecentJustifications.Count - 1);
        }
        d.LastDurationMinutes = durationMinutes;
    });

    /// <summary>
    /// Saves a used message. A group activation stores it on the group only; a single-item
    /// activation stores it as that item's last message (and optionally its default).
    /// </summary>
    public void RememberMessage(string? message, string? itemKey, string? groupId, bool setAsDefault) => Update(d =>
    {
        message = message?.Trim();
        if (string.IsNullOrEmpty(message)) return;
        d.RecentJustifications.Remove(message);
        d.RecentJustifications.Insert(0, message);
        if (d.RecentJustifications.Count > MaxJustifications) d.RecentJustifications.RemoveRange(MaxJustifications, d.RecentJustifications.Count - MaxJustifications);

        if (groupId is not null)
        {
            var g = d.Groups.FirstOrDefault(x => x.Id == groupId);
            if (g is not null) g.LastJustification = message;
        }
        else if (itemKey is not null)
        {
            if (!d.ItemMessages.TryGetValue(itemKey, out var m)) d.ItemMessages[itemKey] = m = new ItemMessages();
            m.Last = message;
            if (setAsDefault) m.Default = message;
        }
    });

    public void ClearItemDefault(string itemKey) => Update(d =>
    {
        if (!d.ItemMessages.TryGetValue(itemKey, out var m)) return;
        m.Default = null;
        if (m.Last is null) d.ItemMessages.Remove(itemKey);
    });

    public void RemoveSavedMessage(string message) => Update(d =>
    {
        d.RecentJustifications.Remove(message);
        foreach (var m in d.ItemMessages.Values.Where(x => x.Default == message)) m.Default = null;
    });

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".corrupt", overwrite: true);
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
