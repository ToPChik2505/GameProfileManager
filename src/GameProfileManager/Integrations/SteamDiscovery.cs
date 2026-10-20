using System.Text.RegularExpressions;
using System.Security;
using GameProfileManager.Core;
using Microsoft.Win32;

namespace GameProfileManager.Integrations;

public static class SteamDiscovery
{
    private static readonly Regex Field = new("\"(?<key>[^\"]+)\"\\s+\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
    // Манифесты Steam содержат и программы; эти ID принадлежат известным утилитам.
    public static readonly IReadOnlySet<string> NonGameAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "228980", // Steamworks Common Redistributables
        "1812620", // DSX
        "993090"  // Lossless Scaling
    };
    public static IReadOnlyList<GameEntry> FindInstalled(string? steamDirectory = null)
    {
        string? steam = steamDirectory ?? FindSteamDirectory();
        if (string.IsNullOrWhiteSpace(steam) || !Directory.Exists(steam)) return [];
        var appInfo = new SteamAppInfoCache(Path.Combine(steam, "appcache", "appinfo.vdf"));
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steam };
        string foldersFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(foldersFile))
        {
            string text = File.ReadAllText(foldersFile);
            foreach (Match match in Field.Matches(text))
                if (match.Groups["key"].Value.Equals("path", StringComparison.OrdinalIgnoreCase))
                    libraries.Add(Unescape(match.Groups["value"].Value));
        }
        var games = new List<GameEntry>();
        var seenAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string library in libraries)
        {
            string steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            foreach (string manifest in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    var fields = Field.Matches(File.ReadAllText(manifest)).Cast<Match>().ToArray();
                    string? appid = FirstValue(fields, "appid");
                    string? name = FirstValue(fields, "name");
                    string? install = FirstValue(fields, "installdir");
                    if (string.IsNullOrWhiteSpace(appid) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(install) ||
                        NonGameAppIds.Contains(appid)) continue;
                    string? appType = appInfo.GetType(appid);
                    if (appType is not null && !appType.Equals("Game", StringComparison.OrdinalIgnoreCase) &&
                        !appType.Equals("Demo", StringComparison.OrdinalIgnoreCase)) continue;
                    string directory = Path.Combine(steamapps, "common", install);
                    if (!Directory.Exists(directory) || !seenAppIds.Add(appid)) continue;
                    string? executable = GameExecutableDiscovery.FindBest(directory);
                    games.Add(new GameEntry
                    {
                        Name = name, SteamAppId = appid, InstallDirectory = directory,
                        ExePath = executable ?? "",
                        Detail = executable is not null ? "Выберите профиль" : "Выберите игровой .exe (Steam не указывает его в манифесте)"
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
        }
        return games;
    }

    public static string? FindArtwork(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || appId.Any(c => !char.IsAsciiDigit(c))) return null;
        string? steam = FindSteamDirectory();
        if (steam is null) return null;
        string cache = Path.Combine(steam, "appcache", "librarycache", appId);
        foreach (string file in new[] { "library_600x900.jpg", "library_header.jpg", "header.jpg" })
        {
            string path = Path.Combine(cache, file);
            if (File.Exists(path)) return path;
        }
        // Для некоторых игр Steam хранит только значок 32x32 с хешем вместо имени.
        try { return Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "*.jpg").FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path) is string name && name.Length == 40 && name.All(Uri.IsHexDigit)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? FindSteamDirectory()
    {
        string?[] candidates =
        [
            ReadRegistryPath(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            ReadRegistryPath(Registry.CurrentUser, @"Software\Valve\Steam", "InstallPath"),
            ReadRegistryPath(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        ];
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "steamapps")));
    }

    private static string? ReadRegistryPath(RegistryKey hive, string keyPath, string valueName)
    {
        try { using var key = hive.OpenSubKey(keyPath); return key?.GetValue(valueName) as string; }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException) { return null; }
    }

    private static string? FirstValue(IEnumerable<Match> fields, string key)
    {
        var match = fields.FirstOrDefault(m => m.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : Unescape(match.Groups["value"].Value);
    }

    private static string Unescape(string value) => value.Replace("\\\\", "\\");
}
