using GameProfileManager.Application;

namespace GameProfileManager;

public sealed partial class MainViewModel
{
    private void RebuildAttentionItems()
    {
        var probe = _latestDiagnosticsProbe;
        var snapshot = DiagnosticsService.Capture(_state, _displaySnapshot,
            _sync.RtssStatus, _sync.RtssStatus is null ? null : _sync.RtssReady, _sync.NvidiaStatus,
            _sync.NvidiaStatus is null ? null : _sync.NvidiaStatus.StartsWith("NVAPI DRS доступен", StringComparison.OrdinalIgnoreCase),
            probe);
        var next = DiagnosticsService.Analyze(snapshot)
            .Where(result => result.Severity != DiagnosticSeverity.Ok)
            .Select(result => new AttentionItem(
                result.Severity switch
                {
                    DiagnosticSeverity.Error => "ОШИБКА",
                    DiagnosticSeverity.Warning => "ПРЕДУПРЕЖДЕНИЕ",
                    DiagnosticSeverity.Info => "ИНФОРМАЦИЯ",
                    _ => "OK"
                }, result.Subject, result.Summary, result.Details, result.SuggestedAction,
                result.Target == DiagnosticActionTarget.Settings ? 5 : 2,
                result.Target switch
                {
                    DiagnosticActionTarget.Settings => "Открыть настройки",
                    DiagnosticActionTarget.ChooseExe => "Выбрать .exe",
                    _ => "Открыть игру"
                }, result.GameId is Guid id ? Games.FirstOrDefault(game => game.Id == id) : null,
                result.Severity, result.RuleId, result.CanAutoFix))
            .ToArray();

        // Сохраняем существующие элементы, если данные не изменились: это
        // предотвращает повторное создание карточек WinUI при каждом сохранении.
        if (AttentionItems.SequenceEqual(next)) return;
        int common = Math.Min(AttentionItems.Count, next.Length);
        for (int index = 0; index < common; index++)
            if (!Equals(AttentionItems[index], next[index]))
                AttentionItems[index] = next[index];
        while (AttentionItems.Count > next.Length)
            AttentionItems.RemoveAt(AttentionItems.Count - 1);
        for (int index = common; index < next.Length; index++)
            AttentionItems.Add(next[index]);
    }
}
