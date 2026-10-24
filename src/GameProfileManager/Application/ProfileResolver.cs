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
                : new(legacy, EffectiveDisplayResolver.ResolveLegacy(state, game, displays), true, "Используется профиль игры по умолчанию");
        }

        if (game.DisplayProfileAssignments.GroupBy(a => a.DisplayId, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            return ProfileResolution.Unresolved("В назначениях обнаружен повторяющийся ID дисплея");

        var displayResolution = EffectiveDisplayResolver.Resolve(state, game, displays);
        var selected = displayResolution.Display;
        if (!displayResolution.IsResolved || selected is null) return ProfileResolution.Unresolved(displayResolution.Reason);

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

}
