using GameProfileManager.Core;

namespace GameProfileManager;

/// <summary>Раздел обязанностей основной модели представления.</summary>
public sealed partial class MainViewModel
{
    private void RebuildAttentionItems()
    {
        AttentionItems.Clear();
        if (_sync.RtssStatus is not null && !_sync.RtssReady)
            AttentionItems.Add(new("ИНТЕГРАЦИЯ", "RTSS недоступен", _sync.RtssStatus,
                "Запустите RTSS. Если он не найден, укажите папку установки в настройках, затем проверьте игры.",
                5, "Открыть настройки", null));
        if (_sync.NvidiaStatus?.StartsWith("NVIDIA недоступна", StringComparison.OrdinalIgnoreCase) == true)
            AttentionItems.Add(new("ИНТЕГРАЦИЯ", "NVIDIA недоступна", _sync.NvidiaStatus,
                "Проверьте драйвер NVIDIA и повторите проверку. Пока DRS недоступен, профили не применяются.",
                5, "Открыть настройки", null));
        foreach (var game in Games)
        {
            bool hasBackup = _state.Backups.Any(backup => backup.ManagedGameId == game.Id);
            if (game.ProfileId is null)
            {
                if (hasBackup)
                    AttentionItems.Add(new("ВОССТАНОВЛЕНИЕ", game.Name, game.Detail,
                        "Профиль снят. Нажмите «Применить» у игры, чтобы восстановить исходные значения RTSS и NVIDIA.",
                        2, "Открыть игру", game));
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.ExePath))
            {
                AttentionItems.Add(new("НУЖЕН .EXE", game.Name, game.Detail,
                    "Выберите игровой исполняемый файл. Лаунчер и служебный процесс для профиля не подходят.",
                    2, "Выбрать .exe", game));
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
                ApplyStatus.Error when !File.Exists(game.ExePath) =>
                    "Файл игры не найден. Выберите актуальный игровой .exe в разделе «Мои игры».",
                _ => "Откройте игру, прочитайте причину и повторите проверку или применение после исправления."
            };
            AttentionItems.Add(new(game.StatusLabel.ToUpperInvariant(), game.Name, game.Detail,
                advice, 2, "Открыть игру", game));
        }
    }}
