using GameProfileManager.Core;

namespace GameProfileManager.Application;

public enum AttentionTarget { Settings, Game, ChooseExe }

/// <summary>Причина, требующая действия пользователя, без привязки к WPF-навигации.</summary>
public sealed record AttentionFinding(string Category, string Title, string Detail, string Advice,
    AttentionTarget Target, GameEntry? Game);

/// <summary>Анализирует локальное состояние и результаты чтения интеграций без внешней записи.</summary>
public static class AttentionService
{
    public static IReadOnlyList<AttentionFinding> Analyze(AppState state, string? rtssStatus,
        bool rtssReady, string? nvidiaStatus, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var findings = new List<AttentionFinding>();
        if (rtssStatus is not null && !rtssReady)
            findings.Add(new("ИНТЕГРАЦИЯ", "RTSS недоступен", rtssStatus,
                "Запустите RTSS. Если он не найден, укажите папку установки в настройках, затем проверьте игры.",
                AttentionTarget.Settings, null));
        if (nvidiaStatus?.StartsWith("NVIDIA недоступна", StringComparison.OrdinalIgnoreCase) == true)
            findings.Add(new("ИНТЕГРАЦИЯ", "NVIDIA недоступна", nvidiaStatus,
                "Проверьте драйвер NVIDIA и повторите проверку. Пока DRS недоступен, профили не применяются.",
                AttentionTarget.Settings, null));

        var backedUpGames = state.Backups.Select(backup => backup.ManagedGameId).ToHashSet();
        foreach (var game in state.Games)
        {
            if (game.ProfileId is null)
            {
                if (backedUpGames.Contains(game.Id))
                    findings.Add(new("ВОССТАНОВЛЕНИЕ", game.Name, game.Detail,
                        "Профиль снят. Нажмите «Применить» у игры, чтобы восстановить исходные значения RTSS и NVIDIA.",
                        AttentionTarget.Game, game));
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.ExePath))
            {
                findings.Add(new("НУЖЕН .EXE", game.Name, game.Detail,
                    "Выберите игровой исполняемый файл. Лаунчер и служебный процесс для профиля не подходят.",
                    AttentionTarget.ChooseExe, game));
                continue;
            }
            if (game.Status == ApplyStatus.Applied) continue;
            string advice = game.Status switch
            {
                ApplyStatus.RestartRequired => "Настройки записаны. Закройте и снова запустите игру, затем проверьте статус.",
                ApplyStatus.Pending => "Настройки отличаются или ещё не применялись. Нажмите «Применить» в строке игры.",
                ApplyStatus.Unsupported when game.Detail.Contains("экран", StringComparison.OrdinalIgnoreCase) ||
                    game.Detail.Contains("частот", StringComparison.OrdinalIgnoreCase) ||
                    game.Detail.Contains("делител", StringComparison.OrdinalIgnoreCase) ||
                    game.Detail.Contains("разделить", StringComparison.OrdinalIgnoreCase) =>
                    "Проверьте экран игры и выберите точную долю герцовки либо ручной FPS в профиле.",
                ApplyStatus.Unsupported when game.Detail.Contains("RTSS", StringComparison.OrdinalIgnoreCase) =>
                    "Запустите RTSS и проверьте доступ к его папке, затем повторите применение.",
                ApplyStatus.Error when !fileExists(game.ExePath) =>
                    "Файл игры не найден. Выберите актуальный игровой .exe в разделе «Мои игры».",
                _ => "Откройте игру, прочитайте причину и повторите проверку или применение после исправления."
            };
            findings.Add(new(game.StatusLabel.ToUpperInvariant(), game.Name, game.Detail,
                advice, AttentionTarget.Game, game));
        }
        return findings;
    }
}
