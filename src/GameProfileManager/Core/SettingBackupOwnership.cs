namespace GameProfileManager.Core;

/// <summary>Находит резервную копию по владельцу и безопасно разрешает старые записи без ID.</summary>
public static class SettingBackupOwnership
{
    /// <summary>Сравнивает пути к EXE без учёта регистра и относительных сегментов.</summary>
    public static bool SameExecutablePath(string? left, string? right) => SamePath(left, right);

    /// <summary>Проверяет, есть ли резервная копия, связанная с игрой или её старым путём EXE.</summary>
    public static bool HasForGame(AppState state, GameEntry game) => HasForGame(state.Backups, game);

    /// <summary>Проверяет владение по ID или наличие совместимой старой записи по пути.</summary>
    public static bool HasForGame(IEnumerable<SettingBackup> backups, GameEntry game)
    {
        if (backups.Any(backup => backup.ManagedGameId == game.Id)) return true;
        try
        {
            return backups.Any(backup => backup.ManagedGameId == Guid.Empty && SamePath(backup.ExePath, game.ExePath));
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// Возвращает единственную копию игры. Старую запись без ID связывает с игрой,
    /// только когда путь EXE однозначно принадлежит одной записи библиотеки.
    /// </summary>
    public static SettingBackup? FindForGame(AppState state, GameEntry game)
    {
        var owned = state.Backups.Where(backup => backup.ManagedGameId == game.Id).ToArray();
        var legacy = state.Backups.Where(backup => backup.ManagedGameId == Guid.Empty &&
            SamePath(backup.ExePath, game.ExePath)).ToArray();
        if (owned.Length + legacy.Length > 1)
            throw new InvalidOperationException("Для игры найдены неоднозначные резервные копии. Восстановление заблокировано.");
        if (owned.Length == 1) return owned[0];
        if (legacy.Length == 0)
        {
            bool foreignOwner = state.Backups.Any(backup => backup.ManagedGameId != Guid.Empty &&
                backup.ManagedGameId != game.Id && SamePath(backup.ExePath, game.ExePath));
            if (foreignOwner)
                throw new InvalidOperationException("Путь EXE уже связан с резервной копией другой игры. Операция заблокирована.");
            return null;
        }

        int owners = state.Games.Count(candidate => SamePath(candidate.ExePath, legacy[0].ExePath));
        if (owners != 1 || !ReferenceEquals(state.Games.FirstOrDefault(candidate => SamePath(candidate.ExePath, legacy[0].ExePath)), game))
            throw new InvalidOperationException("Старая резервная копия совпадает с несколькими играми. Восстановление заблокировано.");
        return legacy[0];
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
