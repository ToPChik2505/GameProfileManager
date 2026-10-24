using GameProfileManager.Core;

namespace GameProfileManager.Application;

/// <summary>
/// Создаёт отделённое состояние для фоновой синхронизации. Наблюдаемые модели
/// исходного состояния принадлежат UI-потоку и не передаются внешним клиентам.
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
            DisplayNames = new Dictionary<string, string>(source.DisplayNames, StringComparer.OrdinalIgnoreCase),
            LastSync = source.LastSync
        };
        copy.GameConfigSnapshots.AddRange(source.GameConfigSnapshots.Select(CopySnapshot));
        copy.GameConfigAssignments.AddRange(source.GameConfigAssignments.Select(item => new GameConfigAssignment
            { GameId = item.GameId, DisplayId = item.DisplayId, SnapshotId = item.SnapshotId }));
        copy.GameConfigOriginalBackups.AddRange(source.GameConfigOriginalBackups.Select(item => new GameConfigOriginalBackup
        {
            GameId = item.GameId, SourceId = item.SourceId, TargetPath = item.TargetPath,
            RegistryValueName = item.RegistryValueName, Existed = item.Existed, ContentPath = item.ContentPath,
            Sha256 = item.Sha256, RegistryKind = item.RegistryKind, Attributes = item.Attributes, CapturedAt = item.CapturedAt
        }));
        copy.GameConfigSafetyCopies.AddRange(source.GameConfigSafetyCopies.Select(item => new GameConfigSafetyCopy
        {
            Id = item.Id, GameId = item.GameId, SourceId = item.SourceId, ContentPath = item.ContentPath,
            Sha256 = item.Sha256, Existed = item.Existed, CreatedAt = item.CreatedAt, Reason = item.Reason
        }));
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
                AutoSwitchProfile = game.AutoSwitchProfile, DisplayProfilePolicy = game.DisplayProfilePolicy,
                DisplayProfileTargetId = game.DisplayProfileTargetId,
                DisplayProfileAssignments = new System.Collections.ObjectModel.ObservableCollection<DisplayProfileAssignment>(
                    game.DisplayProfileAssignments.Select(assignment => new DisplayProfileAssignment
                    { DisplayId = assignment.DisplayId, ProfileId = assignment.ProfileId })),
                GameConfigSettings = CopySettings(game.GameConfigSettings),
                Status = game.Status, Detail = game.Detail,
                SteamAppId = game.SteamAppId, EpicAppName = game.EpicAppName,
                InstallDirectory = game.InstallDirectory, LastApplied = game.LastApplied
            });
        copy.Backups.AddRange(source.Backups.Select(CopyBackup));
        return copy;
    }

    /// <summary>
    /// Вызывается только в UI-потоке после завершения операции. Не заменяет
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
            game.GameConfigSettings = CopySettings(result.GameConfigSettings);
        }
        target.Backups.Clear();
        target.Backups.AddRange(completed.Backups.Select(CopyBackup));
        target.GameConfigSnapshots = completed.GameConfigSnapshots.Select(CopySnapshot).ToList();
        target.GameConfigAssignments = completed.GameConfigAssignments.Select(item => new GameConfigAssignment
            { GameId = item.GameId, DisplayId = item.DisplayId, SnapshotId = item.SnapshotId }).ToList();
        target.GameConfigOriginalBackups = completed.GameConfigOriginalBackups.Select(item => new GameConfigOriginalBackup
        {
            GameId = item.GameId, SourceId = item.SourceId, TargetPath = item.TargetPath,
            RegistryValueName = item.RegistryValueName, Existed = item.Existed, ContentPath = item.ContentPath,
            Sha256 = item.Sha256, RegistryKind = item.RegistryKind, Attributes = item.Attributes, CapturedAt = item.CapturedAt
        }).ToList();
        target.GameConfigSafetyCopies = completed.GameConfigSafetyCopies.Select(item => new GameConfigSafetyCopy
        {
            Id = item.Id, GameId = item.GameId, SourceId = item.SourceId, ContentPath = item.ContentPath,
            Sha256 = item.Sha256, Existed = item.Existed, CreatedAt = item.CreatedAt, Reason = item.Reason
        }).ToList();
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

    private static GameConfigSnapshot CopySnapshot(GameConfigSnapshot source) => new()
    {
        Id = source.Id, GameId = source.GameId, Name = source.Name, CreatedAt = source.CreatedAt,
        SchemaVersion = source.SchemaVersion,
        Items = source.Items.Select(item => new GameConfigSnapshotItem
        {
            SourceId = item.SourceId, Size = item.Size, Sha256 = item.Sha256, ContentPath = item.ContentPath,
            SourceExisted = item.SourceExisted, OriginalAttributes = item.OriginalAttributes, RegistryKind = item.RegistryKind
        }).ToList()
    };

    private static GameConfigSettings CopySettings(GameConfigSettings source) => new()
    {
        AutoApply = source.AutoApply,
        Sources = new System.Collections.ObjectModel.ObservableCollection<GameConfigSource>(source.Sources.Select(item => new GameConfigSource
        {
            Id = item.Id, GameId = item.GameId, Kind = item.Kind, TargetPath = item.TargetPath,
            ApprovedRootPath = item.ApprovedRootPath, RegistryValueName = item.RegistryValueName,
            DefinitionId = item.DefinitionId, UserConfirmed = item.UserConfirmed,
            BinaryExplicitlyAllowed = item.BinaryExplicitlyAllowed
        })),
        AdditionalProcessPaths = [.. source.AdditionalProcessPaths],
        LastAppliedHashes = new Dictionary<Guid, string>(source.LastAppliedHashes),
        LastAppliedExists = new Dictionary<Guid, bool>(source.LastAppliedExists),
        LastAppliedSnapshotId = source.LastAppliedSnapshotId, DesiredSnapshotId = source.DesiredSnapshotId,
        DesiredDisplayId = source.DesiredDisplayId, DesiredGeneration = source.DesiredGeneration,
        LastAppliedAt = source.LastAppliedAt, Status = source.Status, StatusDetail = source.StatusDetail,
        Events = source.Events.Select(item => new GameConfigEvent { At = item.At, Status = item.Status, Message = item.Message }).ToList()
    };
}
