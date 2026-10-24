using System.Text.RegularExpressions;

namespace GameProfileManager.Application;

internal static class PcgamingWikiPathResolver
{
    public static string? GetReferencedFileName(string wikiPath)
    {
        string value = PcgamingWikiArticleParser.NormalizePathValue(wikiPath)
            .Replace('/', '\\').Trim();
        value = Regex.Replace(value, @"\s+\([^)]*\)", "").TrimEnd('\\');
        string fileName = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains('<') || fileName.Contains('>') ||
            string.IsNullOrWhiteSpace(Path.GetExtension(fileName)) ||
            GameConfigPathSafety.IsLikelyBinaryExtension(Path.GetExtension(fileName))) return null;
        return fileName;
    }

    public static bool RefersToGameInstall(string wikiPath) =>
        Regex.IsMatch(PcgamingWikiArticleParser.NormalizePathValue(wikiPath),
            @"<\s*path-to-game\s*>|(?:the\s+)?base installation folder", RegexOptions.IgnoreCase);

    public static IEnumerable<string> Expand(string wikiPath, string? installDirectory)
    {
        string? value = Resolve(wikiPath, installDirectory);
        if (value is not null && File.Exists(value))
        {
            if (GameConfigPathSafety.ValidateCandidate(value, allowBinary: false, out _)) yield return Path.GetFullPath(value);
            yield break;
        }
        if (value is not null && Directory.Exists(value))
        {
            foreach (string file in LocalGameConfigSearch.EnumerateConfigFiles(value))
                if (GameConfigPathSafety.ValidateCandidate(file, allowBinary: false, out _)) yield return Path.GetFullPath(file);
            yield break;
        }
        if (TryExpandUserIdDirectory(wikiPath, installDirectory, out IReadOnlyList<string> userIdFiles))
        {
            foreach (string file in userIdFiles) yield return file;
            yield break;
        }
        if (value is null) yield break;
        string pattern = Path.GetFileName(value);
        if (pattern.IndexOfAny(['*', '?']) >= 0)
        {
            if (GameConfigPathSafety.IsLikelyBinaryExtension(Path.GetExtension(pattern))) yield break;
            string? directory = Path.GetDirectoryName(value);
            if (!Directory.Exists(directory)) yield break;
            string[] matches;
            try { matches = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Take(128).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DirectoryNotFoundException) { matches = []; }
            foreach (string match in matches)
                if (GameConfigPathSafety.ValidateCandidate(match, allowBinary: false, out _)) yield return Path.GetFullPath(match);
            yield break;
        }
    }

    private static bool TryExpandUserIdDirectory(string wikiPath, string? installDirectory,
        out IReadOnlyList<string> files)
    {
        string value = PcgamingWikiArticleParser.NormalizePathValue(wikiPath).Replace('/', '\\').Trim();
        value = Regex.Replace(value, @"\s+\([^)]*\)", "").Trim();
        Match marker = Regex.Match(value, @"[\\/](?:<\s*user-id\s*>|uid)[\\/]*$", RegexOptions.IgnoreCase);
        if (!marker.Success)
        {
            files = [];
            return false;
        }

        string parentPath = value[..marker.Index].TrimEnd('\\', '/');
        string? parent = Resolve(parentPath, installDirectory);
        if (parent is null || !Directory.Exists(parent))
        {
            files = [];
            return true;
        }

        var matches = new List<string>();
        string[] profileDirectories;
        try { profileDirectories = Directory.EnumerateDirectories(parent, "*", SearchOption.TopDirectoryOnly).Take(128).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            files = [];
            return true;
        }
        foreach (string profileDirectory in profileDirectories)
        foreach (string candidate in LocalGameConfigSearch.EnumerateConfigFiles(profileDirectory))
            if (GameConfigPathSafety.ValidateCandidate(candidate, allowBinary: false, out _))
                matches.Add(Path.GetFullPath(candidate));
        files = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return true;
    }

    public static string? Resolve(string wikiPath, string? installDirectory)
    {
        string value = PcgamingWikiArticleParser.NormalizePathValue(wikiPath).Replace('/', '\\').Trim();
        value = Regex.Replace(value, @"\s+\(Windows:[^)]*\)", "", RegexOptions.IgnoreCase).Trim();
        Match gameRoot = Regex.Match(value, @"<path-to-game>\s*(?:\([^)]*\))?", RegexOptions.IgnoreCase);
        if (gameRoot.Success)
        {
            if (string.IsNullOrWhiteSpace(installDirectory)) return null;
            string relative = value[(gameRoot.Index + gameRoot.Length)..].TrimStart('\\', '/');
            value = Path.Combine(installDirectory, relative);
        }
        else if (Regex.IsMatch(value, @"^(?:the\s+)?base installation folder(?:\s*\([^)]*\))?", RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(installDirectory)) return null;
            string relative = Regex.Replace(value, @"^(?:the\s+)?base installation folder(?:\s*\([^)]*\))?", "", RegexOptions.IgnoreCase)
                .TrimStart('\\', '/');
            value = Path.Combine(installDirectory, relative);
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents)) documents = Path.Combine(userProfile, "Documents");
        value = value.Replace("%USERPROFILE%\\Documents", documents, StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%\\Saved Games", LocalGameConfigSearch.GetSavedGamesDirectory(), StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%", userProfile, StringComparison.OrdinalIgnoreCase);
        if (value.Contains('<') || value.Contains('>')) return null;
        value = Regex.Replace(value, @"\s+\([^)]*\)", "").Trim();
        if (!Path.IsPathRooted(value))
        {
            string extension = Path.GetExtension(value);
            if (string.IsNullOrWhiteSpace(installDirectory) || value.Contains('\\') || value.Contains('/') ||
                string.IsNullOrWhiteSpace(extension) || GameConfigPathSafety.IsLikelyBinaryExtension(extension)) return null;
            value = Path.Combine(installDirectory, value);
        }
        string fileName = Path.GetFileName(value);
        if (fileName.IndexOfAny(['*', '?']) >= 0)
        {
            string extension = Path.GetExtension(fileName);
            string? directory = Path.GetDirectoryName(value);
            if (string.IsNullOrWhiteSpace(extension) || GameConfigPathSafety.IsLikelyBinaryExtension(extension) ||
                string.IsNullOrWhiteSpace(directory) || !GameConfigPathSafety.ValidateConfigurationDirectory(directory, out _))
                return null;
            // Validate the existing parent directory here; the wildcard itself is not
            // a file path. Expand() validates each matching file before returning it.
            return Path.Combine(Path.GetFullPath(directory), fileName);
        }
        if (Directory.Exists(value))
            return GameConfigPathSafety.ValidateConfigurationDirectory(value, out _) ? Path.GetFullPath(value) : null;
        if (!GameConfigPathSafety.ValidateCandidate(value, allowBinary: false, out _)) return null;
        return Path.GetFullPath(value);
    }
}
