using GameProfileManager.Core;

namespace GameProfileManager;

/// <summary>Раздел обязанностей основной модели представления.</summary>
public sealed partial class MainViewModel
{
    private void RebuildAttentionItems()
    {
        // Сначала собираем новое представление. Пересоздание ObservableCollection на каждом
        // сохранении профиля провоцировало повторную отрисовку WPF без изменений данных.
        var next = new List<AttentionItem>();
        if (_sync.RtssStatus is not null && !_sync.RtssReady)
            next.Add(new("ИНТЕГРАЦИЯ", "RTSS недоступен", _sync.RtssStatus,
                "Запустите RTSS. Если он не найден, укажите папку установки в настройках, затем проверьте игры.",
                5, "Открыть настройки", null));
        if (_sync.NvidiaStatus?.StartsWith("NVIDIA недоступна", StringComparison.OrdinalIgnoreCase) == true)
            next.Add(new("ИНТЕГРАЦИЯ", "NVIDIA недоступна", _sync.NvidiaStatus,
                "Проверьте драйвер NVIDIA и повторите проверку. Пока DRS недоступен, профили не применяются.",
                5, "Открыть настройки", null));
        foreach (var game in Games)
        {
            bool hasBackup = _state.Backups.Any(backup => backup.ManagedGameId == game.Id);
            if (game.ProfileId is null)
            {
                if (hasBackup)
                    next.Add(new("ВОССТАНОВЛЕНИЕ", game.Name, game.Detail,
                        "Профиль снят. Нажмите «Применить» у игры, чтобы восстановить исходные значения RTSS и NVIDIA.",
                        2, "Открыть игру", game));
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.ExePath))
            {
                next.Add(new("НУЖЕН .EXE", game.Name, game.Detail,
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
            next.Add(new(game.StatusLabel.ToUpperInvariant(), game.Name, game.Detail,
                advice, 2, "Открыть игру", game));
        }

        if (AttentionItems.SequenceEqual(next)) return;
        int common = Math.Min(AttentionItems.Count, next.Count);
        for (int index = 0; index < common; index++)
            if (!Equals(AttentionItems[index], next[index]))
                AttentionItems[index] = next[index];
        while (AttentionItems.Count > next.Count)
            AttentionItems.RemoveAt(AttentionItems.Count - 1);
        for (int index = common; index < next.Count; index++)
            AttentionItems.Add(next[index]);
    }
}

