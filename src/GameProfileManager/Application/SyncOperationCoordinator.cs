using GameProfileManager.Core;

namespace GameProfileManager.Application;

/// <summary>
/// Сериализует обращения к RTSS/NVIDIA и сохраняет результат на отделённом
/// состоянии. Публикация в моделях WinUI выполняется вызывающим кодом после await.
/// </summary>
public sealed class SyncOperationCoordinator(SyncService service)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<Exception?> ExecuteAsync(AppState snapshot, Action<SyncService, AppState> operation)
    {
        await _gate.WaitAsync();
        try
        {
            try
            {
                await Task.Run(() => operation(service, snapshot));
                return null;
            }
            catch (Exception ex) { return ex; }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Serializes file/registry configuration transactions with RTSS/NVIDIA operations.</summary>
    public async Task<Exception?> ExecuteGameConfigAsync(Func<Task> operation)
    {
        await _gate.WaitAsync();
        try
        {
            try { await operation(); return null; }
            catch (Exception ex) { return ex; }
        }
        finally { _gate.Release(); }
    }
}
