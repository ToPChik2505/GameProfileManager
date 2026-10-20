using System.ComponentModel;
using GameProfileManager.Core;
using GameProfileManager.Presentation;

namespace GameProfileManager;

/// <summary>Раздел обязанностей основной модели представления.</summary>
public sealed partial class MainViewModel
{
    private void Hook(PerformanceProfile profile)
    {
        if (_profileHandlers.ContainsKey(profile)) return;
        _effectiveRtssModes[profile.Id] = RtssLimiterModeResolver.Resolve(_state, profile);
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(PerformanceProfile.RtssLimiterMode))
            {
                var effective = RtssLimiterModeResolver.Resolve(_state, profile);
                bool changed = !_effectiveRtssModes.TryGetValue(profile.Id, out var previous) || previous != effective;
                _effectiveRtssModes[profile.Id] = effective;
                if (profile == SelectedProfile)
                {
                    Changed(nameof(SelectedProfileRtssLimiterChoice));
                    Changed(nameof(SelectedProfileEffectiveRtssModeLabel));
                }
                if (changed)
                    foreach (var game in Games.Where(g => g.ProfileId == profile.Id))
                    { game.Status = ApplyStatus.Pending; game.Detail = "Режим лимитера RTSS изменён; примените профиль"; }
                RefreshDisplays();
                ScheduleSave();
                return;
            }
            if (e.PropertyName is nameof(PerformanceProfile.Name) or nameof(PerformanceProfile.FpsLimit) or
                nameof(PerformanceProfile.FpsMode) or nameof(PerformanceProfile.FpsDivisor) or
                nameof(PerformanceProfile.Vrr) or nameof(PerformanceProfile.VSync) or
                nameof(PerformanceProfile.Description))
            {
                if (e.PropertyName == nameof(PerformanceProfile.Name)) ProfileOptions.First(option => option.Id == profile.Id).RefreshName();
                if (e.PropertyName != nameof(PerformanceProfile.Description))
                {
                    if (profile == SelectedProfile) UpdateFpsFractions();
                    foreach (var game in Games.Where(g => g.ProfileId == profile.Id)) { game.Status = ApplyStatus.Pending; game.Detail = "Профиль изменён"; }
                    RefreshDisplays();
                }
                ScheduleSave();
            }
        };
        _profileHandlers.Add(profile, handler);
        profile.PropertyChanged += handler;
    }
    private void Hook(GameEntry game)
    {
        if (_gameHandlers.ContainsKey(game)) return;
        var card = new GameEntryViewModel(game, _iconLoader);
        _gameCards.Add(game, card);
        GameCards.Add(card);
        PropertyChangedEventHandler handler = (_, e) =>
        {
        if (e.PropertyName == nameof(GameEntry.Name)) { UpdateSummary(); ScheduleSave(); }
        if (e.PropertyName is nameof(GameEntry.ProfileId) or nameof(GameEntry.ExePath) or nameof(GameEntry.DisplayId))
        {
            game.Status = ApplyStatus.Pending;
            game.Detail = game.ProfileId is null
                ? _state.Backups.Any(b => b.ManagedGameId == game.Id)
                    ? "Профиль отключён. Нажмите «Применить», чтобы восстановить исходные параметры."
                    : "Профиль не выбран: параметры не применяются"
                : "Назначение изменено";
            RefreshDisplays();
            if (e.PropertyName is nameof(GameEntry.ProfileId) or nameof(GameEntry.DisplayId)) SaveOnly(false);
            else ScheduleSave();
        }
        };
        _gameHandlers.Add(game, handler);
        game.PropertyChanged += handler;
    }
    private void Unhook(PerformanceProfile profile)
    {
        if (_profileHandlers.Remove(profile, out var handler)) profile.PropertyChanged -= handler;
        _effectiveRtssModes.Remove(profile.Id);
    }
    private void Unhook(GameEntry game)
    {
        if (_gameHandlers.Remove(game, out var handler)) game.PropertyChanged -= handler;
        if (_gameCards.Remove(game, out var card))
        {
            card.Dispose();
            GameCards.Remove(card);
        }
    }
    private void RefreshDisplays()
    {
        var displays = _displays.ToArray();
        var profilesById = Profiles.ToDictionary(profile => profile.Id);
        foreach (var profile in Profiles)
        {
            var fps = DisplayFps.Resolve(_state, null, profile, displays);
            string preview = profile.FpsMode == FpsLimitMode.Manual ? $"{profile.FpsLimit} FPS" :
                fps.Fps is int value ? $"1/{profile.FpsDivisor} · {value} FPS" : $"1/{profile.FpsDivisor} · недоступно";
            if (profile.FpsPreview != preview)
            {
                profile.FpsPreview = preview;
                profile.Changed(nameof(PerformanceProfile.FpsPreview));
            }
        }
        foreach (var game in Games)
        {
            PerformanceProfile? profile = game.ProfileId is { } id &&
                profilesById.TryGetValue(id, out var found) ? found : null;
            UpdateGameDisplay(game, nameof(GameEntry.ProfileDisplay), game.ProfileDisplay,
                profile?.Name ?? "Без профиля", value => game.ProfileDisplay = value);
            UpdateGameDisplay(game, nameof(GameEntry.FpsDisplay), game.FpsDisplay,
                profile is null ? "—" : DisplayFps.Resolve(_state, game, profile, displays).DisplayText,
                value => game.FpsDisplay = value);
            UpdateGameDisplay(game, nameof(GameEntry.VrrDisplay), game.VrrDisplay,
                profile is null ? "—" : ProfileLabels.Vrr(profile.Vrr), value => game.VrrDisplay = value);
            UpdateGameDisplay(game, nameof(GameEntry.VSyncDisplay), game.VSyncDisplay,
                profile is null ? "—" : ProfileLabels.VSync(profile.VSync), value => game.VSyncDisplay = value);
            UpdateGameDisplay(game, nameof(GameEntry.RtssLimiterDisplay), game.RtssLimiterDisplay,
                profile is null ? "—" : ProfileLabels.Rtss(RtssLimiterModeResolver.Resolve(_state, profile)) +
                (profile.RtssLimiterMode is null ? " · общий режим" : " · режим профиля"),
                value => game.RtssLimiterDisplay = value);
            // Загрузка Icon выполняется привязкой только для отображаемых WPF-элементов.
        }
        if (StatusFilter != "Все статусы") GamesView.Refresh();
        Changed(nameof(SelectedProfileFpsDetail));
        UpdateSummary();
    }
    /// <summary>Не отправляет лишние уведомления привязкам, если текстовое значение не изменилось.</summary>
    private static void UpdateGameDisplay(GameEntry game, string name, string current, string next, Action<string> assign)
    {
        if (current == next) return;
        assign(next);
        game.Changed(name);
    }

}
