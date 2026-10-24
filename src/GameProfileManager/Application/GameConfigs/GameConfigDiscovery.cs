using GameProfileManager.Core;
using GameProfileManager.Infrastructure;

namespace GameProfileManager.Application;

public sealed record GameConfigCandidate(string Path, string DefinitionId, string Description,
    bool IsRecommended = false, int Priority = 100,
    GameConfigSourceKind SourceKind = GameConfigSourceKind.File, string RegistryValueName = "", bool AllowBinary = false);

public interface IGameConfigDiscovery
{
    Task<IReadOnlyList<GameConfigCandidate>> FindCandidatesAsync(GameEntry game, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameConfigCandidate>> FindOnlineCandidatesAsync(GameEntry game, CancellationToken cancellationToken = default);
    Task TestOnlineAccessAsync(CancellationToken cancellationToken = default);
}

/// <summary>Coordinates local discovery and the separately selectable PCGamingWiki search.</summary>
public sealed class GameConfigDiscovery : IGameConfigDiscovery
{
    private readonly LocalGameConfigSearch _localSearch = new();
    private readonly UnityPlayerPrefsRegistryDiscovery _unityPlayerPrefs = new();
    private readonly IPcgamingWikiCredentialStore? _credentialStore;

    public GameConfigDiscovery(IPcgamingWikiCredentialStore? credentialStore = null) => _credentialStore = credentialStore;

    public async Task<IReadOnlyList<GameConfigCandidate>> FindCandidatesAsync(GameEntry game,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GameConfigCandidate> files = await _localSearch.FindCandidatesAsync(game, cancellationToken)
            .ConfigureAwait(false);
        return Merge(files, FindUnityPlayerPrefsCandidates(game));
    }

    public async Task<IReadOnlyList<GameConfigCandidate>> FindOnlineCandidatesAsync(GameEntry game,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GameConfigCandidate> registryCandidates = FindUnityPlayerPrefsCandidates(game);
        try
        {
            IReadOnlyList<GameConfigCandidate> online = await new PcgamingWikiConfigSearch(_credentialStore)
                .FindCandidatesAsync(game, cancellationToken).ConfigureAwait(false);
            return online.Count > 0 ? online : registryCandidates;
        }
        catch (InvalidDataException) when (registryCandidates.Count > 0)
        {
            // Some PCGamingWiki pages document a directory (or a file not yet created) while
            // Unity stores the actual display preferences in PlayerPrefs. Preserve the online
            // search action and offer the locally verified registry candidates in that case.
            return registryCandidates;
        }
    }

    private IReadOnlyList<GameConfigCandidate> FindUnityPlayerPrefsCandidates(GameEntry game) =>
        _unityPlayerPrefs.FindCandidates(game).Select(candidate => new GameConfigCandidate(
            candidate.KeyPath, "unity-playerprefs", candidate.Description, IsRecommended: true, Priority: 5,
            SourceKind: GameConfigSourceKind.Registry, RegistryValueName: candidate.ValueName)).ToArray();

    private static IReadOnlyList<GameConfigCandidate> Merge(IReadOnlyList<GameConfigCandidate> files,
        IReadOnlyList<GameConfigCandidate> registry)
    {
        var result = new List<GameConfigCandidate>(files.Count + registry.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameConfigCandidate candidate in files.Concat(registry)
                     .OrderBy(candidate => candidate.Priority).ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase))
        {
            string identity = candidate.SourceKind == GameConfigSourceKind.Registry
                ? candidate.Path + "|" + candidate.RegistryValueName
                : candidate.Path;
            if (!seen.Add(identity)) continue;
            result.Add(candidate);
            if (result.Count >= 32) break;
        }
        return result;
    }

    public Task TestOnlineAccessAsync(CancellationToken cancellationToken = default) =>
        new PcgamingWikiConfigSearch(_credentialStore).TestAccessAsync(cancellationToken);
}
