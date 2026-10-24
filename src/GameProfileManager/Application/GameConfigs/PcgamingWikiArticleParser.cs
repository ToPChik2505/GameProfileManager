using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using GameProfileManager.Core;

namespace GameProfileManager.Application;

internal static class PcgamingWikiArticleParser
{
    private const string GameRootPlaceholder = "\uE000GPM_GAME_ROOT\uE001";
    private const string UserIdPlaceholder = "\uE002GPM_USER_ID\uE003";
    private const string ConfigurationHeadingPattern = @"Configuration file(?:\(s\)|s)? location";

    public static IReadOnlyList<string> ParseWikitext(string wikitext)
    {
        if (string.IsNullOrWhiteSpace(wikitext)) return [];
        string source = Regex.Replace(wikitext, @"(?is)<!--.*?-->", "");
        var paths = new List<string>();
        foreach (Match start in Regex.Matches(source, @"\{\{\s*Game[ _]+data\s*/\s*config\b", RegexOptions.IgnoreCase))
        {
            int end = FindTemplateEnd(source, start.Index);
            if (end < 0) continue;
            string[] arguments = SplitArguments(source[(start.Index + 2)..(end - 2)]).ToArray();
            if (arguments.Length < 3 ||
                !NormalizeTemplateName(arguments[0]).Equals("game data/config", StringComparison.OrdinalIgnoreCase) ||
                !arguments[1].Trim().StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (string argument in arguments.Skip(2))
            {
                string path = NormalizePathValue(argument);
                if (path.Length > 2 && !path.Contains("{{", StringComparison.Ordinal))
                    paths.Add(path);
            }
        }
        return paths.SelectMany(SplitPathList).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> ParseHtml(string html)
    {
        var paths = new List<string>();
        Match heading = Regex.Match(html, $"(?is)<h(?<level>[2-6])\\b[^>]*>.*?{ConfigurationHeadingPattern}.*?</h\\k<level>>");
        if (heading.Success)
        {
            int sectionStart = heading.Index + heading.Length;
            Match nextHeading = Regex.Match(html[sectionStart..], @"(?is)<h[2-6]\b");
            string section = html.Substring(sectionStart, nextHeading.Success ? nextHeading.Index : html.Length - sectionStart);
            ReadWindowsTableRows(section, paths);
            if (paths.Count == 0) ReadWindowsTextRows(section, paths);
        }
        if (paths.Count == 0) ReadConfigurationRowsFromAnyTable(html, paths);
        return paths.SelectMany(SplitPathList).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IEnumerable<string> SplitCargoPaths(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        string decoded = WebUtility.HtmlDecode(value);
        decoded = Regex.Replace(decoded, @"(?is)<br\s*/?>|</(?:li|p|div)>|<li\b[^>]*>|<p\b[^>]*>|<div\b[^>]*>", "\n");
        foreach (string line in decoded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        foreach (string segment in SplitArguments(line))
        {
            foreach (string path in SplitPathList(segment)) yield return path;
        }
    }

    /// <summary>
    /// Cargo can serialize several GameData.Paths entries into one comma-separated
    /// value. Split only when the next item begins with a recognizable rooted path,
    /// so commas inside directory or file names stay intact.
    /// </summary>
    internal static IReadOnlyList<string> SplitPathList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        string normalized = NormalizePathValue(value);
        return Regex.Split(normalized,
                @",\s*(?=(?:<\s*[^>]+>|%[^%]+%|[A-Za-z]:[\\/]|\\\\))",
                RegexOptions.IgnoreCase)
            .Select(NormalizePathValue)
            .Select(path => path.Trim())
            .Where(path => path.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ReadWindowsTableRows(string section, List<string> paths)
    {
        string currentSystem = "";
        foreach (Match row in Regex.Matches(section, @"(?is)<tr\b[^>]*>(.*?)</tr>"))
        {
            MatchCollection cells = Regex.Matches(row.Groups[1].Value, @"(?is)<t[dh]\b[^>]*>(.*?)</t[dh]>");
            if (cells.Count == 0) continue;
            int firstLocationCell;
            if (cells.Count >= 2)
            {
                currentSystem = PlainText(cells[0].Groups[1].Value);
                firstLocationCell = 1;
            }
            else firstLocationCell = 0;
            if (!currentSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Match cell in cells.Cast<Match>().Skip(firstLocationCell)) AddPathsFromCell(cell.Groups[1].Value, paths);
        }
    }

    private static void ReadWindowsTextRows(string section, List<string> paths)
    {
        string text = HtmlTextWithBoundaries(section);
        Match heading = Regex.Match(text, ConfigurationHeadingPattern, RegexOptions.IgnoreCase);
        if (!heading.Success) return;
        int end = text.IndexOf("Save game data location", heading.Index + heading.Length, StringComparison.OrdinalIgnoreCase);
        string[] lines = text[(heading.Index + heading.Length)..(end >= 0 ? end : text.Length)]
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            Match inline = Regex.Match(line, @"(?i)^Windows\s+(?<path>.+)$");
            if (inline.Success)
            {
                string path = NormalizePathValue(inline.Groups["path"].Value);
                if (path.Length > 2) paths.Add(path);
            }
            else if (line.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                for (int next = index + 1; next < lines.Length && !IsPlatformLabel(lines[next]); next++)
                {
                    string path = NormalizePathValue(lines[next]);
                    if (path.Length > 2) paths.Add(path);
                }
            }
        }
    }

    private static void ReadConfigurationRowsFromAnyTable(string html, List<string> paths)
    {
        foreach (Match table in Regex.Matches(html, @"(?is)<table\b[^>]*>(.*?)</table>"))
        {
            string currentSystem = "";
            foreach (Match row in Regex.Matches(table.Groups[1].Value, @"(?is)<tr\b[^>]*>(.*?)</tr>"))
            {
                MatchCollection cells = Regex.Matches(row.Groups[1].Value, @"(?is)<t[dh]\b[^>]*>(.*?)</t[dh]>");
                if (cells.Count == 0) continue;
                int firstLocationCell;
                if (cells.Count >= 2)
                {
                    currentSystem = PlainText(cells[0].Groups[1].Value);
                    firstLocationCell = 1;
                }
                else firstLocationCell = 0;
                if (!currentSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Match cell in cells.Cast<Match>().Skip(firstLocationCell))
                foreach (string line in HtmlTextWithBoundaries(cell.Groups[1].Value).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    string path = NormalizePathValue(line);
                    string extension = Path.GetExtension(Path.GetFileName(path));
                    if (path.Length > 2 && extension.Length > 0 && !GameConfigPathSafety.IsLikelyBinaryExtension(extension)) paths.Add(path);
                }
            }
        }
    }

    private static void AddPathsFromCell(string cell, List<string> paths)
    {
        foreach (string line in HtmlTextWithBoundaries(cell).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string path = NormalizePathValue(line);
            if (path.Length > 2) paths.Add(path);
        }
    }

    private static int FindTemplateEnd(string source, int start)
    {
        int depth = 0;
        for (int index = start; index < source.Length - 1; index++)
        {
            if (source[index] == '{' && source[index + 1] == '{') { depth++; index++; }
            else if (source[index] == '}' && source[index + 1] == '}')
            {
                depth--;
                index++;
                if (depth == 0) return index + 1;
            }
        }
        return -1;
    }

    private static IEnumerable<string> SplitArguments(string body)
    {
        int templateDepth = 0, linkDepth = 0, start = 0;
        for (int index = 0; index < body.Length; index++)
        {
            if (index + 1 < body.Length && body[index] == '{' && body[index + 1] == '{') { templateDepth++; index++; }
            else if (index + 1 < body.Length && body[index] == '}' && body[index + 1] == '}') { templateDepth = Math.Max(0, templateDepth - 1); index++; }
            else if (index + 1 < body.Length && body[index] == '[' && body[index + 1] == '[') { linkDepth++; index++; }
            else if (index + 1 < body.Length && body[index] == ']' && body[index + 1] == ']') { linkDepth = Math.Max(0, linkDepth - 1); index++; }
            else if (body[index] == '|' && templateDepth == 0 && linkDepth == 0)
            {
                yield return body[start..index].Trim();
                start = index + 1;
            }
        }
        yield return body[start..].Trim();
    }

    private static string FlattenWikitextPath(string value, int depth = 0)
    {
        if (depth >= 16) return value;
        var output = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length;)
        {
            if (index + 1 < value.Length && value[index] == '{' && value[index + 1] == '{')
            {
                int end = FindTemplateEnd(value, index);
                if (end > index)
                {
                    string[] args = SplitArguments(value[(index + 2)..(end - 2)]).ToArray();
                    string template = args.Length == 0 ? "" : NormalizeTemplateName(args[0]);
                    string replacement = template switch
                    {
                        "path" => string.Join("\\", args.Skip(1)),
                        "note" => "",
                        "p" => ExpandWikiVariable(args.Skip(1).FirstOrDefault() ?? ""),
                        _ => string.Join("|", args.Skip(1))
                    };
                    output.Append(FlattenWikitextPath(replacement, depth + 1));
                    index = end;
                    continue;
                }
            }
            if (index + 1 < value.Length && value[index] == '[' && value[index + 1] == '[')
            {
                int end = value.IndexOf("]]", index + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    string[] pieces = SplitArguments(value[(index + 2)..end]).ToArray();
                    output.Append(FlattenWikitextPath(pieces.LastOrDefault() ?? "", depth + 1));
                    index = end + 2;
                    continue;
                }
            }
            output.Append(value[index++]);
        }
        return WebUtility.HtmlDecode(output.ToString()).Trim();
    }

    private static string NormalizeTemplateName(string value) => Regex.Replace(value.Replace('_', ' '), @"\s+", " ").Trim().ToLowerInvariant();

    private static string ExpandWikiVariable(string value) => NormalizeTemplateName(value) switch
    {
        "game" or "gameroot" => "<path-to-game>",
        "userid" or "user-id" or "user id" => "<user-id>",
        "userprofile" => "%USERPROFILE%",
        "documents" or "userdocuments" => "%USERPROFILE%\\Documents",
        "savedgames" or "saved games" => "%USERPROFILE%\\Saved Games",
        "localappdata" => "%LOCALAPPDATA%",
        "appdata" or "roamingappdata" => "%APPDATA%",
        _ => value
    };

    internal static string CleanPath(string path)
    {
        path = Regex.Replace(path, @"\s+\(Windows:[^)]*\)", "", RegexOptions.IgnoreCase).Trim();
        // PCGamingWiki renders reference templates as ^{[Note 1]}. If the source
        // contains that notation literally, removing only [Note 1] leaves ^{},
        // which gets appended to the filename and prevents a local file match.
        path = Regex.Replace(path, @"\s*\^\s*\{\s*\[(?:Note\s*)?\d+\]\s*\}", "", RegexOptions.IgnoreCase);
        path = Regex.Replace(path, @"\[(?:Note\s*)?\d+\]", "", RegexOptions.IgnoreCase);
        return path.Trim(' ', '\t', '"', '\'', '`');
    }

    internal static string NormalizePathValue(string value)
    {
        string normalized = CleanPath(FlattenWikitextPath(HtmlTextWithBoundaries(value)));
        // Some article/HTML variants expose the system token without percent
        // delimiters. Normalize it back to the documented environment variable.
        return Regex.Replace(normalized, @"^%?USERPROFILE%?(?=[\\/])", "%USERPROFILE%", RegexOptions.IgnoreCase);
    }

    private static bool IsPlatformLabel(string line) => line.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("Steam Play", StringComparison.OrdinalIgnoreCase) || line.Equals("Linux", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("macOS", StringComparison.OrdinalIgnoreCase) || line.Equals("OS X", StringComparison.OrdinalIgnoreCase) ||
        line.Equals("DOS", StringComparison.OrdinalIgnoreCase);

    private static string PlainText(string html)
    {
        string value = ProtectPlaceholders(html);
        value = Regex.Replace(value, @"(?is)<br\s*/?>", "\n");
        value = Regex.Replace(value, @"(?is)<[^>]+>", "");
        return RestorePlaceholders(WebUtility.HtmlDecode(value)).Replace('\u00a0', ' ').Trim();
    }

    private static string HtmlTextWithBoundaries(string html)
    {
        string value = ProtectPlaceholders(html);
        value = Regex.Replace(value, @"(?is)<br\s*/?>", "\n");
        value = Regex.Replace(value, @"(?is)</(?:h[1-6]|tr|td|th|p|li|div|table)\s*>", "\n");
        value = Regex.Replace(value, @"(?is)<[^>]+>", "");
        return RestorePlaceholders(WebUtility.HtmlDecode(value)).Replace('\u00a0', ' ');
    }

    private static string ProtectPlaceholders(string html)
    {
        string protectedValue = Regex.Replace(html,
            @"(?:&lt;|<)\s*path-to-game\s*(?:&gt;|>)", GameRootPlaceholder, RegexOptions.IgnoreCase);
        return Regex.Replace(protectedValue,
            @"(?:&lt;|<)\s*user(?:[-_\s]?id)\s*(?:&gt;|>)", UserIdPlaceholder, RegexOptions.IgnoreCase);
    }

    private static string RestorePlaceholders(string value) => value
        .Replace(GameRootPlaceholder, "<path-to-game>", StringComparison.OrdinalIgnoreCase)
        .Replace(UserIdPlaceholder, "<user-id>", StringComparison.OrdinalIgnoreCase);
}
