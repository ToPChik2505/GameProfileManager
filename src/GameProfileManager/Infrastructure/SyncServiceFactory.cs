using GameProfileManager.Application;
using GameProfileManager.Core;
using GameProfileManager.Integrations;

namespace GameProfileManager.Infrastructure;

/// <summary>Composition root для реальных системных интеграций приложения.</summary>
public static class SyncServiceFactory
{
    /// <summary>Создаёт сервис синхронизации с драйверами и источником дисплеев.</summary>
    public static SyncService Create(IStateStore store,
        Func<IReadOnlyList<DisplayInfo>>? displayProvider = null) =>
        new(store, directory => new RtssClient(directory), () => new NvidiaClient(),
            displayProvider ?? WindowsDisplayDiscovery.Read);
}
