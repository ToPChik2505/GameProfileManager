using GameProfileManager.Core;
using GameProfileManager.Integrations;
using GameProfileManager.Application;

namespace GameProfileManager;

/// <summary>Раздел обязанностей основной модели представления.</summary>
public sealed partial class MainViewModel
{
    private async Task AddGameAsync()
    {
        string? path = await SelectLocalFileAsync("Выберите игровой .exe", "*.exe", null);
        if (path is null) return;
        if (Games.Any(g => g.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase))) { Notice = "Этот .exe уже добавлен."; return; }
        var game = new GameEntry { Name = Path.GetFileNameWithoutExtension(path), ExePath = path, Detail = "Выберите профиль" };
        Games.Add(game); Hook(game); SelectedGame = game; RefreshDisplays(); SaveOnly(); UpdateSummary();
    }

    private void RemoveGameFromLibrary(GameEntry? game)
    {
        if (game is null || !Games.Contains(game)) { Notice = "Выберите игру для удаления."; return; }
        if (_busy || game.GameConfigSettings.Status == GameConfigApplyStatus.Applying)
        {
            Notice = "Дождитесь завершения текущей операции с игрой, затем повторите удаление.";
            return;
        }
        if (SettingBackupOwnership.HasForGame(_state, game))
        {
            Notice = "Игра не удалена: сначала восстановите сохранённые исходные параметры RTSS/NVIDIA.";
            return;
        }
        if (HasGameConfigArtifactsToPreserve(game.Id))
        {
            Notice = "Игра не удалена: сначала завершите восстановление, снимите назначения и удалите графические снимки.";
            return;
        }

        string name = game.Name;
        Games.Remove(game);
        Unhook(game);
        if (ReferenceEquals(SelectedGame, game)) SelectedGame = null;
        RefreshDisplays();
        SaveOnly(false);
        UpdateSummary();
        Notice = $"Игра «{name}» удалена из библиотеки GPM. Профиль и файлы игры не затронуты.";
    }
    private async Task ChooseExeAsync()
    {
        if (SelectedGame is not { } game) { Notice = "Выберите игру."; return; }
        string? path;
        try
        {
            string? currentDirectory = Path.GetDirectoryName(game.ExePath);
            path = await SelectLocalFileAsync("Выберите игровой .exe", "*.exe",
                Directory.Exists(currentDirectory) ? currentDirectory : game.InstallDirectory);
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            Notice = "Не удалось выбрать EXE: " + ex.Message;
            return;
        }
        if (path is null) return;
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        { Notice = "Выберите существующий игровой .exe."; return; }
        if (Games.Any(candidate => candidate != game &&
            SettingBackupOwnership.SameExecutablePath(candidate.ExePath, path)))
        { Notice = "Этот EXE уже назначен другой игре."; return; }
        if (SettingBackupOwnership.SameExecutablePath(game.ExePath, path)) return;
        if (!game.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase) &&
            SettingBackupOwnership.HasForGame(_state, game))
        {
            bool completed = await RunSyncAsync((service, state) =>
                service.Restore(state, state.Games.Single(item => item.Id == game.Id)),
                message: "Восстанавливаем исходные параметры прежнего EXE…");
            if (!completed || SettingBackupOwnership.HasForGame(_state, game))
            { Notice = "EXE не изменён: " + game.Detail; return; }
        }
        game.ExePath = path;
        game.Status = ApplyStatus.Pending;
        game.Detail = "EXE изменён; примените профиль к игре";
        SaveOnly(false);
        Notice = "EXE выбран. Для записи настроек примените профиль к игре.";
    }

    private async Task RestoreSelectedGameAsync()
    {
        if (SelectedGame is not { } game) { Notice = "Выберите игру."; return; }
        bool hadBackup = SettingBackupOwnership.HasForGame(_state, game);
        if (hadBackup)
        {
            bool completed = await RunSyncAsync((service, state) =>
                service.Restore(state, state.Games.Single(item => item.Id == game.Id)),
                message: "Восстанавливаем исходные параметры…");
            if (!completed) return;
            if (SettingBackupOwnership.HasForGame(_state, game) || game.Status is ApplyStatus.Error or ApplyStatus.Unsupported)
            { Notice = "Профиль сохранён: " + game.Detail; return; }
        }
        else if (game.ProfileId is null && !game.AutoSwitchProfile && game.DisplayProfileAssignments.Count == 0)
        { Notice = "Для этой игры нет назначенного профиля или резервных параметров."; return; }

        _updatingGameAssignment = true;
        try
        {
            game.ProfileId = null;
            game.AutoSwitchProfile = false;
            game.DisplayProfileAssignments.Clear();
            game.DisplayProfileTargetId = "";
            game.LastApplied = null;
            game.Status = ApplyStatus.Pending;
            game.Detail = "Профиль снят; параметры игры не применяются";
        }
        finally { _updatingGameAssignment = false; }
        RefreshDisplays();
        SaveOnly(false);
        Notice = hadBackup ? "Исходные параметры восстановлены; профиль игры снят."
            : "Профиль игры снят. Сохранённых исходных параметров для восстановления не было.";
    }

    private Task<string?> SelectLocalFileAsync(string title, string pattern, string? initialDirectory) =>
        PickLocalFileAsync?.Invoke(title, pattern, initialDirectory) ?? Task.FromResult<string?>(null);
    private async Task ImportSteamAsync()
    {
        if (!BeginActivity("Поиск установленных игр Steam…")) return;
        try
        {
            var previousGames = Games.ToArray();
            var discovery = await Task.Run(() => SteamDiscovery.DiscoverInstalled());
            if (_disposed) return;
            var protectedGameIds = Games.Where(game => HasGameConfigArtifactsToPreserve(game.Id)).Select(game => game.Id).ToHashSet();
            var result = GameImportService.MergeSteam(Games, _state.Backups, discovery.Games, protectedGameIds,
                discovery.PresentGameIds, discovery.IsComplete);
            foreach (var removed in previousGames.Where(game => !Games.Contains(game))) Unhook(removed);
            foreach (var game in result.AddedGames) Hook(game);
            RestoreGameSelection();
            Search = "";
            StatusFilter = "Все статусы";
            RefreshDisplays();
            Notice = !discovery.IsComplete
                ? $"Steam: найдено {result.Discovered}. Каталог прочитан не полностью; отсутствующие записи оставлены. Проверьте доступность всех библиотек Steam."
                : result.Discovered == 0
                    ? $"Steam: установленные игры не найдены; удалено из библиотеки GPM {result.Removed}, сохранено с данными для восстановления {result.Preserved}."
                    : $"Steam: найдено {result.Discovered}, добавлено {result.Added}, удалено отсутствующих/дублей/утилит {result.Removed}, сохранено с данными для восстановления {result.Preserved}. Для неоднозначных игр выберите .exe вручную.";
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Ошибка импорта Steam: " + ex.Message; }
        finally
        {
            EndActivity();
            if (!_disposed) { SaveOnly(false); UpdateSummary(); _ = ReconcileGameConfigsAsync(); }
        }
    }

    private async Task ImportEpicAsync()
    {
        if (!BeginActivity("Поиск установленных игр Epic Games…")) return;
        try
        {
            var previousGames = Games.ToArray();
            var discovery = await Task.Run(() => EpicDiscovery.DiscoverInstalled());
            if (_disposed) return;
            var protectedGameIds = Games.Where(game => HasGameConfigArtifactsToPreserve(game.Id)).Select(game => game.Id).ToHashSet();
            var result = GameImportService.MergeEpic(Games, discovery.Games, _state.Backups, protectedGameIds,
                discovery.PresentGameIds, discovery.IsComplete);
            foreach (var removed in previousGames.Where(game => !Games.Contains(game))) Unhook(removed);
            foreach (var game in result.AddedGames) Hook(game);
            RestoreGameSelection();
            Search = "";
            StatusFilter = "Все статусы";
            RefreshDisplays();
            Notice = !discovery.IsComplete
                ? $"EGS: найдено {result.Discovered}. Каталог прочитан не полностью; отсутствующие записи оставлены. Проверьте манифесты Epic Games Launcher."
                : result.Discovered == 0
                    ? $"EGS: установленные игры не найдены; удалено из библиотеки GPM {result.Removed}, сохранено с данными для восстановления {result.Preserved}."
                    : $"EGS: найдено {result.Discovered}, добавлено {result.Added}, уже в библиотеке {result.Existing}, удалено отсутствующих {result.Removed}, сохранено с данными для восстановления {result.Preserved}. Для неоднозначных игр выберите .exe вручную.";
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Ошибка импорта EGS: " + ex.Message; }
        finally
        {
            EndActivity();
            if (!_disposed) { SaveOnly(false); UpdateSummary(); _ = ReconcileGameConfigsAsync(); }
        }
    }

    /// <summary>
    /// При удалении дубликата сохраняет выбранную игру по пути EXE;
    /// переключение на первую запись допустимо только при отсутствии прежней.
    /// </summary>
    private void RestoreGameSelection()
    {
        if (SelectedGame is not null && Games.Contains(SelectedGame)) return;
        string? previousExe = SelectedGame?.ExePath;
        SelectedGame = !string.IsNullOrWhiteSpace(previousExe)
            ? Games.FirstOrDefault(game => game.ExePath.Equals(previousExe, StringComparison.OrdinalIgnoreCase))
                ?? Games.FirstOrDefault()
            : Games.FirstOrDefault();
    }

}
