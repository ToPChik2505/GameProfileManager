using GameProfileManager.Core;

namespace GameProfileManager.Application;

public sealed record EffectiveDisplayResolution(DisplayInfo? Display, bool IsResolved, string Reason);

/// <summary>Resolves the selected physical display independently of performance-profile settings.</summary>
public static class EffectiveDisplayResolver
{
    public static EffectiveDisplayResolution Resolve(AppState state, GameEntry game,
        IReadOnlyList<DisplayInfo> displays)
    {
        DisplayInfo? selected = game.DisplayProfilePolicy switch
        {
            DisplayProfilePolicy.PreferredDisplay => FindPreferred(state, game, displays),
            DisplayProfilePolicy.WindowsPrimary => FindUniquePrimary(displays),
            DisplayProfilePolicy.PinnedDisplay => FindUniqueById(game.DisplayId, displays),
            _ => null
        };
        if (selected is not null) return new(selected, true, $"Используется дисплей «{selected.Name}»");
        return new(null, false, game.DisplayProfilePolicy switch
        {
            DisplayProfilePolicy.PinnedDisplay => "Закреплённый дисплей отключён или не выбран",
            DisplayProfilePolicy.WindowsPrimary => "Основной дисплей Windows недоступен или неоднозначен",
            _ => "Предпочтительный дисплей недоступен или неоднозначен"
        });
    }

    /// <summary>Legacy display choice for games without automatic per-display profiles.</summary>
    public static DisplayInfo? ResolveLegacy(AppState state, GameEntry game, IReadOnlyList<DisplayInfo> displays) =>
        DisplayFps.ChooseDisplay(state, game, displays);

    private static DisplayInfo? FindPreferred(AppState state, GameEntry game, IReadOnlyList<DisplayInfo> displays)
    {
        string id = !string.IsNullOrWhiteSpace(game.DisplayId) ? game.DisplayId : state.DefaultDisplayId;
        if (!string.IsNullOrWhiteSpace(id)) return FindUniqueById(id, displays);
        return FindUniquePrimary(displays) ?? (displays.Count == 1 ? displays[0] : null);
    }

    private static DisplayInfo? FindUniquePrimary(IReadOnlyList<DisplayInfo> displays) =>
        displays.Where(display => display.IsPrimary).Take(2).ToArray() is [var only] ? only : null;

    private static DisplayInfo? FindUniqueById(string? id, IReadOnlyList<DisplayInfo> displays)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var matches = displays.Where(display => display.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
