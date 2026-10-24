using System.Net.Http;
using System.Text.Json;

namespace GameProfileManager;

internal sealed record GitHubRelease(string Tag, Version Version);

internal sealed class GitHubReleaseLookup(HttpClient client)
{
    internal async Task<GitHubRelease> LatestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync(
            "https://api.github.com/repos/Vladislav2505/GameProfileManager/releases/latest",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        string tag = json.RootElement.GetProperty("tag_name").GetString()
            ?? throw new FormatException("У опубликованной версии нет номера.");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new FormatException("У опубликованной версии неверный номер.");
        return new GitHubRelease(tag, version);
    }
}
