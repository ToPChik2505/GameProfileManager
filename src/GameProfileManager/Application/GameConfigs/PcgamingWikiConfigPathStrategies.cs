using System.Text.Json;

namespace GameProfileManager.Application;

internal interface IPcgamingWikiConfigPathStrategy
{
    string DefinitionId { get; }
    Task<IReadOnlyList<string>> FindWindowsPathsAsync(PcgamingWikiApiClient api, string pageTitle,
        CancellationToken cancellationToken);
}

/// <summary>Uses the public article parse endpoint and falls back from wikitext templates to rendered table HTML.</summary>
internal sealed class PcgamingWikiArticlePathStrategy : IPcgamingWikiConfigPathStrategy
{
    public string DefinitionId => "PCGamingWiki";

    public async Task<IReadOnlyList<string>> FindWindowsPathsAsync(PcgamingWikiApiClient api, string pageTitle,
        CancellationToken cancellationToken)
    {
        PcgamingWikiArticle? article = await api.GetArticleAsync(pageTitle, cancellationToken).ConfigureAwait(false);
        if (article is null) return [];
        if (article.Html.Length == 0 && article.Wikitext.Length == 0)
            throw new InvalidDataException($"PCGamingWiki вернул статью «{article.Title}» без HTML и исходного вики-текста.");
        IReadOnlyList<string> paths = PcgamingWikiArticleParser.ParseWikitext(article.Wikitext);
        return paths.Count > 0 || article.Html.Length == 0 ? paths : PcgamingWikiArticleParser.ParseHtml(article.Html);
    }
}

/// <summary>Uses the authenticated Cargo GameData table and a parser dedicated to its row shape.</summary>
internal sealed class PcgamingWikiCargoPathStrategy : IPcgamingWikiConfigPathStrategy
{
    public string DefinitionId => "PCGamingWiki:GameData";

    public async Task<IReadOnlyList<string>> FindWindowsPathsAsync(PcgamingWikiApiClient api, string pageTitle,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await api.GetCargoRowsAsync(pageTitle, cancellationToken).ConfigureAwait(false);
        return PcgamingWikiCargoParser.Parse(document.RootElement);
    }
}
