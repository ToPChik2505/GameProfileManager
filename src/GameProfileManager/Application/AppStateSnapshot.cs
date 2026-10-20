using GameProfileManager.Core;

namespace GameProfileManager.Application;

/// <summary>
/// Создаёт отделённое состояние для фоновой синхронизации. Наблюдаемые модели
/// исходного состояния принадлежат потоку WPF и не передаются внешним клиентам.
/// </summary>
public static class AppStateSnapshot
{
    public static AppState Capture(AppState source)
    {
        var copy = new AppState
        {
            PresetNamesLocalized = source.PresetNamesLocalized,
            PresetProtectionMigrated = source.PresetProtectionMigrated,
            RtssDirectory = source.RtssDirectory,
            RtssLimiterMode = source.RtssLimiterMode,
            CheckAtStartup = source.CheckAtStartup,
            MinimizeToTray = source.MinimizeToTray,
            Theme = source.Theme,
            DefaultDisplayId = source.DefaultDisplayId,
            LastSync = source.LastSync
        };
        foreach (var profile in source.Profiles)
            copy.Profiles.Add(new PerformanceProfile
            {
                Id = profile.Id, Name = profile.Name, FpsLimit = profile.FpsLimit,
                FpsMode = profile.FpsMode, FpsDivisor = profile.FpsDivisor,
                Vrr = profile.Vrr, VSync = profile.VSync, RtssLimiterMode = profile.RtssLimiterMode,
                Description = profile.Description, IsBuiltIn = profile.IsBuiltIn
            });
        foreach (var game in source.Games)
            copy.Games.Add(new GameEntry
            {
                Id = game.Id, Name = game.Name, ExePath = game.ExePath,
                ProfileId = game.ProfileId, DisplayId = game.DisplayId,
                Status = game.Status, Detail = game.Detail,
                SteamAppId = game.SteamAppId, EpicAppName = game.EpicAppName,
                InstallDirectory = game.InstallDirectory, LastApplied = game.LastApplied
            });
        copy.Backups.AddRange(source.Backups.Select(CopyBackup));
        return copy;
    }

    /// <summary>
    /// Вызывается только в потоке WPF после завершения операции. Не заменяет
    /// коллекции игр и профилей, чтобы сохранить их привязки и выбранные элементы.
    /// </summary>
    public static void PublishResults(AppState completed, AppState target)
    {
        var games = target.Games.ToDictionary(game => game.Id);
        foreach (var result in completed.Games)
        {
            if (!games.TryGetValue(result.Id, out var game)) continue;
            game.Status = result.Status;
            game.Detail = result.Detail;
            game.LastApplied = result.LastApplied;
        }
        target.Backups.Clear();
        target.Backups.AddRange(completed.Backups.Select(CopyBackup));
        target.LastSync = completed.LastSync;
    }

    private static SettingBackup CopyBackup(SettingBackup source) => new()
    {
        ExePath = source.ExePath,
        RtssFps = source.RtssFps,
        RtssSyncLimiter = source.RtssSyncLimiter,
        RtssProfileExisted = source.RtssProfileExisted,
        RtssCaptured = source.RtssCaptured,
        NvidiaVrr = source.NvidiaVrr,
        NvidiaVSync = source.NvidiaVSync,
        NvidiaFpsLimit = source.NvidiaFpsLimit,
        NvidiaVrrExplicit = source.NvidiaVrrExplicit,
        NvidiaVSyncExplicit = source.NvidiaVSyncExplicit,
        NvidiaFpsLimitExplicit = source.NvidiaFpsLimitExplicit,
        NvidiaProfileExisted = source.NvidiaProfileExisted,
        NvidiaCaptured = source.NvidiaCaptured,
        ManagedGameId = source.ManagedGameId,
        Created = source.Created
    };
}
