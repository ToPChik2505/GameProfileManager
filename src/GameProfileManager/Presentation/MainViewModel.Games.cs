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
    private async Task ChooseExeAsync()
    {
        if (SelectedGame is not { } game) { Notice = "Выберите игру."; return; }
        string? path = await SelectLocalFileAsync("Выберите игровой процесс", "*.exe", game.InstallDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (path is null) return;
        if (!game.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase) &&
            SettingBackupOwnership.HasForGame(_state, game))
        {
            bool completed = await RunSyncAsync((service, state) =>
                service.Restore(state, state.Games.Single(item => item.Id == game.Id)));
            if (!completed || SettingBackupOwnership.HasForGame(_state, game))
            { Notice = game.Detail; return; }
        }
        game.ExePath = path;
        game.Status = ApplyStatus.Pending;
        game.Detail = "Нужно применить профиль";
        SaveOnly();
    }

    private Task<string?> SelectLocalFileAsync(string title, string pattern, string? initialDirectory) =>
        PickLocalFileAsync?.Invoke(title, pattern, initialDirectory) ?? Task.FromResult<string?>(null);
    private async Task ImportSteamAsync()
    {
        if (!BeginActivity("Поиск установленных игр Steam…")) return;
        try
        {
            var previousGames = Games.ToArray();
            var discovered = await Task.Run(() => SteamDiscovery.FindInstalled());
            if (_disposed) return;
            var result = GameImportService.MergeSteam(Games, _state.Backups, discovered);
            foreach (var removed in previousGames.Where(game => !Games.Contains(game))) Unhook(removed);
            foreach (var game in result.AddedGames) Hook(game);
            RestoreGameSelection();
            Search = "";
            StatusFilter = "Все статусы";
            RefreshDisplays();
            Notice = result.Discovered == 0
                ? "Steam: установленные игры не найдены. Проверьте путь к библиотеке Steam."
                : $"Steam: найдено {result.Discovered}, добавлено {result.Added}, убрано дублей и утилит {result.Removed}. Для неоднозначных игр выберите .exe вручную.";
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Ошибка импорта Steam: " + ex.Message; }
        finally
        {
            EndActivity();
            if (!_disposed) { SaveOnly(false); UpdateSummary(); }
        }
    }

    private async Task ImportEpicAsync()
    {
        if (!BeginActivity("Поиск установленных игр Epic Games…")) return;
        try
        {
            var discovered = await Task.Run(() => EpicDiscovery.FindInstalled());
            if (_disposed) return;
            var result = GameImportService.MergeEpic(Games, discovered);
            foreach (var game in result.AddedGames) Hook(game);
            RestoreGameSelection();
            Search = "";
            StatusFilter = "Все статусы";
            RefreshDisplays();
            Notice = result.Discovered == 0
                ? "EGS: установленные игры не найдены. Проверьте установку Epic Games Launcher."
                : $"EGS: найдено {result.Discovered}, добавлено {result.Added}, уже в библиотеке {result.Existing}. Для неоднозначных игр выберите .exe вручную.";
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Ошибка импорта EGS: " + ex.Message; }
        finally
        {
            EndActivity();
            if (!_disposed) { SaveOnly(false); UpdateSummary(); }
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
