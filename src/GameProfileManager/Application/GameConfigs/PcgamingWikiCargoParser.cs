using System.Text.Json;

namespace GameProfileManager.Application;

internal static class PcgamingWikiCargoParser
{
    public static IReadOnlyList<string> Parse(JsonElement root)
    {
        if (!root.TryGetProperty("cargoquery", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array) return [];
        var paths = new List<string>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            string type = GetField(row, "Type");
            string platform = GetField(row, "Platform");
            if (!platform.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
                !type.Contains("config", StringComparison.OrdinalIgnoreCase)) continue;
            paths.AddRange(PcgamingWikiArticleParser.SplitCargoPaths(GetField(row, "Paths")));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string GetField(JsonElement row, string fieldName)
    {
        foreach (string containerName in new[] { "title", "fields" })
        {
            if (!TryGetPropertyIgnoreCase(row, containerName, out JsonElement container) || container.ValueKind != JsonValueKind.Object) continue;
            if (TryGetPropertyIgnoreCase(container, fieldName, out JsonElement value)) return value.ToString();
        }
        return TryGetPropertyIgnoreCase(row, fieldName, out JsonElement direct) ? direct.ToString() : "";
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
