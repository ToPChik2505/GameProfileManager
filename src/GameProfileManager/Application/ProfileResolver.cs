using GameProfileManager.Core;

namespace GameProfileManager.Application;

public sealed record ProfileResolution(PerformanceProfile? Profile, DisplayInfo? Display,
    bool IsResolved, string Reason)
{
    public static ProfileResolution Unresolved(string reason) => new(null, null, false, reason);
}
public sealed record EffectiveProfileConfiguration(int? Fps, RtssLimiterMode LimiterMode, VrrMode Vrr, VSyncMode VSync);

/// <summary>Resolves the effective game profile without changing state or integrations.</summary>
public static class ProfileResolver
{
    public static EffectiveProfileConfiguration? ResolveConfiguration(AppState state, GameEntry game,
        IReadOnlyList<DisplayInfo> displays)
    {
        var resolution = Resolve(state, game, displays);
        if (!resolution.IsResolved || resolution.Profile is not { } profile) return null;
        FpsResolution fps;
        if (game.AutoSwitchProfile && profile.FpsMode == FpsLimitMode.DisplayFraction)
        {
            if (resolution.Display is null) return null;
            var selectedGame = new GameEntry { DisplayId = resolution.Display.Id };
            var selectedState = new AppState { DefaultDisplayId = resolution.Display.Id };
            fps = DisplayFps.Resolve(selectedState, selectedGame, profile, [resolution.Display]);
        }
        else fps = DisplayFps.Resolve(state, game, profile, displays);
        return fps.Fps is int target
            ? new(target, RtssLimiterModeResolver.Resolve(state, profile), profile.Vrr, profile.VSync)
            : null;
    }

    public static ProfileResolution Resolve(AppState state, GameEntry game, IReadOnlyList<DisplayInfo> displays)
    {
        if (!game.AutoSwitchProfile)
        {
            var legacy = FindProfile(state, game.ProfileId);
            return legacy is null
                ? ProfileResolution.Unresolved("Профиль по умолчанию не выбран или удалён")
                : new(legacy, DisplayFps.ChooseDisplay(state, game, displays), true, "Используется профиль игры по умолчанию");
        }

        if (game.DisplayProfileAssignments.GroupBy(a => a.DisplayId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            return ProfileResolution.Unresolved("В назначениях обнаружен повторяющийся ID дисплея");

        DisplayInfo? selected = game.DisplayProfilePolicy switch
        {
            DisplayProfilePolicy.PreferredDisplay => FindUniquePreferred(state, game, displays),
            DisplayProfilePolicy.WindowsPrimary => FindUniquePrimary(displays),
            DisplayProfilePolicy.PinnedDisplay => FindPinned(game, displays),
            _ => null
        };
        if (selected is null)
            return ProfileResolution.Unresolved(game.DisplayProfilePolicy switch
            {
                DisplayProfilePolicy.PinnedDisplay => "Закреплённый дисплей недоступен или не выбран",
                DisplayProfilePolicy.WindowsPrimary => "Основной дисплей Windows недоступен или неоднозначен",
                _ => "Предпочтительный дисплей недоступен или неоднозначен"
            });

        var assignment = game.DisplayProfileAssignments.SingleOrDefault(a =>
            a.DisplayId.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
        if (assignment is null)
        {
            var fallback = FindProfile(state, game.ProfileId);
            return fallback is null
                ? ProfileResolution.Unresolved($"Для дисплея «{selected.Name}» нет назначения и профиль по умолчанию не задан")
                : new(fallback, selected, true, $"Для «{selected.Name}» используется профиль по умолчанию");
        }
        var profile = FindProfile(state, assignment.ProfileId);
        return profile is null
            ? ProfileResolution.Unresolved($"Назначение дисплея «{selected.Name}» ссылается на отсутствующий профиль")
            : new(profile, selected, true, $"Профиль «{profile.Name}» выбран для «{selected.Name}»");
    }

    private static PerformanceProfile? FindProfile(AppState state, Guid? id) =>
        id is { } value ? state.Profiles.FirstOrDefault(profile => profile.Id == value) : null;

    private static DisplayInfo? FindUniquePrimary(IReadOnlyList<DisplayInfo> displays) =>
        displays.Where(display => display.IsPrimary).Take(2).ToArray() is [var only] ? only : null;

    private static DisplayInfo? FindPinned(GameEntry game, IReadOnlyList<DisplayInfo> displays) =>
        FindUniqueById(game.DisplayId, displays);

    private static DisplayInfo? FindUniquePreferred(AppState state, GameEntry game, IReadOnlyList<DisplayInfo> displays)
    {
        string id = !string.IsNullOrWhiteSpace(game.DisplayId) ? game.DisplayId : state.DefaultDisplayId;
        if (!string.IsNullOrWhiteSpace(id)) return FindUniqueById(id, displays);
        var primary = FindUniquePrimary(displays);
        return primary ?? (displays.Count == 1 ? displays[0] : null);
    }

    private static DisplayInfo? FindUniqueById(string id, IReadOnlyList<DisplayInfo> displays)
    {
        var matches = displays.Where(display => display.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
