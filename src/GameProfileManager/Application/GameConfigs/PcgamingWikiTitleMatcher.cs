using System.Text;
using System.Text.RegularExpressions;

namespace GameProfileManager.Application;

internal static class PcgamingWikiTitleMatcher
{
    public static string[] FilterRelevant(string query, IEnumerable<string> pages)
    {
        string[] queryTokens = Tokenize(query).Where(token => token.Length >= 3).ToArray();
        return pages.Where(page =>
            {
                string[] pageTokens = Tokenize(page).Where(token => token.Length > 0).ToArray();
                if (queryTokens.Length == 0)
                    return Normalize(page).Contains(Normalize(query), StringComparison.OrdinalIgnoreCase);
                return queryTokens.All(token => pageTokens.Any(pageToken => pageToken.Length >= 3 &&
                    EditDistanceAtMost(token, pageToken, token.Length >= 7 ? 2 : 1)) || MatchesAcronym(token, pageTokens));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray();
    }

    private static string[] Tokenize(string value)
    {
        // Installation folders and executable metadata commonly concatenate title words.
        // Split the standard camel-case boundaries before comparing them to wiki page titles.
        string readable = Regex.Replace(value, @"(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})", " ");
        readable = Regex.Replace(readable, @"(?<=\p{Lu})(?=\p{Lu}\p{Ll})", " ");
        return Regex.Split(readable, @"[^\p{L}\p{N}]+").Where(token => token.Length > 0).ToArray();
    }

    private static bool MatchesAcronym(string token, string[] words)
    {
        if (token.Length < 3) return false;
        for (int start = 0; start < words.Length; start++)
        {
            var acronym = new StringBuilder();
            for (int end = start; end < words.Length; end++)
            {
                if (words[end].Length > 0 && char.IsLetterOrDigit(words[end][0])) acronym.Append(words[end][0]);
                if (acronym.Length >= 2 && acronym.ToString().Equals(token, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static bool EditDistanceAtMost(string left, string right, int maximum)
    {
        if (Math.Abs(left.Length - right.Length) > maximum) return false;
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];
        for (int row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            int minimum = current[0];
            for (int column = 1; column <= right.Length; column++)
            {
                int substitution = previous[column - 1] +
                    (char.ToUpperInvariant(left[row - 1]) == char.ToUpperInvariant(right[column - 1]) ? 0 : 1);
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1), substitution);
                minimum = Math.Min(minimum, current[column]);
            }
            if (minimum > maximum) return false;
            (previous, current) = (current, previous);
        }
        return previous[right.Length] <= maximum;
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
}
