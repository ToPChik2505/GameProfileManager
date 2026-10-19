namespace GameProfileManager.Integrations;

internal static class GameExecutableDiscovery
{
    public static string? FindBest(string directory, string? preferredRelativePath = null)
    {
        string? preferred = ResolvePreferred(directory, preferredRelativePath);
        var candidates = EnumerateCandidates(directory).ToList();
        if (preferred is not null && !IsExcluded(preferred) &&
            !candidates.Contains(preferred, StringComparer.OrdinalIgnoreCase)) candidates.Add(preferred);
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];
        var ranked = candidates.Select(path => (Path: path, Score: Score(path, directory) +
            (path.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? 45 : 0)))
            .OrderByDescending(candidate => candidate.Score).ToArray();
        return (ranked[0].Score >= 30 && ranked[0].Score - ranked[1].Score >= 20) ||
               (ranked[0].Score >= 0 && ranked[1].Score <= -30 && ranked[0].Score - ranked[1].Score >= 35)
            ? ranked[0].Path : null;
    }

    private static string? ResolvePreferred(string directory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath)) return null;
        try
        {
            string path = Path.GetFullPath(Path.Combine(directory, relativePath));
            string relative = Path.GetRelativePath(directory, path);
            return relative is not ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                   Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
                ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static int Score(string path, string directory)
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

    private static IEnumerable<string> EnumerateCandidates(string directory)
    {
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((directory, 0));
        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(folder, "*.exe").ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string file in files)
            {
                string name = Path.GetFileName(file).ToLowerInvariant();
                if (IsExcluded(file)) continue;
                yield return file;
            }
            if (depth >= 3) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(folder).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string child in children)
            {
                string name = Path.GetFileName(child).ToLowerInvariant();
                if (name.Contains("redist") || name.Contains("support") || name.Contains("installer") || name.Contains("directx")) continue;
                pending.Push((child, depth + 1));
            }
        }
    }

    private static bool IsExcluded(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        return new[] { "unins", "crash", "setup", "redist", "launcher", "webhelper", "cefsubprocess", "report", "7za", "compiler", "d3dconfig", "breakpad", "bootstrap", "installer", "service", "browser", "protected_game", "playgtav", "dedicated_server" }
            .Any(name.Contains);
    }
}
