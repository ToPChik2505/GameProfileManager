using GameProfileManager.Core;
using GameProfileManager.Application;

namespace GameProfileManager;

/// <summary>Раздел обязанностей основной модели представления.</summary>
public sealed partial class MainViewModel
{
    private async Task ApplyAllAsync(bool checkSavedFirst = false)
    {
        if (_busy) return;
        _busy = true;
        IsApplyingAll = true;
        SyncProgressPercent = 0;
        SyncProgressMessage = checkSavedFirst ? "Проверяем сохранённые параметры игр…" : "Подготавливаем применение…";
        Notice = checkSavedFirst ? "Проверяем настройки при запуске." : "Применяем профили. Окно остаётся доступным.";
        try
        {
            SaveOnly(false, duringSync: true);
            var snapshot = AppStateSnapshot.Capture(_state);
            if (checkSavedFirst)
            {
                var checkError = await _syncCoordinator.ExecuteAsync(snapshot, (service, state) => service.CheckSaved(state));
                AppStateSnapshot.PublishResults(snapshot, _state);
                if (checkError is not null) throw checkError;
                RefreshDisplays();
                bool needsApply = Games.Any(game => game.Status != ApplyStatus.Applied && game.ProfileId is not null &&
                    !string.IsNullOrWhiteSpace(game.ExePath)) ||
                    Games.Any(game => game.ProfileId is null && _state.Backups.Any(backup => backup.ManagedGameId == game.Id));
                if (!needsApply)
                {
                    Notice = "Проверка завершена: сохранённые параметры совпадают.";
                    return;
                }
            }

            var games = Games.ToArray();
            if (games.Length == 0) { Notice = "В библиотеке пока нет игр."; return; }
            for (int index = 0; index < games.Length; index++)
            {
                var game = games[index];
                SyncProgressMessage = $"Игра {index + 1} из {games.Length}: {game.Name}";
                var error = await _syncCoordinator.ExecuteAsync(snapshot, (service, state) =>
                    service.ApplyOne(state, state.Games.Single(item => item.Id == game.Id)));
                AppStateSnapshot.PublishResults(snapshot, _state);
                if (error is not null) throw error;
                SyncProgressPercent = 100d * (index + 1) / games.Length;
                UpdateSummary();
            }
            int issues = games.Count(game => game.Status is ApplyStatus.Error or ApplyStatus.Unsupported);
            Notice = issues == 0
                ? $"Применение завершено. Обработано игр: {games.Length}. Проверьте статусы."
                : $"Применение завершено. Обработано игр: {games.Length}; с проблемами: {issues}. Проверьте статусы в разделе «Требуют внимания».";
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            Notice = "Применение прервано: " + ex.Message;
        }
        finally
        {
            _busy = false;
            IsApplyingAll = false;
            SaveOnly(false);
            RefreshDisplays();
            UpdateSummary();
        }
    }

}
