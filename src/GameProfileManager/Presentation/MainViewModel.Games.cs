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
    private void ChooseExe()
    {
        if (SelectedGame is null) { Notice = "Выберите игру."; return; }
        string? path = SelectLocalFile("Выберите игровой процесс", "*.exe", SelectedGame.InstallDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (path is null) return;
        if (!SelectedGame.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase) &&
            _state.Backups.Any(b => b.ExePath.Equals(SelectedGame.ExePath, StringComparison.OrdinalIgnoreCase)))
        {
            Run(() => _sync.Restore(_state, SelectedGame));
            if (SelectedGame.Status == ApplyStatus.Error) { Notice = SelectedGame.Detail; return; }
        }
        SelectedGame.ExePath = path;
        SelectedGame.Status = ApplyStatus.Pending;
        SelectedGame.Detail = "Нужно применить профиль";
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
    private void ImportSteam()
    {
        Run(() =>
        {
            var result = GameImportService.MergeSteam(Games, _state.Backups, SteamDiscovery.FindInstalled());
            foreach (var game in result.AddedGames) Hook(game);
            RestoreGameSelection();
            Search = "";
            StatusFilter = "Все статусы";
            RefreshDisplays();
            SaveOnly();
            Notice = result.Discovered == 0
                ? "Steam: установленные игры не найдены. Проверьте путь к библиотеке Steam."
                : $"Steam: найдено {result.Discovered}, добавлено {result.Added}, убрано дублей и утилит {result.Removed}. Для неоднозначных игр выберите .exe вручную.";
        });
    }

    private void ImportEpic()
    {
        Run(() =>
        {
            var result = GameImportService.MergeEpic(Games, EpicDiscovery.FindInstalled());
            foreach (var game in result.AddedGames) Hook(game);
            RestoreGameSelection();
            Search = "";
            StatusFilter = "Все статусы";
            RefreshDisplays();
            SaveOnly();
            Notice = result.Discovered == 0
                ? "EGS: установленные игры не найдены. Проверьте установку Epic Games Launcher."
                : $"EGS: найдено {result.Discovered}, добавлено {result.Added}, уже в библиотеке {result.Existing}. Для неоднозначных игр выберите .exe вручную.";
        });
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
