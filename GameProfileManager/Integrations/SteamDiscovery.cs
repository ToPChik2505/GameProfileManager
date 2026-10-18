using System.Text.RegularExpressions;
using GameProfileManager.Core;
using Microsoft.Win32;

namespace GameProfileManager.Integrations;

public static class SteamDiscovery
{
    private static readonly Regex Field = new("\"(?<key>[^\"]+)\"\\s+\"(?<value>(?:\\\\.|[^\"])*)\"", RegexOptions.Compiled);
    // Steam manifests also describe tools. These installed app IDs are known utilities.
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
                    var candidates = EnumerateCandidates(directory).ToArray();
                    string? executable = PickExecutable(candidates, directory);
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
        // Some installed games only have Steam's hash-named 32x32 icon cached.
        try { return Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "*.jpg").FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path) is string name && name.Length == 40 && name.All(Uri.IsHexDigit)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? PickExecutable(string[] candidates, string directory)
    {
        if (candidates.Length == 1) return candidates[0];
        if (candidates.Length == 0) return null;
        var ranked = candidates.Select(path => (Path: path, Score: ScoreExecutable(path, directory)))
            .OrderByDescending(candidate => candidate.Score).ToArray();
        return (ranked[0].Score >= 30 && ranked[0].Score - ranked[1].Score >= 20) ||
               (ranked[0].Score >= 0 && ranked[1].Score <= -30 && ranked[0].Score - ranked[1].Score >= 35)
            ? ranked[0].Path : null;
    }

    private static int ScoreExecutable(string path, string directory)
    {
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        string relative = Path.GetRelativePath(directory, path).Replace('\\', '/').ToLowerInvariant();
        int score = 0;
        if (name.Contains("shipping")) score += 75;
        if (relative.Contains("/binaries/win64/")) score += 35;
        if (relative.StartsWith("bin64/") || relative.Contains("/x64/")) score += 30;
        if (name.Equals(Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase)) score += 25;
        if (Path.GetDirectoryName(path)!.Equals(directory, StringComparison.OrdinalIgnoreCase)) score += 10;
        if (name.EndsWith("_be")) score -= 50;
        return score;
    }

    private static string? FindSteamDirectory()
    {
        using var currentUser = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        using var localMachine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        string?[] candidates =
        [
            currentUser?.GetValue("SteamPath") as string,
            currentUser?.GetValue("InstallPath") as string,
            localMachine?.GetValue("InstallPath") as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
        ];
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(Path.Combine(path, "steamapps")));
    }

    private static string? FirstValue(IEnumerable<Match> fields, string key)
    {
        var match = fields.FirstOrDefault(m => m.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : Unescape(match.Groups["value"].Value);
    }

    private static IEnumerable<string> EnumerateCandidates(string directory)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((directory, 0));
        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.exe").ToArray(); }
            catch { continue; }
            foreach (string file in files)
            {
                string name = Path.GetFileName(file).ToLowerInvariant();
                if (new[] { "unins", "crash", "setup", "redist", "launcher", "webhelper", "cefsubprocess", "report", "7za", "compiler", "d3dconfig", "breakpad", "bootstrap", "installer", "service", "browser", "protected_game", "playgtav", "dedicated_server" }
                    .Any(name.Contains)) continue;
                yield return file;
            }
            if (depth >= 3) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(folder).ToArray(); }
            catch { continue; }
            foreach (string child in children)
            {
                string name = Path.GetFileName(child).ToLowerInvariant();
                if (name.Contains("redist") || name.Contains("support") || name.Contains("installer") || name.Contains("directx")) continue;
                pending.Push((child, depth + 1));
            }
        }
    }
    private static string Unescape(string value) => value.Replace("\\\\", "\\");
}
