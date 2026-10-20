using System.Collections.ObjectModel;
using GameProfileManager.Core;
using GameProfileManager.Integrations;

namespace GameProfileManager.Application;

/// <summary>Результат объединения найденных игр с локальной библиотекой.</summary>
public sealed record GameImportResult(
    int Discovered,
    int Added,
    int Removed,
    int Existing,
    IReadOnlyList<GameEntry> AddedGames);

/// <summary>
/// Сопоставляет импортированные игры с существующими записями.
/// Не выполняет файловый ввод-вывод и не меняет назначенные пользователем профили.
/// </summary>
public static class GameImportService
{
    /// <summary>Объединяет Steam-каталог, сохраняя ручные записи и резервные настройки.</summary>
    public static GameImportResult MergeSteam(
        ObservableCollection<GameEntry> games,
        IReadOnlyList<SettingBackup> backups,
        IReadOnlyList<GameEntry> discovered)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(backups);
        ArgumentNullException.ThrowIfNull(discovered);
        int removed = 0;
        var added = new List<GameEntry>();

        foreach (var utility in games.Where(game =>
                     game.SteamAppId is not null &&
                     SteamDiscovery.NonGameAppIds.Contains(game.SteamAppId) &&
                     game.ProfileId is null &&
                     !backups.Any(backup => backup.ManagedGameId == game.Id)).ToArray())
        {
            games.Remove(utility);
            removed++;
        }

        foreach (var game in discovered)
        {
            var existing = games.FirstOrDefault(other => other.SteamAppId == game.SteamAppId);
            var manuallyAdded = games.FirstOrDefault(other =>
                other.SteamAppId is null && !string.IsNullOrWhiteSpace(game.ExePath) &&
                other.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));

            // Если тот же EXE добавлен вручную, предпочтительнее сохранить эту запись:
            // к ней могут быть привязаны пользовательские настройки и идентификатор.
            if (manuallyAdded is not null && existing is not null &&
                existing.ProfileId is null &&
                !backups.Any(backup => backup.ManagedGameId == existing.Id))
            {
                games.Remove(existing);
                existing = null;
                removed++;
            }

            if (manuallyAdded is not null && existing is null)
            {
                manuallyAdded.SteamAppId = game.SteamAppId;
                manuallyAdded.InstallDirectory = game.InstallDirectory;
                if (manuallyAdded.Name.Equals(Path.GetFileNameWithoutExtension(manuallyAdded.ExePath),
                        StringComparison.OrdinalIgnoreCase))
                    manuallyAdded.Name = game.Name;
                manuallyAdded.Icon = null;
                continue;
            }

            if (existing is not null)
            {
                if (string.IsNullOrWhiteSpace(existing.ExePath) && !string.IsNullOrWhiteSpace(game.ExePath))
                    existing.ExePath = game.ExePath;
                continue;
            }

            games.Add(game);
            added.Add(game);
        }

        return new GameImportResult(discovered.Count, added.Count, removed, 0, added);
    }

    /// <summary>Объединяет Epic Games Store по AppName или EXE без сброса профилей.</summary>
    public static GameImportResult MergeEpic(
        ObservableCollection<GameEntry> games,
        IReadOnlyList<GameEntry> discovered)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(discovered);
        int existingCount = 0;
        var added = new List<GameEntry>();

        foreach (var game in discovered)
        {
            var existing = games.FirstOrDefault(other => other.EpicAppName is not null &&
                other.EpicAppName.Equals(game.EpicAppName, StringComparison.OrdinalIgnoreCase));
            existing ??= games.FirstOrDefault(other => !string.IsNullOrWhiteSpace(game.ExePath) &&
                other.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.EpicAppName = game.EpicAppName;
                existing.InstallDirectory ??= game.InstallDirectory;
                if (string.IsNullOrWhiteSpace(existing.ExePath) && !string.IsNullOrWhiteSpace(game.ExePath))
                    existing.ExePath = game.ExePath;
                if (existing.Name.Equals(Path.GetFileNameWithoutExtension(existing.ExePath),
                        StringComparison.OrdinalIgnoreCase))
                    existing.Name = game.Name;
                existingCount++;
                continue;
            }

            games.Add(game);
            added.Add(game);
        }

        return new GameImportResult(discovered.Count, added.Count, 0, existingCount, added);
    }
}
