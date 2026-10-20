namespace GameProfileManager.Application;

/// <summary>Загрузка состояния завершилась ошибкой; запуск нужно остановить без создания пустой базы.</summary>
public sealed class StateLoadException(string databasePath, Exception innerException)
    : InvalidOperationException($"Не удалось прочитать базу настроек '{databasePath}'.", innerException)
{
    /// <summary>Путь к базе, которую приложение не смогло загрузить.</summary>
    public string DatabasePath { get; } = databasePath;
}
