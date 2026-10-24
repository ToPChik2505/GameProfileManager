using GameProfileManager.Core;

namespace GameProfileManager.Application;

public sealed record GameConfigTarget(string DisplayId, Guid? SnapshotId, bool IsResolved, string Reason);

/// <summary>Computes the independent graphics target from the same effective-display rules as profile selection.</summary>
public static class GameConfigCoordinator
{
    public static GameConfigTarget ResolveTarget(AppState state, GameEntry game, IReadOnlyList<DisplayInfo> displays)
    {
        if (game.GameConfigSettings.Sources.Count == 0)
            return new("", null, true, "Добавьте файлы графических настроек.");
        var display = EffectiveDisplayResolver.Resolve(state, game, displays);
        if (!display.IsResolved || display.Display is null)
            return new("", null, false, display.Reason + ". Графические файлы не изменены.");
        var matching = state.GameConfigAssignments.Where(item => item.GameId == game.Id &&
            item.DisplayId.Equals(display.Display.Id, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matching.Length > 1)
            return new(display.Display.Id, null, false, "Для этого дисплея обнаружено несколько назначений снимка.");
        if (matching.Length == 0)
            return new(display.Display.Id, null, true, $"Для «{display.Display.Name}» снимок не назначен; файлы останутся без изменений.");
        var snapshot = state.GameConfigSnapshots.FirstOrDefault(item => item.Id == matching[0].SnapshotId && item.GameId == game.Id);
        if (snapshot is null)
            return new(display.Display.Id, matching[0].SnapshotId, false, "Назначенный снимок отсутствует. Графические файлы не изменены.");
        var sourceIds = game.GameConfigSettings.Sources.Select(source => source.Id).ToHashSet();
        if (snapshot.Items.Count != sourceIds.Count || snapshot.Items.Any(item => !sourceIds.Contains(item.SourceId)))
            return new(display.Display.Id, snapshot.Id, false, "Список источников изменился. Сохраните новую ревизию перед применением.");
        return new(display.Display.Id, snapshot.Id, true, $"Для «{display.Display.Name}» выбран снимок «{snapshot.Name}».");
    }

    public static void SetDesired(GameConfigSettings settings, GameConfigTarget target)
    {
        bool changed = settings.DesiredSnapshotId != target.SnapshotId ||
            !settings.DesiredDisplayId.Equals(target.DisplayId, StringComparison.OrdinalIgnoreCase);
        if (changed) settings.DesiredGeneration++;
        settings.DesiredSnapshotId = target.SnapshotId;
        settings.DesiredDisplayId = target.DisplayId;
        // A display change alone must not clear a file conflict or an interrupted-transaction
        // recovery requirement. Reconciliation can clear a conflict only for a saved snapshot state.
        if (settings.Status is GameConfigApplyStatus.Conflict or GameConfigApplyStatus.RecoveryRequired)
            return;
        if (!target.IsResolved)
        {
            settings.Status = settings.AutoApply ? GameConfigApplyStatus.Pending : GameConfigApplyStatus.Ready;
            settings.StatusDetail = target.Reason;
        }
        else if (changed)
        {
            settings.Status = settings.Sources.Count == 0 ? GameConfigApplyStatus.NotConfigured :
                settings.AutoApply && target.SnapshotId is not null ? GameConfigApplyStatus.Pending : GameConfigApplyStatus.Ready;
            settings.StatusDetail = target.Reason;
        }
        else if (settings.Status is not (GameConfigApplyStatus.Applied or GameConfigApplyStatus.Conflict or
                     GameConfigApplyStatus.RecoveryRequired or GameConfigApplyStatus.Error or GameConfigApplyStatus.WaitingForGameExit))
        {
            settings.Status = settings.Sources.Count == 0 ? GameConfigApplyStatus.NotConfigured : GameConfigApplyStatus.Ready;
            settings.StatusDetail = target.Reason;
        }
    }
}
