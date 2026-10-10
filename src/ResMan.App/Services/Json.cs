using System.Globalization;
using System.Text.Json.Nodes;

namespace ResMan.App.Services;

/// <summary>Forgiving accessors for the agent's JSON: a missing or null field gives null, never an exception.</summary>
public static class Json
{
    public static string? Str(this JsonNode? n, string key) =>
        n?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?[key]?.ToString();

    public static double? Num(this JsonNode? n, string key) =>
        n?[key] is JsonValue v ? v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null : null;

    public static long? Long(this JsonNode? n, string key) => n.Num(key) is { } d ? (long)d : null;

    public static bool Bool(this JsonNode? n, string key) => n?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static DateTime? Time(this JsonNode? n, string key) =>
        n.Str(key) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public static IEnumerable<JsonNode> Items(this JsonNode? n, string? key = null) =>
        (key is null ? n : n?[key]) is JsonArray a ? a.Where(x => x is not null).Select(x => x!) : [];

    public static IEnumerable<string> Strings(this JsonNode? n, string key) =>
        n.Items(key).Select(x => x.ToString()).Where(s => s.Length > 0);

    public static string Clock(DateTime utc) => utc.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);

    public static string Day(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? "Today" : local.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : local.ToString("ddd d MMM", CultureInfo.CurrentCulture);
    }
}
