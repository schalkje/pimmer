using System.Globalization;
using System.Text.Json;
using System.Xml;

namespace Pimmer.Core;

internal static class JsonExt
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    public static DateTimeOffset? Date(this JsonElement e, string name) =>
        e.Str(name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

public static class Iso8601
{
    public static string Format(TimeSpan t) => XmlConvert.ToString(t);

    public static bool TryParse(string? s, out TimeSpan t)
    {
        t = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        try { t = XmlConvert.ToTimeSpan(s); return true; }
        catch (FormatException) { return false; }
    }

    public static string Friendly(TimeSpan t) =>
        t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m"
        : t.Minutes == 0 ? $"{(int)t.TotalHours}h"
        : $"{(int)t.TotalHours}h {t.Minutes}m";
}
