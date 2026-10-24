using System.Diagnostics;
using System.Text.RegularExpressions;
using GameProfileManager.Core;

namespace GameProfileManager.Application;

/// <summary>Searches PCGamingWiki and delegates path extraction to the selected parser strategy.</summary>
internal sealed class PcgamingWikiConfigSearch(IPcgamingWikiCredentialStore? credentialStore)
{
    private const int MaxCandidates = 32;
    private readonly IPcgamingWikiCredentialStore? _credentialStore = credentialStore;

    public async Task<IReadOnlyList<GameConfigCandidate>> FindCandidatesAsync(GameEntry game,
        CancellationToken cancellationToken)
    {
        PcgamingWikiCredentials? credentials = _credentialStore?.Read();
        PcgamingWikiApiClient api;
        IPcgamingWikiConfigPathStrategy strategy;
        if (credentials is null)
        {
            api = PcgamingWikiApiClient.CreateAnonymous();
            strategy = new PcgamingWikiArticlePathStrategy();
        }
        else
        {
            api = PcgamingWikiApiClient.CreateAuthenticated();
            strategy = new PcgamingWikiCargoPathStrategy();
        }
        using (api)
        {
            if (credentials is not null) await api.LoginAsync(credentials, cancellationToken).ConfigureAwait(false);
            return await FindCandidatesAsync(game, api, strategy, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task TestAccessAsync(CancellationToken cancellationToken)
    {
        PcgamingWikiCredentials credentials = _credentialStore?.Read()
            ?? throw new InvalidOperationException("Сначала сохраните bot-имя и пароль PCGamingWiki в настройках.");
        using PcgamingWikiApiClient api = PcgamingWikiApiClient.CreateAuthenticated();
        await api.LoginAsync(credentials, cancellationToken).ConfigureAwait(false);
        await api.TestCargoAccessAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<GameConfigCandidate>> FindCandidatesAsync(GameEntry game,
        PcgamingWikiApiClient api, IPcgamingWikiConfigPathStrategy strategy, CancellationToken cancellationToken)
    {
        string? installDirectory = LocalGameConfigSearch.GetInstallDirectory(game);
        string? successfullyParsedPage = null;
        string? successfullyParsedSource = null;
        bool anyPageFound = false;
        int wikiWindowsPaths = 0;
        int localFilesFound = 0;
        var observedWikiPaths = new List<string>();
        string[] gameRoots = GetGamePathRoots(game, installDirectory);
        string?[] pathResolutionRoots = gameRoots.Length > 0
            ? gameRoots.Cast<string?>().ToArray()
            : [installDirectory];

        foreach (string query in GetOnlineQueries(game, installDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PcgamingWikiPageSearchResult pageSearch = await api.FindPagesAsync(query, cancellationToken).ConfigureAwait(false);
            string[] pages = pageSearch.Titles.ToArray();
            anyPageFound |= pages.Length > 0;
            bool exactPageFound = pageSearch.ExactTitleFound;
            if (pages.Length == 0) continue;
            bool queryHasEditionSuffix = !LocalGameConfigSearch.StripGameEditionSuffix(query).Equals(query, StringComparison.OrdinalIgnoreCase);
            string normalizedQuery = Normalize(query);
            string? exactPage = pages.FirstOrDefault(page => Normalize(page).Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase));
            if (queryHasEditionSuffix && !pageSearch.ExactTitleFound && exactPage is null) continue;
            string[] selectedPages = exactPage is not null
                ? [exactPage]
                : pageSearch.ExactTitleFound
                    ? pages.Distinct(StringComparer.OrdinalIgnoreCase).Take(1).ToArray()
                    : [];
            // Broad MediaWiki search for a short base title can return several different
            // games (for example, numbered sequels). Never parse those pages as fallbacks:
            // only an exact normalized title or an API-confirmed title/redirect is safe.
            if (selectedPages.Length == 0) continue;
            foreach (string page in selectedPages)
            {
                IReadOnlyList<string> rawPaths;
                IPcgamingWikiConfigPathStrategy usedStrategy = strategy;
                try { rawPaths = await usedStrategy.FindWindowsPathsAsync(api, page, cancellationToken).ConfigureAwait(false); }
                catch (PcgamingWikiApiException ex) when (ex.Code.Equals("missingtitle", StringComparison.OrdinalIgnoreCase)) { continue; }
                catch (PcgamingWikiApiException ex) when (strategy is PcgamingWikiCargoPathStrategy &&
                    ex.Code.Equals("permissiondenied", StringComparison.OrdinalIgnoreCase))
                {
                    // Direct Cargo links opened in a browser do not carry GPM's bot session, and
                    // a bot password may lack the Cargo permission. The public article parser
                    // remains usable in either case, so keep bot mode functional with that source.
                    usedStrategy = new PcgamingWikiArticlePathStrategy();
                    rawPaths = await usedStrategy.FindWindowsPathsAsync(api, page, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException) when (strategy is PcgamingWikiCargoPathStrategy)
                {
                    usedStrategy = new PcgamingWikiArticlePathStrategy();
                    rawPaths = await usedStrategy.FindWindowsPathsAsync(api, page, cancellationToken).ConfigureAwait(false);
                }

                // GameData is a separate indexed table and may not yet contain a row even while
                // the public configuration section is present on the article. In bot mode use
                // the same wikitext/HTML parser as anonymous mode as a secondary source.
                if (rawPaths.Count == 0 && strategy is PcgamingWikiCargoPathStrategy)
                {
                    usedStrategy = new PcgamingWikiArticlePathStrategy();
                    rawPaths = await usedStrategy.FindWindowsPathsAsync(api, page, cancellationToken).ConfigureAwait(false);
                }

                successfullyParsedPage = page;
                successfullyParsedSource = usedStrategy.DefinitionId;
                var results = new List<GameConfigCandidate>();
                foreach (string rawPath in rawPaths)
                {
                    wikiWindowsPaths++;
                    observedWikiPaths.Add(rawPath);
                    foreach (string? pathRoot in pathResolutionRoots)
                    foreach (string path in PcgamingWikiPathResolver.Expand(rawPath, pathRoot))
                    {
                        localFilesFound++;
                        string description = usedStrategy.DefinitionId.Equals("PCGamingWiki", StringComparison.Ordinal)
                            ? "PCGamingWiki · статья"
                            : "PCGamingWiki · GameData";
                        AddCandidate(results, path, usedStrategy.DefinitionId, description);
                        if (results.Count >= MaxCandidates) return results;
                    }
                }

                // If a wiki path was parsed but its placeholder could not be resolved exactly,
                // match its explicit filename against the bounded local discovery results. Paths
                // documented relative to the game root remain restricted to that known root.
                if (results.Count == 0 && rawPaths.Count > 0)
                {
                    IReadOnlyList<GameConfigCandidate> localMatches = await FindLocalMatchesAsync(
                        game, rawPaths, gameRoots, cancellationToken).ConfigureAwait(false);
                    foreach (GameConfigCandidate candidate in localMatches)
                    {
                        localFilesFound++;
                        string description = usedStrategy.DefinitionId.Equals("PCGamingWiki", StringComparison.Ordinal)
                            ? "PCGamingWiki · статья · имя сопоставлено с локальным файлом"
                            : "PCGamingWiki · GameData · имя сопоставлено с локальным файлом";
                        AddCandidate(results, candidate.Path, usedStrategy.DefinitionId, description);
                        if (results.Count >= MaxCandidates) return results;
                    }
                }
                if (results.Count > 0) return results;
            }

            // An exact edition page is authoritative: never substitute another game or a
            // legacy edition just because its documented path does not exist on this computer.
            if ((exactPageFound || exactPage is not null) && queryHasEditionSuffix) break;
        }

        if (successfullyParsedPage is not null)
        {
            string source = successfullyParsedSource?.Equals("PCGamingWiki", StringComparison.Ordinal) == true
                ? "статья HTML/wikitext"
                : "Cargo GameData";
            if (wikiWindowsPaths == 0)
                throw new InvalidDataException($"PCGamingWiki ({source}): статья «{successfullyParsedPage}» найдена, пути Windows не распознаны.");
            if (localFilesFound == 0)
                throw new InvalidDataException($"PCGamingWiki ({source}): указано Windows-путей — {wikiWindowsPaths}; локальные файлы не найдены. Пути: {FormatPathDiagnostics(observedWikiPaths)}");
            throw new InvalidDataException($"PCGamingWiki ({source}) указал пути, но локальные файлы не прошли проверку безопасности.");
        }
        throw new InvalidDataException(anyPageFound
            ? $"PCGamingWiki нашёл похожие статьи для «{game.Name}», но не подтвердил точное совпадение названия. Проверьте название игры или найдите её страницу вручную."
            : $"PCGamingWiki не нашёл статью для «{game.Name}». Поиск выполнен также по очищенному от знаков ™/® названию и вариантам имени EXE.");
    }

    private static async Task<IReadOnlyList<GameConfigCandidate>> FindLocalMatchesAsync(GameEntry game,
        IReadOnlyList<string> wikiPaths, IReadOnlyList<string> gameRoots, CancellationToken cancellationToken)
    {
        IReadOnlyList<GameConfigCandidate> localCandidates = await new LocalGameConfigSearch()
            .FindCandidatesAsync(game, cancellationToken).ConfigureAwait(false);
        var matches = new List<GameConfigCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string wikiPath in wikiPaths)
        {
            string? expectedName = PcgamingWikiPathResolver.GetReferencedFileName(wikiPath);
            if (expectedName is null) continue;
            bool filePattern = expectedName.IndexOfAny(['*', '?']) >= 0;
            string? expectedDirectory = null;
            if (filePattern)
            {
                string? resolvedPattern = PcgamingWikiPathResolver.Resolve(
                    wikiPath, LocalGameConfigSearch.GetInstallDirectory(game));
                expectedDirectory = resolvedPattern is null ? null : Path.GetDirectoryName(resolvedPattern);
                if (string.IsNullOrWhiteSpace(expectedDirectory)) continue;
            }
            bool gameRootPath = PcgamingWikiPathResolver.RefersToGameInstall(wikiPath);
            foreach (GameConfigCandidate candidate in localCandidates)
            {
                if (!MatchesFileName(Path.GetFileName(candidate.Path), expectedName) ||
                    (filePattern && !GameConfigPathSafety.IsWithin(candidate.Path, expectedDirectory!)) ||
                    (gameRootPath && !gameRoots.Any(root => GameConfigPathSafety.IsWithin(candidate.Path, root))) ||
                    !seen.Add(candidate.Path)) continue;
                matches.Add(candidate);
            }
        }
        return matches;
    }

    private static bool MatchesFileName(string fileName, string expectedName)
    {
        if (expectedName.IndexOfAny(['*', '?']) < 0)
            return fileName.Equals(expectedName, StringComparison.OrdinalIgnoreCase);
        string pattern = "^" + Regex.Escape(expectedName).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string[] GetGamePathRoots(GameEntry game, string? installDirectory) =>
        new[] { installDirectory, Path.GetDirectoryName(game.ExePath) }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string FormatPathDiagnostics(IEnumerable<string> paths) =>
        string.Join(" · ", paths.Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Select(rawPath =>
        {
            string? fileName = PcgamingWikiPathResolver.GetReferencedFileName(rawPath);
            string normalized = PcgamingWikiArticleParser.NormalizePathValue(rawPath);
            bool directoryPath = normalized.EndsWith('\\') || normalized.EndsWith('/') || fileName is null;
            string raw = Regex.Replace(rawPath, @"\s+", " ").Trim();
            if (raw.Length > 100) raw = raw[..97] + "…";
            if (normalized.Length > 100) normalized = normalized[..97] + "…";
            string target = directoryPath ? "путь-каталог" : $"имя файла: {fileName}";
            return $"API: «{raw}» → «{normalized}»; {target}";
        }));

    private static void AddCandidate(List<GameConfigCandidate> output, string path, string definition, string description)
    {
        if (output.Count >= MaxCandidates || !File.Exists(path) || !GameConfigPathSafety.ValidateCandidate(path, false, out _)) return;
        string full = Path.GetFullPath(path);
        if (output.Any(item => item.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) return;
        string fileName = Path.GetFileName(full);
        bool recommended = fileName.Equals("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase);
        bool genericConfig = fileName.Equals("Config.ini", StringComparison.OrdinalIgnoreCase);
        int priority = recommended ? 0 : fileName.Equals("Engine.ini", StringComparison.OrdinalIgnoreCase) ? 10 : genericConfig ? 15 : 100;
        string guidance = fileName.Equals("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase)
            ? "Основной пользовательский файл Unreal Engine; начните с него для переключения экрана и игровых настроек. " + description
            : genericConfig ? "Файл Config.ini указан PCGamingWiki; проверьте, содержит ли он нужные параметры графики. " + description
            : description;
        output.Add(new GameConfigCandidate(full, definition, guidance, recommended, priority));
    }

    private static IEnumerable<string> GetOnlineQueries(GameEntry game, string? installDirectory)
    {
        string manifestName = LocalGameConfigSearch.ReadSteamManifestName(game, installDirectory);
        string installName = Path.GetFileName(installDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "");
        string productName = LocalGameConfigSearch.ReadExecutableMetadata(game.ExePath, productName: true);
        string[] titles = new[] { game.Name, game.EpicAppName, manifestName, installName, productName,
                Path.GetFileNameWithoutExtension(game.ExePath) }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return titles.SelectMany(ExpandTitleVariants)
            .SelectMany(value => new[] { value, LocalGameConfigSearch.StripGameEditionSuffix(value) })
            .Where(value => value.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => !LocalGameConfigSearch.StripGameEditionSuffix(value)
                .Equals(value, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(value => Normalize(value).Length);
    }

    private static IEnumerable<string> ExpandTitleVariants(string value)
    {
        yield return value.Trim();
        string withoutMarks = Regex.Replace(value, @"[\u2122\u00AE\u00A9\u2120]", "");
        if (!withoutMarks.Equals(value, StringComparison.Ordinal)) yield return withoutMarks.Trim();

        // Store manifests and executable names often concatenate words (e.g. ShadowOfMordor).
        // Add a readable search form so MediaWiki does not have to guess those boundaries.
        string splitCamelCase = Regex.Replace(withoutMarks, @"(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})", " ");
        splitCamelCase = Regex.Replace(splitCamelCase, @"(?<=\p{Lu})(?=\p{Lu}\p{Ll})", " ");
        splitCamelCase = Regex.Replace(splitCamelCase, @"\s+", " ").Trim();
        if (splitCamelCase.Length > 0 && !splitCamelCase.Equals(withoutMarks, StringComparison.Ordinal))
            yield return splitCamelCase;

        string punctuationFree = Regex.Replace(withoutMarks, @"[^\p{L}\p{N}]+", " ").Trim();
        if (punctuationFree.Length > 0 && !punctuationFree.Equals(withoutMarks, StringComparison.Ordinal))
            yield return punctuationFree;
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
}
