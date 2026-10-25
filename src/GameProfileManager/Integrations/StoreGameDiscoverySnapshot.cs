using GameProfileManager.Core;

namespace GameProfileManager.Integrations;

/// <summary>Снимок игр одного магазина с признаком полного чтения его каталога.</summary>
public sealed record StoreGameDiscoverySnapshot(
    IReadOnlyList<GameEntry> Games,
    IReadOnlySet<string> PresentGameIds,
    bool IsComplete);
