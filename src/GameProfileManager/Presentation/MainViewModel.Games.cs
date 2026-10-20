using GameProfileManager.Core;
using GameProfileManager.Integrations;
using GameProfileManager.Application;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GameProfileManager;

/// <summary>Раздел обязанностей основной модели представления.</summary>
public sealed partial class MainViewModel
{
    private void AddGame()
    {
        string? path = SelectLocalFile("Выберите игровой .exe", "*.exe", null);
        if (path is null) return;
        if (Games.Any(g => g.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase))) { Notice = "Этот .exe уже добавлен."; return; }
        var game = new GameEntry { Name = Path.GetFileNameWithoutExtension(path), ExePath = path, Detail = "Выберите профиль" };
        Games.Add(game); Hook(game); SelectedGame = game; RefreshDisplays(); SaveOnly(); UpdateSummary();
    }
    private async Task ChooseExeAsync()
    {
        if (SelectedGame is not { } game) { Notice = "Выберите игру."; return; }
        string? path = SelectLocalFile("Выберите игровой процесс", "*.exe", game.InstallDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
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

    private string? SelectLocalFile(string title, string pattern, string? initialDirectory)
    {
        if (PickLocalFile is not null) return PickLocalFile(title, pattern, initialDirectory);
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = pattern == "*.exe" ? "Приложения (*.exe)|*.exe" : "RTSSHooks64.dll|RTSSHooks64.dll",
            InitialDirectory = initialDirectory ?? ""
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private async Task ImportSteamAsync()
    {
        if (_busy) return;
        _busy = true;
        SyncProgressMessage = "Поиск установленных игр Steam…";
        IsApplyingAll = true;
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
            _busy = false;
            IsApplyingAll = false;
            if (!_disposed) { SaveOnly(false); UpdateSummary(); }
        }
    }

    private async Task ImportEpicAsync()
    {
        if (_busy) return;
        _busy = true;
        SyncProgressMessage = "Поиск установленных игр Epic Games…";
        IsApplyingAll = true;
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
            _busy = false;
            IsApplyingAll = false;
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
