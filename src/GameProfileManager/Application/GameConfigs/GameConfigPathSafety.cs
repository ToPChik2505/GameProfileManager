namespace GameProfileManager.Application;

public static class GameConfigPathSafety
{
    public const int MaxFileBytes = 32 * 1024 * 1024;
    public const int MaxSnapshotBytes = 128 * 1024 * 1024;
    public const int MaxSourceCount = 32;
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".sys", ".msi", ".bat", ".cmd", ".ps1", ".com", ".scr", ".lnk" };
    private static readonly HashSet<string> SaveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".sav", ".save", ".savegame", ".cloudsave" };
    private static readonly HashSet<string> TextConfigurationExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ini", ".json", ".cfg", ".xml", ".toml", ".yaml", ".yml", ".txt", ".conf", ".config", ".properties", ".settings" };

    public static bool ValidateCandidate(string path, bool allowBinary, out string reason)
    {
        reason = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains("..", StringComparison.Ordinal))
                return Fail("В пути обнаружен небезопасный компонент.", out reason);
            string full = Path.GetFullPath(path);
            string extension = Path.GetExtension(full);
            if (ExecutableExtensions.Contains(extension)) return Fail("Исполняемые и системные файлы не поддерживаются.", out reason);
            if (SaveExtensions.Contains(extension) || full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part.Equals("SaveGames", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("Saved Games", StringComparison.OrdinalIgnoreCase) ||
                                 part.Equals("CloudStorage", StringComparison.OrdinalIgnoreCase)))
                return Fail("Путь похож на игровые сохранения или облачные данные.", out reason);
            string leaf = Path.GetFileName(full);
            if (leaf.Equals("remotecache.vdf", StringComparison.OrdinalIgnoreCase) ||
                leaf.Equals("steam_autocloud.vdf", StringComparison.OrdinalIgnoreCase) ||
                leaf.EndsWith(".cloudmeta", StringComparison.OrdinalIgnoreCase))
                return Fail("Облачные метаданные не поддерживаются.", out reason);
            string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameProfileManager");
            if (IsWithin(full, system) || IsWithin(full, programData) || IsWithin(full, appData))
                return Fail("Нельзя управлять файлами Windows, ProgramData или самого GameProfileManager.", out reason);
            if (!allowBinary && IsLikelyBinaryExtension(extension))
                return Fail("Для этого типа файла требуется явное подтверждение бинарной конфигурации.", out reason);
            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                if (info.Length > MaxFileBytes) return Fail($"Размер файла превышает {MaxFileBytes / 1024 / 1024} МБ.", out reason);
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    return Fail("Символические ссылки и junction points не поддерживаются.", out reason);
            }
            for (string? directory = Path.GetDirectoryName(full); directory is not null; directory = Path.GetDirectoryName(directory))
            {
                if (!Directory.Exists(directory)) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return Fail("В пути есть символическая ссылка или junction point.", out reason);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Fail("Не удалось проверить путь: " + ex.Message, out reason); }
    }

    public static bool ValidateConfigurationDirectory(string path, out string reason)
    {
        reason = "";
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains("..", StringComparison.Ordinal))
                return Fail("В пути обнаружен небезопасный компонент.", out reason);
            string full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return Fail("Каталог конфигурации не существует.", out reason);
            string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameProfileManager");
            if (IsWithin(full, system) || IsWithin(full, programData) || IsWithin(full, appData))
                return Fail("Нельзя искать конфигурации в системных каталогах или каталоге самого GameProfileManager.", out reason);
            for (string? directory = full; directory is not null; directory = Path.GetDirectoryName(directory))
            {
                if (!Directory.Exists(directory)) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    return Fail("В пути есть символическая ссылка или junction point.", out reason);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Fail("Не удалось проверить каталог: " + ex.Message, out reason); }
    }

    public static bool IsLikelyBinaryExtension(string extension) => !TextConfigurationExtensions.Contains(extension);

    public static bool IsWithin(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static bool Fail(string message, out string reason) { reason = message; return false; }
}
