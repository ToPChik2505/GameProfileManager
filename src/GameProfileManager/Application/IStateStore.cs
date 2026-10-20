using GameProfileManager.Core;

namespace GameProfileManager.Application;

/// <summary>
/// Контракт постоянного хранилища: бизнес-операции не зависят от нативных вызовов SQLite.
/// </summary>
public interface IStateStore
{
    /// <summary>Каталог файлов приложения.</summary>
    string DirectoryPath { get; }

    /// <summary>Считывает сохранённое состояние приложения.</summary>
    AppState Load();

    /// <summary>Надёжно сохраняет состояние, включая снимки значений до внешних записей.</summary>
    void Save(AppState state);

    /// <summary>Экспортирует пользовательскую копию настроек.</summary>
    string ExportBackup(AppState state);

    /// <summary>Фиксирует нештатное событие для последующей диагностики.</summary>
    void Log(Exception exception);
}
