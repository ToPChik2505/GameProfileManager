using System.Text.Json;
using GameProfileManager.Core;

namespace GameProfileManager.Integrations;

public static class EpicDiscovery
{
    public static IReadOnlyList<GameEntry> FindInstalled(string? manifestsDirectory = null) =>
        DiscoverInstalled(manifestsDirectory).Games;

    public static StoreGameDiscoverySnapshot DiscoverInstalled(string? manifestsDirectory = null)
    {
        string directory = manifestsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(directory))
            return new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), false);

        var games = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presentAppNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool complete = true;
        string[] manifests;
        try { manifests = Directory.GetFiles(directory, "*.item"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new([], presentAppNames, false);
        }
        foreach (string path in manifests)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement item = document.RootElement;
                if (item.ValueKind != JsonValueKind.Object) { complete = false; continue; }
                string? appName = Field(item, "AppName");
                if (!string.IsNullOrWhiteSpace(appName)) presentAppNames.Add(appName);
                else complete = false;
                if (!IsGame(item) || !string.IsNullOrWhiteSpace(Field(item, "MainGameAppName"))) continue;
                if (string.IsNullOrWhiteSpace(appName)) continue;
                if (FlagIs(item, "bIsIncompleteInstall", true) ||
                    FlagIs(item, "bIsApplication", false) ||
                    FlagIs(item, "bIsExecutable", false)) continue;

                string? name = Field(item, "DisplayName");
                string? install = Field(item, "InstallLocation");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(install))
                { complete = false; continue; }
                if (!Directory.Exists(install) || !seen.Add(appName)) continue;

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
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { complete = false; }
        }
        return new(games, presentAppNames, complete);
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
