using GameProfileManager.Core;
using GameProfileManager.Integrations;
using GameProfileManager.Application;

namespace GameProfileManager;

/// <summary>Раздел обязанностей основной модели представления.</summary>
public sealed partial class MainViewModel
{
    public async Task RefreshMonitorListAsync()
    {
        try
        {
            var found = await Task.Run(WindowsDisplayDiscovery.Read);
            if (_disposed) return;
            _displays.Clear();
            foreach (var display in found) _displays.Add(display);
            Volatile.Write(ref _displaySnapshot, found.ToArray());
            CacheDisplayNames(found);
            _displayMonitor.Reset(found);
            UpdateDisplayOptions();
            UpdateDisplaySummary();
            UpdateFpsFractions();
            RefreshDisplays();
            DisplayStatus = found.Count == 0 ? "Windows не обнаружила активные экраны" :
                "Список экранов обновлён";
        }
        catch (Exception ex) { _store.Log(ex); DisplayStatus = "Не удалось прочитать экраны: " + ex.Message; }
    }

    private void LoadDisplays()
    {
        try
        {
            var found = WindowsDisplayDiscovery.Read();
            _displays.Clear();
            foreach (var display in found) _displays.Add(display);
            Volatile.Write(ref _displaySnapshot, found.ToArray());
            CacheDisplayNames(found);
            _displayMonitor.Reset(found);
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
        if (_busy || _displayPollInFlight || _disposed) return;
        _displayPollInFlight = true;
        _ = PollDisplaysAsync();
    }

    private async Task PollDisplaysAsync()
    {
        try
        {
            var found = await Task.Run(WindowsDisplayDiscovery.Read);
            if (_busy || _disposed || !_displayMonitor.Observe(found)) return;
            var previous = _displays.ToArray();
            var changed = Games.Where(game =>
                ProfileResolver.ResolveConfiguration(_state, game, previous) !=
                ProfileResolver.ResolveConfiguration(_state, game, found)).ToArray();
            _displays.Clear();
            foreach (var display in found) _displays.Add(display);
            Volatile.Write(ref _displaySnapshot, found.ToArray());
            CacheDisplayNames(found);
            UpdateDisplayOptions(); UpdateDisplaySummary(); UpdateFpsFractions(); RefreshDisplays();
            DisplayStatus = found.Count == 0 ? "Windows не обнаружила активные экраны" :
                found.Any(d => d.DynamicRefresh) ? "Динамическая частота: автоматический лимит недоступен" :
                "Параметры экранов обновляются автоматически";
            if (changed.Length == 0) return;
            // Запись RTSS/NVIDIA может длиться долго. Не выполняем её в обработчике DispatcherTimer.
            _ = ApplyDisplayChangesAsync(changed);
        }
        catch (Exception ex) { _store.Log(ex); DisplayStatus = "Ошибка проверки экрана: " + ex.Message; }
        finally { _displayPollInFlight = false; }
    }

    /// <summary>
    /// Последовательно применяет изменения FPS, не задерживая прорисовку окна.
    /// Пока выполняется внешняя запись, редактирование профилей блокируется как при «Применить ко всем».
    /// </summary>
    private async Task ApplyDisplayChangesAsync(GameEntry[] changed)
    {
        if (!BeginActivity("Обновление лимитов после смены экрана…")) return;
        try
        {
            SaveOnly(false, duringSync: true);
            var snapshot = AppStateSnapshot.Capture(_state);
            for (int index = 0; index < changed.Length; index++)
            {
                var game = changed[index];
                SyncProgressMessage = $"Обновление лимита после смены экрана: {game.Name}";
                var error = await _syncCoordinator.ExecuteAsync(snapshot, (service, state) =>
                    service.ApplyOne(state, state.Games.Single(item => item.Id == game.Id)));
                AppStateSnapshot.PublishResults(snapshot, _state);
                if (error is not null) throw error;
                SyncProgressPercent = 100d * (index + 1) / changed.Length;
            }
            Notice = changed.All(game => game.Status is ApplyStatus.Applied or ApplyStatus.RestartRequired)
                ? "Дисплей изменился; обновлённые профили применены."
                : "Дисплей изменился; проверьте статусы игр.";
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            Notice = "Ошибка автоматической подстройки FPS: " + ex.Message;
        }
        finally
        {
            EndActivity();
            SaveOnly(false);
            RefreshDisplays();
            UpdateSummary();
        }
    }

    private void UpdateDisplayOptions()
    {
        string? selected = SelectedDisplay?.Id;
        IsUpdatingDisplayOptions = true;
        try
        {
            DefaultDisplayOptions.Clear(); GameDisplayOptions.Clear(); ProfileAssignmentDisplayOptions.Clear();
            DefaultDisplayOptions.Add(new("", "Основной экран Windows"));
            GameDisplayOptions.Add(new("", "Экран по умолчанию"));
            ProfileAssignmentDisplayOptions.Add(new("", "Сначала выберите монитор"));
            foreach (var display in _displays)
            {
                string label = $"{display.Name} · {display.RefreshLabel}" + (display.IsPrimary ? " · основной" : "");
                DefaultDisplayOptions.Add(new(display.Id, label));
                GameDisplayOptions.Add(new(display.Id, label));
                ProfileAssignmentDisplayOptions.Add(new(display.Id, label));
            }
            var available = _displays.Select(display => display.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_state.DefaultDisplayId) && !available.Contains(_state.DefaultDisplayId))
                DefaultDisplayOptions.Add(new(_state.DefaultDisplayId, $"{DisplayNameCache.Get(_state, _state.DefaultDisplayId)} — отключён"));
            foreach (string id in Games.Select(game => game.DisplayId).Where(id => !string.IsNullOrWhiteSpace(id) && !available.Contains(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                GameDisplayOptions.Add(new(id, $"{DisplayNameCache.Get(_state, id)} — отключён"));
            foreach (string id in Games.Select(game => game.DisplayProfileTargetId)
                         .Where(id => !string.IsNullOrWhiteSpace(id) && !available.Contains(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                ProfileAssignmentDisplayOptions.Add(new(id, $"{DisplayNameCache.Get(_state, id)} — отключён"));
            foreach (var card in GameCards)
                card.UpdateDisplayAssignmentRows(_state, _displays.ToArray());
        }
        finally { IsUpdatingDisplayOptions = false; }
        SelectedDisplay = _displays.FirstOrDefault(display => display.Id == selected) ?? OverviewDisplay;
        Changed(nameof(DefaultDisplayId));
    }

    private void CacheDisplayNames(IReadOnlyList<DisplayInfo> displays)
    {
        bool changed = false;
        foreach (var display in displays)
        {
            if (string.IsNullOrWhiteSpace(display.Id) || string.IsNullOrWhiteSpace(display.Name)) continue;
            var existingKey = _state.DisplayNames.Keys.FirstOrDefault(key =>
                key.Equals(display.Id, StringComparison.OrdinalIgnoreCase));
            if (existingKey is not null && _state.DisplayNames[existingKey] == display.Name) continue;
            if (existingKey is not null) _state.DisplayNames.Remove(existingKey);
            _state.DisplayNames[display.Id] = display.Name;
            changed = true;
        }
        if (changed) SaveOnly(false);
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
