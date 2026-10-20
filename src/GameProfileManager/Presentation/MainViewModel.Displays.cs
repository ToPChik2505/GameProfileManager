using GameProfileManager.Core;
using GameProfileManager.Integrations;

namespace GameProfileManager;

/// <summary>Раздел обязанностей основной модели представления.</summary>
public sealed partial class MainViewModel
{
    private void LoadDisplays()
    {
        try
        {
            var found = WindowsDisplayDiscovery.Read();
            _displays.Clear();
            foreach (var display in found) _displays.Add(display);
            _displayFingerprint = Fingerprint(found);
            UpdateDisplayOptions();
            DisplayStatus = found.Count == 0 ? "Windows не обнаружила активные экраны" :
                found.Any(d => d.DynamicRefresh) ? "Обнаружена динамическая частота: автоматический лимит для неё недоступен" :
                "Параметры экранов обновляются автоматически";
        }
        catch (Exception ex) { _store.Log(ex); DisplayStatus = "Не удалось прочитать экраны: " + ex.Message; }
        UpdateDisplaySummary();
    }

    private void PollDisplays()
    {
        if (_busy) return;
        try
        {
            var found = WindowsDisplayDiscovery.Read();
            string fingerprint = Fingerprint(found);
            if (fingerprint == _displayFingerprint) { _candidateFingerprint = ""; _candidateObservations = 0; return; }
            if (fingerprint != _candidateFingerprint) { _candidateFingerprint = fingerprint; _candidateObservations = 1; return; }
            if (++_candidateObservations < 2) return;
            _candidateFingerprint = ""; _candidateObservations = 0;
            var previous = _displays.ToArray();
            var changed = Games.Where(game =>
            {
                var profile = Profiles.FirstOrDefault(p => p.Id == game.ProfileId);
                return profile?.FpsMode == FpsLimitMode.DisplayFraction &&
                    DisplayFps.Resolve(_state, game, profile, previous).Fps !=
                    DisplayFps.Resolve(_state, game, profile, found).Fps;
            }).ToArray();
            _displays.Clear();
            foreach (var display in found) _displays.Add(display);
            _displayFingerprint = fingerprint;
            UpdateDisplayOptions(); UpdateDisplaySummary(); UpdateFpsFractions(); RefreshDisplays();
            DisplayStatus = found.Count == 0 ? "Windows не обнаружила активные экраны" :
                found.Any(d => d.DynamicRefresh) ? "Динамическая частота: автоматический лимит недоступен" :
                "Параметры экранов обновляются автоматически";
            if (changed.Length == 0) return;
            Run(() =>
            {
                foreach (var game in changed)
                {
                    _sync.ApplyOne(_state, game);
                }
                RefreshDisplays();
                Notice = changed.All(game => game.Status is ApplyStatus.Applied or ApplyStatus.RestartRequired)
                    ? "Частота экрана изменилась; новые лимиты FPS применены."
                    : "Частота экрана изменилась; проверьте статусы игр.";
            });
        }
        catch (Exception ex) { _store.Log(ex); DisplayStatus = "Ошибка проверки экрана: " + ex.Message; }
    }

    private static string Fingerprint(IEnumerable<DisplayInfo> displays) => string.Join("|",
        displays.OrderBy(display => display.Id, StringComparer.OrdinalIgnoreCase).Select(display =>
            $"{display.Id}:{display.Width}:{display.Height}:{display.RefreshNumerator}/{display.RefreshDenominator}:{display.IsPrimary}:{display.DynamicRefresh}"));

    private void UpdateDisplayOptions()
    {
        string? selected = SelectedDisplay?.Id;
        IsUpdatingDisplayOptions = true;
        try
        {
            DefaultDisplayOptions.Clear(); GameDisplayOptions.Clear();
            DefaultDisplayOptions.Add(new("", "Основной экран Windows"));
            GameDisplayOptions.Add(new("", "Экран по умолчанию"));
            foreach (var display in _displays)
            {
                string label = $"{display.Name} · {display.RefreshLabel}" + (display.IsPrimary ? " · основной" : "");
                DefaultDisplayOptions.Add(new(display.Id, label));
                GameDisplayOptions.Add(new(display.Id, label));
            }
            var available = _displays.Select(display => display.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_state.DefaultDisplayId) && !available.Contains(_state.DefaultDisplayId))
                DefaultDisplayOptions.Add(new(_state.DefaultDisplayId, "Выбранный экран отключён"));
            foreach (string id in Games.Select(game => game.DisplayId).Where(id => !string.IsNullOrWhiteSpace(id) && !available.Contains(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                GameDisplayOptions.Add(new(id, "Выбранный экран отключён"));
        }
        finally { IsUpdatingDisplayOptions = false; }
        SelectedDisplay = _displays.FirstOrDefault(display => display.Id == selected) ?? OverviewDisplay;
        Changed(nameof(DefaultDisplayId));
    }

    private void UpdateDisplaySummary()
    {
        Changed(nameof(DefaultDisplay)); Changed(nameof(OverviewDisplay)); Changed(nameof(OverviewDisplayHeading));
        Changed(nameof(OverviewDisplayTitle)); Changed(nameof(OverviewDisplayRate)); Changed(nameof(OverviewDisplayDescription));
        Changed(nameof(SelectedProfileFpsDetail));
    }

    private void UpdateFpsFractions()
    {
        IsUpdatingFpsOptions = true;
        try
        {
            var available = new List<ModeOption<int>>();
            var display = DefaultDisplay;
            if (display is { DynamicRefresh: false })
            {
                for (int divisor = 1; divisor <= 12; divisor++)
                {
                    if (DisplayFps.ExactLimit(display, divisor) is int fps)
                        available.Add(new(divisor, $"1/{divisor} — {fps} FPS"));
                }
            }
            var values = available.Select(option => option.Value).ToHashSet();
            for (int i = FpsFractions.Count - 1; i >= 0; i--)
                if (!values.Contains(FpsFractions[i].Value)) FpsFractions.RemoveAt(i);
            for (int i = 0; i < available.Count; i++)
            {
                var next = available[i];
                if (i < FpsFractions.Count && FpsFractions[i].Value == next.Value)
                    FpsFractions[i].Label = next.Label;
                else
                    FpsFractions.Insert(i, next);
            }
        }
        finally { IsUpdatingFpsOptions = false; }
        Changed(nameof(SelectedProfileFpsDetail));
    }

}
