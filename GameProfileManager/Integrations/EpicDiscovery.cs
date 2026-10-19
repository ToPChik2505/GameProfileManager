using System.Text.Json;
using GameProfileManager.Core;

namespace GameProfileManager.Integrations;

public static class EpicDiscovery
{
    public static IReadOnlyList<GameEntry> FindInstalled(string? manifestsDirectory = null)
    {
        string directory = manifestsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(directory)) return [];

        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(directory, "*.item"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement item = document.RootElement;
                if (item.ValueKind != JsonValueKind.Object ||
                    FlagIs(item, "bIsIncompleteInstall", true) ||
                    FlagIs(item, "bIsApplication", false) ||
                    FlagIs(item, "bIsExecutable", false) ||
                    !IsGame(item) ||
                    !string.IsNullOrWhiteSpace(Field(item, "MainGameAppName"))) continue;

                string? appName = Field(item, "AppName");
                string? name = Field(item, "DisplayName");
                string? install = Field(item, "InstallLocation");
                if (string.IsNullOrWhiteSpace(appName) || string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(install) || !Directory.Exists(install) ||
                    !seen.Add(appName)) continue;

                string? executable = GameExecutableDiscovery.FindBest(install, Field(item, "LaunchExecutable"));
                games.Add(new GameEntry
                {
                    Name = name,
                    EpicAppName = appName,
                    InstallDirectory = install,
                    ExePath = executable ?? "",
                    Detail = executable is null ? "Выберите игровой .exe (EGS: несколько кандидатов или файл не найден)" : "Выберите профиль"
                });
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return games;
    }

    private static bool IsGame(JsonElement item)
    {
        if (Field(item, "TechnicalType") is string type &&
            type.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(value => value.Equals("games", StringComparison.OrdinalIgnoreCase))) return true;
        return item.TryGetProperty("AppCategories", out JsonElement categories) &&
               categories.ValueKind == JsonValueKind.Array &&
               categories.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String &&
                   value.GetString()!.Equals("games", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Field(JsonElement item, string key) =>
        item.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool FlagIs(JsonElement item, string key, bool value) =>
        item.TryGetProperty(key, out JsonElement flag) &&
        (value ? flag.ValueKind == JsonValueKind.True : flag.ValueKind == JsonValueKind.False);
}
