using System.Net;
using System.Text;
using System.Text.Json;

namespace GameProfileManager.Application;

internal sealed record PcgamingWikiArticle(string Title, string Html, string Wikitext);
internal sealed record PcgamingWikiPageSearchResult(IReadOnlyList<string> Titles, bool ExactTitleFound);

/// <summary>Owns HTTP, MediaWiki API calls, and authentication; parsing is delegated to path strategies.</summary>
internal sealed class PcgamingWikiApiClient : IDisposable
{
    private const string ApiEndpoint = "https://www.pcgamingwiki.com/w/api.php";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    private PcgamingWikiApiClient(HttpClient client, bool ownsClient)
    {
        _client = client;
        _ownsClient = ownsClient;
    }

    private static readonly Lazy<PcgamingWikiApiClient> Anonymous = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        SetUserAgent(client);
        return new PcgamingWikiApiClient(client, ownsClient: true);
    });

    public static PcgamingWikiApiClient CreateAnonymous() => new(Anonymous.Value._client, ownsClient: false);

    public static PcgamingWikiApiClient CreateAuthenticated()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        SetUserAgent(client);
        return new PcgamingWikiApiClient(client, ownsClient: true);
    }

    public async Task LoginAsync(PcgamingWikiCredentials credentials, CancellationToken cancellationToken)
    {
        string tokenUrl = BuildUrl(("action", "query"), ("meta", "tokens"), ("type", "login"),
            ("format", "json"), ("formatversion", "2"));
        using HttpResponseMessage tokenResponse = await _client.GetAsync(tokenUrl, cancellationToken).ConfigureAwait(false);
        using JsonDocument tokenDocument = await ReadResponseAsync(tokenResponse, cancellationToken).ConfigureAwait(false);
        if (!tokenDocument.RootElement.TryGetProperty("query", out JsonElement query) ||
            !query.TryGetProperty("tokens", out JsonElement tokens) ||
            !tokens.TryGetProperty("logintoken", out JsonElement tokenElement))
            throw new InvalidDataException("PCGamingWiki не выдал токен входа для API.");

        using HttpResponseMessage loginResponse = await _client.PostAsync(ApiEndpoint,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["action"] = "login",
                ["lgname"] = credentials.Username,
                ["lgpassword"] = credentials.Password,
                ["lgtoken"] = tokenElement.GetString() ?? "",
                ["format"] = "json",
                ["formatversion"] = "2"
            }), cancellationToken).ConfigureAwait(false);
        using JsonDocument loginDocument = await ReadResponseAsync(loginResponse, cancellationToken).ConfigureAwait(false);
        if (!loginDocument.RootElement.TryGetProperty("login", out JsonElement login) ||
            !login.TryGetProperty("result", out JsonElement result) ||
            !result.ToString().Equals("Success", StringComparison.OrdinalIgnoreCase))
        {
            string reason = login.TryGetProperty("reason", out JsonElement reasonElement) ? reasonElement.ToString() :
                login.TryGetProperty("result", out JsonElement resultElement) ? resultElement.ToString() : "неизвестная ошибка";
            throw new UnauthorizedAccessException($"Вход в PCGamingWiki не выполнен ({reason}). Проверьте bot-имя и bot-пароль в настройках.");
        }
    }

    public async Task<PcgamingWikiPageSearchResult> FindPagesAsync(string title, CancellationToken cancellationToken)
    {
        string exactUrl = BuildUrl(("action", "query"), ("prop", "info"), ("redirects", "1"),
            ("format", "json"), ("formatversion", "2"), ("titles", title));
        using HttpResponseMessage exactResponse = await _client.GetAsync(exactUrl, cancellationToken).ConfigureAwait(false);
        using JsonDocument exactDocument = await ReadResponseAsync(exactResponse, cancellationToken).ConfigureAwait(false);
        string[] exact = ExtractPageTitles(exactDocument.RootElement);
        if (exact.Length > 0) return new PcgamingWikiPageSearchResult(exact, ExactTitleFound: true);

        string openSearchUrl = BuildUrl(("action", "opensearch"), ("redirects", "resolve"),
            ("format", "json"), ("profile", "fuzzy"), ("limit", "5"), ("search", title));
        using HttpResponseMessage searchResponse = await _client.GetAsync(openSearchUrl, cancellationToken).ConfigureAwait(false);
        using JsonDocument searchDocument = await ReadResponseAsync(searchResponse, cancellationToken).ConfigureAwait(false);
        string[] candidates = searchDocument.RootElement.ValueKind == JsonValueKind.Array &&
            searchDocument.RootElement.GetArrayLength() >= 2 && searchDocument.RootElement[1].ValueKind == JsonValueKind.Array
                ? searchDocument.RootElement[1].EnumerateArray().Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>().Take(5).ToArray()
                : [];
        candidates = PcgamingWikiTitleMatcher.FilterRelevant(title, candidates);
        if (candidates.Length > 0) return new PcgamingWikiPageSearchResult(candidates, ExactTitleFound: false);

        string fallbackUrl = BuildUrl(("action", "query"), ("list", "search"), ("srnamespace", "0"),
            ("srlimit", "5"), ("srenablerewrites", "1"), ("format", "json"), ("formatversion", "2"), ("srsearch", title));
        using HttpResponseMessage fallbackResponse = await _client.GetAsync(fallbackUrl, cancellationToken).ConfigureAwait(false);
        using JsonDocument fallbackDocument = await ReadResponseAsync(fallbackResponse, cancellationToken).ConfigureAwait(false);
        if (!fallbackDocument.RootElement.TryGetProperty("query", out JsonElement fallbackQuery) ||
            !fallbackQuery.TryGetProperty("search", out JsonElement searchResults) || searchResults.ValueKind != JsonValueKind.Array)
            return new PcgamingWikiPageSearchResult([], ExactTitleFound: false);
        candidates = searchResults.EnumerateArray()
            .Where(item => item.TryGetProperty("title", out _))
            .Select(item => item.GetProperty("title").GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        return new PcgamingWikiPageSearchResult(PcgamingWikiTitleMatcher.FilterRelevant(title, candidates), ExactTitleFound: false);
    }

    public async Task<PcgamingWikiArticle?> GetArticleAsync(string pageTitle, CancellationToken cancellationToken)
    {
        string url = BuildUrl(("action", "parse"), ("redirects", "1"), ("prop", "text|wikitext"),
            ("format", "json"), ("formatversion", "2"), ("page", pageTitle));
        using HttpResponseMessage response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("parse", out JsonElement parse)) return null;
        string title = parse.TryGetProperty("title", out JsonElement titleValue) ? titleValue.GetString() ?? pageTitle : pageTitle;
        return new PcgamingWikiArticle(title, GetApiText(parse, "text"), GetApiText(parse, "wikitext"));
    }

    public async Task<JsonDocument> GetCargoRowsAsync(string pageTitle, CancellationToken cancellationToken)
    {
        string pageIdUrl = BuildUrl(("action", "query"), ("prop", "info"), ("redirects", "1"),
            ("format", "json"), ("formatversion", "2"), ("titles", pageTitle));
        using HttpResponseMessage pageResponse = await _client.GetAsync(pageIdUrl, cancellationToken).ConfigureAwait(false);
        using JsonDocument pageDocument = await ReadResponseAsync(pageResponse, cancellationToken).ConfigureAwait(false);
        string? pageId = ExtractPageId(pageDocument.RootElement);
        if (string.IsNullOrWhiteSpace(pageId)) return JsonDocument.Parse("{\"cargoquery\":[]}");

        string cargoUrl = BuildUrl(("action", "cargoquery"), ("tables", "Game,GameData"),
            ("fields", "GameData.Type=Type,GameData.Platform=Platform,GameData.Paths=Paths"),
            ("join_on", "Game._pageID=GameData._pageID"),
            ("where", $"Game._pageID=\"{pageId}\""), ("limit", "100"),
            ("format", "json"), ("formatversion", "2"));
        using HttpResponseMessage cargoResponse = await _client.GetAsync(cargoUrl, cancellationToken).ConfigureAwait(false);
        JsonDocument cargoDocument = await ReadResponseAsync(cargoResponse, cancellationToken).ConfigureAwait(false);
        if (!cargoDocument.RootElement.TryGetProperty("cargoquery", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
        {
            cargoDocument.Dispose();
            throw new InvalidDataException("PCGamingWiki Cargo API не вернул таблицу GameData.");
        }
        if (rows.GetArrayLength() > 0) return cargoDocument;

        // Some pages have valid GameData rows that are not joined to the Game table. Retry
        // directly against GameData before treating an empty joined query as missing data.
        cargoDocument.Dispose();
        string directCargoUrl = BuildUrl(("action", "cargoquery"), ("tables", "GameData"),
            ("fields", "GameData.Type=Type,GameData.Platform=Platform,GameData.Paths=Paths"),
            ("where", $"GameData._pageID=\"{pageId}\""), ("limit", "100"),
            ("format", "json"), ("formatversion", "2"));
        using HttpResponseMessage directCargoResponse = await _client.GetAsync(directCargoUrl, cancellationToken).ConfigureAwait(false);
        JsonDocument directCargoDocument = await ReadResponseAsync(directCargoResponse, cancellationToken).ConfigureAwait(false);
        if (!directCargoDocument.RootElement.TryGetProperty("cargoquery", out JsonElement directRows) || directRows.ValueKind != JsonValueKind.Array)
        {
            directCargoDocument.Dispose();
            throw new InvalidDataException("PCGamingWiki Cargo API не вернул таблицу GameData.");
        }
        return directCargoDocument;
    }

    public async Task TestCargoAccessAsync(CancellationToken cancellationToken)
    {
        string url = BuildUrl(("action", "cargoquery"), ("tables", "GameData"),
            ("fields", "GameData._pageID=PageID"), ("limit", "1"), ("format", "json"), ("formatversion", "2"));
        using HttpResponseMessage response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("cargoquery", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("PCGamingWiki принял вход, но не вернул результат Cargo API.");
    }

    private static string[] ExtractPageTitles(JsonElement root)
    {
        if (!root.TryGetProperty("query", out JsonElement query) || !query.TryGetProperty("pages", out JsonElement pages)) return [];
        return EnumeratePages(pages).Where(page => !page.TryGetProperty("missing", out _) && page.TryGetProperty("title", out _))
            .Select(page => page.GetProperty("title").GetString()).Where(title => !string.IsNullOrWhiteSpace(title))
            .Cast<string>().Take(3).ToArray();
    }

    private static string? ExtractPageId(JsonElement root)
    {
        if (!root.TryGetProperty("query", out JsonElement query) || !query.TryGetProperty("pages", out JsonElement pages)) return null;
        JsonElement page = EnumeratePages(pages).FirstOrDefault(item => item.TryGetProperty("pageid", out _));
        if (page.ValueKind == JsonValueKind.Undefined || !page.TryGetProperty("pageid", out JsonElement id)) return null;
        string value = id.ToString();
        return value.Length > 0 && value.All(char.IsDigit) ? value : null;
    }

    private static IEnumerable<JsonElement> EnumeratePages(JsonElement pages) => pages.ValueKind switch
    {
        JsonValueKind.Array => pages.EnumerateArray(),
        JsonValueKind.Object => pages.EnumerateObject().Select(item => item.Value),
        _ => []
    };

    private static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        string bodyText = Encoding.UTF8.GetString(body);
        bool cloudflare = response.Headers.Contains("cf-ray") ||
            response.Headers.Server.Any(server => server.Product?.Name.Equals("cloudflare", StringComparison.OrdinalIgnoreCase) == true) ||
            bodyText.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) || bodyText.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase) ||
            bodyText.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase);
        if (!response.IsSuccessStatusCode)
        {
            string detail = cloudflare ? "Ответ содержит признаки блокировки или challenge Cloudflare; приложение не пытается обходить защиту." :
                response.StatusCode switch
                {
                    HttpStatusCode.Forbidden => "Сервер запретил запрос. PCGamingWiki указывает, что 403 может быть связан с User-Agent или ограничением IP.",
                    HttpStatusCode.TooManyRequests => "Сработало ограничение частоты запросов PCGamingWiki.",
                    _ => "Сервер не принял запрос к API."
                };
            string rayId = response.Headers.TryGetValues("cf-ray", out IEnumerable<string>? values) ? " Ray ID: " + string.Join(", ", values) : "";
            throw new HttpRequestException($"PCGamingWiki API вернул HTTP {(int)response.StatusCode} ({response.StatusCode}). {detail}{rayId}", null, response.StatusCode);
        }
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("error", out JsonElement error))
            {
                string code = error.TryGetProperty("code", out JsonElement codeValue) ? codeValue.ToString() : "API error";
                string info = error.TryGetProperty("info", out JsonElement infoValue) ? infoValue.ToString() : "без пояснения сервера";
                if (code.Equals("permissiondenied", StringComparison.OrdinalIgnoreCase))
                    info += " Для cargoquery создайте bot-пароль с разрешением «Create, query and delete data through the Cargo extension» в PCGamingWiki.";
                throw new PcgamingWikiApiException(code, info);
            }
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "не указан";
            string detail = cloudflare ? "Вместо JSON API пришла страница Cloudflare; защита не обходится." :
                $"Вместо JSON API пришёл ответ типа «{contentType}».";
            throw new InvalidDataException($"Не удалось прочитать ответ PCGamingWiki API. {detail}", ex);
        }
    }

    private static string GetApiText(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out JsonElement value)) return "";
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? "";
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("*", out JsonElement legacyValue) &&
            legacyValue.ValueKind == JsonValueKind.String ? legacyValue.GetString() ?? "" : "";
    }

    private static string BuildUrl(params (string Name, string Value)[] parameters) =>
        ApiEndpoint + "?" + string.Join("&", parameters.Select(item => Uri.EscapeDataString(item.Name) + "=" + Uri.EscapeDataString(item.Value)));

    private static void SetUserAgent(HttpClient client) =>
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GameProfileManager/2.0 (+https://github.com/ToPChik2505/GameProfileManager)");

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}

internal sealed class PcgamingWikiApiException(string code, string info) : Exception($"PCGamingWiki API вернул ошибку {code}: {info}")
{
    public string Code { get; } = code;
}
