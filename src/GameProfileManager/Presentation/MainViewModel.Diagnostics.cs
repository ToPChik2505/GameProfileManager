using GameProfileManager.Application;

namespace GameProfileManager;

public sealed partial class MainViewModel
{
    private void RebuildAttentionItems()
    {
        var next = AttentionService.Analyze(_state, _sync.RtssStatus, _sync.RtssReady, _sync.NvidiaStatus)
            .Select(finding => new AttentionItem(finding.Category, finding.Title, finding.Detail,
                finding.Advice, finding.Target == AttentionTarget.Settings ? 5 : 2,
                finding.Target switch
                {
                    AttentionTarget.Settings => "Открыть настройки",
                    AttentionTarget.ChooseExe => "Выбрать .exe",
                    _ => "Открыть игру"
                }, finding.Game))
            .ToArray();

        // Сохраняем существующие элементы, если данные не изменились: это
        // предотвращает повторное создание карточек WPF при каждом сохранении.
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
