using System.Diagnostics;
using System.Text.RegularExpressions;
using GameProfileManager.Core;

namespace GameProfileManager.Application;

internal sealed class LocalGameConfigSearch
{
    private const int MaxCandidates = 32;
    private static readonly string[] ConfigNames =
    [
        "GameUserSettings.ini", "Engine.ini", "Game.ini", "Input.ini", "UserSettings.ini", "UserSettings.json",
        "userSettings.json", "Settings.ini", "settings.ini", "Settings.json", "settings.json", "renderer.ini",
        "user.cfg", "config.ini", "config.json", "options.ini", "options.json", "graphics.ini", "display.ini",
        "preferences.ini", "game.cfg", "GraphicOptions.JSON", "WindowInfo.JSON", "resolution.xml"
    ];
    private static readonly string[] UnrealConfigFolders = ["Windows", "WindowsClient", "WindowsNoEditor", "WinGDK"];

    public Task<IReadOnlyList<GameConfigCandidate>> FindCandidatesAsync(GameEntry game, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<GameConfigCandidate>>(() => FindLocal(game, cancellationToken), cancellationToken);

    private static IReadOnlyList<GameConfigCandidate> FindLocal(GameEntry game, CancellationToken cancellationToken)
    {
        string[] identity = GetIdentity(game, out string[] primaryIdentity)
            .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var primaryIdentityNames = primaryIdentity.Select(Normalize).Where(name => name.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (identity.Length == 0) return [];
        string? installDirectory = GetInstallDirectory(game);
        string? executableDirectory = Path.GetDirectoryName(game.ExePath);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local
                ? Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow") : "",
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"),
            GetSavedGamesDirectory(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            installDirectory ?? "", executableDirectory ?? ""
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? unrealProjectDirectory = GetUnrealProjectDirectory(game.ExePath);
        string engine = unrealProjectDirectory is null
            ? DetectEngine(installDirectory, executableDirectory)
            : "Unreal Engine";
        var candidates = new List<GameConfigCandidate>();
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Some games keep configuration files directly beside the main executable rather than
        // below a game-named subfolder. Search only the known game locations; never scan the drive.
        foreach (string gameLocation in new[] { installDirectory, executableDirectory }
                     .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            AddNamedDirectory(gameLocation, engine, candidates, visitedDirectories);
        if (engine.Equals("Unity", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string gameLocation in new[] { installDirectory, executableDirectory }
                         .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>()
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                AddUnityDataDirectoryCandidates(gameLocation, candidates, cancellationToken);
        }
        if (unrealProjectDirectory is not null)
            AddNamedDirectory(unrealProjectDirectory, engine, candidates, visitedDirectories);

        foreach (string root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string name in identity)
            {
                string namedRoot = Path.Combine(root, name);
                AddNamedDirectory(namedRoot, engine, candidates, visitedDirectories);
                if (IsPrimaryIdentityDirectory(namedRoot, primaryIdentityNames))
                    AddImmediateChildDirectories(namedRoot, engine, candidates, visitedDirectories, cancellationToken);
                if (engine.Equals("Godot", StringComparison.OrdinalIgnoreCase))
                    AddNamedDirectory(Path.Combine(root, "Godot", "app_userdata", name), engine, candidates, visitedDirectories);
            }
        }

        string company = ReadExecutableMetadata(game.ExePath, productName: false);
        string product = ReadExecutableMetadata(game.ExePath, productName: true);
        if (!string.IsNullOrWhiteSpace(company) && !string.IsNullOrWhiteSpace(product))
        {
            string low = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow");
            AddNamedDirectory(Path.Combine(low, company, product), "Unity", candidates, visitedDirectories);
            AddNamedDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), company, product), "Unity", candidates, visitedDirectories);
        }

        // Games commonly add an unstandardized publisher directory between a Windows user-data
        // root and the game-title directory (for example, LocalAppData\\publisher\\title).
        // Search only two directory levels in known user-data roots and only accept a directory
        // whose name matches metadata-derived game aliases; do not assume publisher names.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localLow = localAppData.Length > 0
            ? Path.Combine(Path.GetDirectoryName(localAppData) ?? localAppData, "LocalLow") : "";
        foreach (string root in new[]
                 {
                     localAppData,
                     roamingAppData,
                     localLow,
                     Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                     GetSavedGamesDirectory()
                 }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AddSecondLevelNamedDirectories(root, identity, primaryIdentityNames, engine, candidates,
                visitedDirectories, cancellationToken);
            if (candidates.Count >= MaxCandidates) break;
        }

        // Unreal's project name is usually the executable/product name, and its user settings follow a stable Saved/Config layout.
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), installDirectory ?? "" }
                     .Where(Directory.Exists))
        foreach (string name in identity)
        foreach (string platform in UnrealConfigFolders)
            AddConfigurationFiles(Path.Combine(root, name, "Saved", "Config", platform), candidates, "Unreal Engine");

        return candidates.Take(MaxCandidates).ToArray();
    }

    private static void AddNamedDirectory(string directory, string engine, List<GameConfigCandidate> output,
        HashSet<string> visited)
    {
        if (!Directory.Exists(directory)) return;
        AddDirectoryCandidates(directory, engine, output, visited);
        foreach (string child in new[] { "Config", "Settings", "Saved\\Config", "Saved\\Config\\Windows", "Saved\\Config\\WindowsClient",
                     "Saved\\Config\\WindowsNoEditor", "Saved\\Config\\WinGDK", "Config\\Windows", "Config\\WindowsClient" })
            AddConfigurationFiles(Path.Combine(directory, child), output, engine);
    }

    private static void AddSecondLevelNamedDirectories(string root, string[] identity,
        HashSet<string> primaryIdentityNames, string engine, List<GameConfigCandidate> output,
        HashSet<string> visited, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return;
        HashSet<string> names = identity.Select(Normalize).Where(value => value.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] parents;
        try { parents = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Take(512).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return; }

        int visitedDirectories = 0;
        foreach (string parent in parents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MatchesIdentityDirectory(Path.GetFileName(parent), names))
            {
                AddNamedDirectory(parent, engine, output, visited);
                if (IsPrimaryIdentityDirectory(parent, primaryIdentityNames))
                    AddImmediateChildDirectories(parent, engine, output, visited, cancellationToken);
            }

            string[] children;
            try { children = Directory.EnumerateDirectories(parent, "*", SearchOption.TopDirectoryOnly).Take(512).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }
            foreach (string child in children)
            {
                if (++visitedDirectories > 16_384) return;
                if (MatchesIdentityDirectory(Path.GetFileName(child), names))
                {
                    AddNamedDirectory(child, engine, output, visited);
                    if (IsPrimaryIdentityDirectory(child, primaryIdentityNames))
                        AddImmediateChildDirectories(child, engine, output, visited, cancellationToken);
                    if (output.Count >= MaxCandidates) return;
                }

                // Some publishers insert two generic folders between AppData and the
                // game-named configuration directory. Inspect one additional bounded
                // level so paths such as AppData\Publisher\Company\Game are discoverable.
                string[] grandchildren;
                try { grandchildren = Directory.EnumerateDirectories(child, "*", SearchOption.TopDirectoryOnly).Take(128).ToArray(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { continue; }
                foreach (string grandchild in grandchildren)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++visitedDirectories > 16_384) return;
                    if (!MatchesIdentityDirectory(Path.GetFileName(grandchild), names)) continue;
                    AddNamedDirectory(grandchild, engine, output, visited);
                    if (IsPrimaryIdentityDirectory(grandchild, primaryIdentityNames))
                        AddImmediateChildDirectories(grandchild, engine, output, visited, cancellationToken);
                    if (output.Count >= MaxCandidates) return;
                }
            }
        }
    }

    private static void AddImmediateChildDirectories(string directory, string engine,
        List<GameConfigCandidate> output, HashSet<string> visited, CancellationToken cancellationToken)
    {
        string[] children;
        try { children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).Take(128).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { return; }
        foreach (string child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddNamedDirectory(child, engine, output, visited);
            if (output.Count >= MaxCandidates) return;
        }
    }

    private static bool IsPrimaryIdentityDirectory(string directory, HashSet<string> primaryIdentityNames)
    {
        string name = Path.GetFileName(directory);
        return primaryIdentityNames.Contains(Normalize(name)) ||
            primaryIdentityNames.Contains(Normalize(StripGameEditionSuffix(name)));
    }

    private static bool MatchesIdentityDirectory(string directoryName, HashSet<string> identityNames)
    {
        string normalized = Normalize(directoryName);
        if (identityNames.Contains(normalized)) return true;
        string withoutEdition = Normalize(StripGameEditionSuffix(directoryName));
        return !withoutEdition.Equals(normalized, StringComparison.OrdinalIgnoreCase) && identityNames.Contains(withoutEdition);
    }

    private static void AddDirectoryCandidates(string directory, string engine, List<GameConfigCandidate> output,
        HashSet<string> visited)
    {
        if (!visited.Add(Path.GetFullPath(directory))) return;
        foreach (string file in EnumerateConfigFiles(directory))
            AddCandidate(output, file, "engine:" + engine, $"Локальный поиск · {engine}; проверьте, что это графические настройки");
    }

    private static void AddConfigurationFiles(string directory, List<GameConfigCandidate> output, string engine)
    {
        foreach (string file in EnumerateConfigFiles(directory))
            AddCandidate(output, file, "engine:" + engine, $"Типовой путь {engine}; проверьте файл перед добавлением");
    }

    private static void AddUnityDataDirectoryCandidates(string gameLocation, List<GameConfigCandidate> output,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(gameLocation)) return;
        string[] dataDirectories;
        try
        {
            dataDirectories = Directory.EnumerateDirectories(gameLocation, "*_Data", SearchOption.TopDirectoryOnly)
                .Take(128).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return;
        }

        foreach (string dataDirectory in dataDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string file in EnumerateConfigFiles(dataDirectory))
                AddCandidate(output, file, "engine:Unity", "Локально найдено в папке данных Unity (*_Data)");
            if (output.Count >= MaxCandidates) return;
        }
    }

    internal static IEnumerable<string> EnumerateConfigFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        string[] settingsFiles;
        try { settingsFiles = Directory.EnumerateFiles(directory, "*.settings", SearchOption.TopDirectoryOnly).Take(32).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { settingsFiles = []; }
        foreach (string path in settingsFiles) yield return path;

        foreach (string name in ConfigNames)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path)) yield return path;
        }
        string[] files;
        try { files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Take(128).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { files = []; }
        foreach (string path in files)
        {
            string extension = Path.GetExtension(path);
            string name = Path.GetFileName(path);
            bool namedAfterDirectory = Normalize(Path.GetFileNameWithoutExtension(path)) ==
                                       Normalize(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));
            if (GameConfigPathSafety.IsLikelyBinaryExtension(extension) ||
                (!LooksLikeSettingsFile(name) && !namedAfterDirectory)) continue;
            yield return path;
        }
    }

    private static void AddCandidate(List<GameConfigCandidate> output, string path, string definition, string description)
    {
        if (output.Count >= MaxCandidates || !File.Exists(path) ||
            !GameConfigPathSafety.ValidateCandidate(path, allowBinary: false, out _)) return;
        string full = Path.GetFullPath(path);
        if (output.Any(item => item.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) return;
        var guidance = DescribeCandidate(full, description);
        output.Add(new GameConfigCandidate(full, definition, guidance.Description, guidance.IsRecommended, guidance.Priority));
    }

    private static (string Description, bool IsRecommended, int Priority) DescribeCandidate(string path, string sourceDescription)
    {
        string name = Path.GetFileName(path);
        if (name.Equals("GameUserSettings.ini", StringComparison.OrdinalIgnoreCase))
            return ("Основной пользовательский файл Unreal Engine: начните с него для переключения экрана и игровых настроек.", true, 0);
        if (name.Equals("GraphicOptions.JSON", StringComparison.OrdinalIgnoreCase))
            return ("Основной файл графических параметров игры.", true, 1);
        if (name.Equals("WindowInfo.JSON", StringComparison.OrdinalIgnoreCase))
            return ("Параметры окна и отображения игры; проверьте файл перед добавлением.", false, 5);
        if (name.Equals("resolution.xml", StringComparison.OrdinalIgnoreCase))
            return ("Файл разрешения и режима отображения; проверьте, содержит ли он нужные графические параметры.", true, 2);
        if (name.Equals("Engine.ini", StringComparison.OrdinalIgnoreCase))
            return ("Дополнительный файл движка. Выбирайте его, если сохраняете собственные графические параметры или overrides.", false, 10);
        if (name.Equals("Input.ini", StringComparison.OrdinalIgnoreCase))
            return ("Настройки клавиатуры и управления; обычно не относятся к графике.", false, 50);
        if (name.Equals("RuntimeOptions.ini", StringComparison.OrdinalIgnoreCase))
            return ("Общие параметры запуска/движка; обычно не нужны для переключения графики.", false, 60);
        if (name.Equals("Game.ini", StringComparison.OrdinalIgnoreCase))
            return ("Общие игровые параметры; не основной файл графики.", false, 70);
        if (name.Equals("Config.ini", StringComparison.OrdinalIgnoreCase))
            return ($"Файл конфигурации игры; проверьте, содержит ли он нужные параметры графики. {sourceDescription}", false, 15);
        return ($"Другой файл настроек. {sourceDescription}", false, 100);
    }

    private static string DetectEngine(string? installDirectory, string? executableDirectory)
    {
        foreach (string root in new[] { installDirectory, executableDirectory }.Where(item => !string.IsNullOrWhiteSpace(item)).Cast<string>())
        {
            try
            {
                if (File.Exists(Path.Combine(root, "UnityPlayer.dll")) ||
                    Directory.EnumerateDirectories(root, "*_Data", SearchOption.TopDirectoryOnly).Any(path =>
                        File.Exists(Path.Combine(path, "globalgamemanagers")))) return "Unity";
                if (Directory.Exists(Path.Combine(root, "Engine", "Binaries")) ||
                    Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Take(128)
                        .Any(path => Directory.Exists(Path.Combine(path, "Binaries", "Win64")) &&
                                     Directory.Exists(Path.Combine(path, "Content", "Paks")))) return "Unreal Engine";
                foreach (string pack in Directory.EnumerateFiles(root, "*.pck", SearchOption.TopDirectoryOnly).Take(8))
                {
                    try
                    {
                        using var stream = File.OpenRead(pack);
                        byte[] signature = new byte[4];
                        if (stream.Read(signature, 0, signature.Length) == 4 && signature[0] == 'G' && signature[1] == 'D' &&
                            signature[2] == 'P' && signature[3] == 'C') return "Godot";
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        }
        return "Неизвестный движок";
    }

    /// <summary>
    /// Unreal titles commonly store an internal project directory immediately above
    /// Binaries (for example, &lt;install&gt;\ProjectName\Binaries\Win64\game.exe).
    /// Derive that slug from the selected executable instead of maintaining game names.
    /// </summary>
    private static string? GetUnrealProjectDirectory(string executablePath)
    {
        try
        {
            string? executableDirectory = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(executableDirectory)) return null;
            DirectoryInfo? current = new(executableDirectory);
            for (int depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
            {
                if (!current.Name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) || current.Parent is null)
                    continue;
                return current.Parent.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        return null;
    }

    private static string[] GetIdentity(GameEntry game, out string[] primaryIdentity)
    {
        string? installDirectory = GetInstallDirectory(game);
        string? unrealProjectDirectory = GetUnrealProjectDirectory(game.ExePath);
        var values = new[]
        {
            game.Name, Path.GetFileNameWithoutExtension(game.ExePath), ReadExecutableMetadata(game.ExePath, productName: true),
            game.EpicAppName, ReadSteamManifestName(game, GetInstallDirectory(game)),
            Path.GetFileName(installDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? ""),
            Path.GetFileName(unrealProjectDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "")
        };
        var identities = values.SelectMany(ExpandIdentityAliases).Select(SanitizeSegment)
            .Where(value => value.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        primaryIdentity = identities.ToArray();
        // Unreal project directories frequently use a short internal slug, which can be recovered
        // from the non-generic tokens in a shipping executable name.
        string executableName = Path.GetFileNameWithoutExtension(game.ExePath);
        foreach (string token in Regex.Split(executableName, @"[^\p{L}\p{N}]+"))
        {
            if (token.Length < 2 || !token.Any(char.IsLetter) ||
                token.Equals("win64", StringComparison.OrdinalIgnoreCase) || token.Equals("win32", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("shipping", StringComparison.OrdinalIgnoreCase) || token.Equals("client", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("server", StringComparison.OrdinalIgnoreCase) || token.Equals("game", StringComparison.OrdinalIgnoreCase)) continue;
            identities.Add(token);
        }
        return identities.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> ExpandIdentityAliases(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        yield return value;

        string withoutEdition = StripGameEditionSuffix(value);
        if (!withoutEdition.Equals(value, StringComparison.OrdinalIgnoreCase)) yield return withoutEdition;

        string[] titleWords = Regex.Split(withoutEdition, @"[^\p{L}\p{N}]+")
            .Where(word => word.Length > 0).ToArray();
        if (titleWords.Length >= 2)
        {
            string acronym = string.Concat(titleWords.Select(word => word[0]));
            string edition = value[withoutEdition.Length..].Trim(' ', '-', '_', ':', '–', '—');
            if (edition.Length > 0) yield return acronym + " " + edition;
            yield return acronym;
        }

        Match titleSeparator = Regex.Match(withoutEdition, @"\s*[:–—]\s*");
        if (titleSeparator.Success && titleSeparator.Index >= 5)
        {
            string mainTitle = withoutEdition[..titleSeparator.Index].Trim();
            if (mainTitle.Length >= 5 && !mainTitle.Equals(withoutEdition, StringComparison.OrdinalIgnoreCase))
                yield return mainTitle;
        }
    }

    internal static string StripGameEditionSuffix(string value) => Regex.Replace(value.Trim(),
        @"(?:\s*[-_:–—]\s*|\s+)(?:remastered|remaster|definitive(?:\s+edition)?|complete(?:\s+edition)?|enhanced(?:\s+edition)?|ultimate(?:\s+edition)?|game\s+of\s+the\s+year|goty|anniversary(?:\s+edition)?)\s*$",
        "", RegexOptions.IgnoreCase).Trim();


    internal static string ReadSteamManifestName(GameEntry game, string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(game.SteamAppId) || !game.SteamAppId.All(char.IsDigit)) return "";
        try
        {
            DirectoryInfo? directory = string.IsNullOrWhiteSpace(installDirectory) ? null : new DirectoryInfo(installDirectory);
            for (int depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
            {
                string manifest = Path.Combine(directory.FullName, $"appmanifest_{game.SteamAppId}.acf");
                if (!File.Exists(manifest)) continue;
                var info = new FileInfo(manifest);
                if (info.Length > 1024 * 1024) return "";
                string contents = File.ReadAllText(manifest);
                Match name = Regex.Match(contents, @"(?im)^\s*""name""\s+""(?<value>[^""]+)""");
                return name.Success ? name.Groups["value"].Value.Trim() : "";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return "";
    }

    private static string SanitizeSegment(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, ' ');
        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    internal static string ReadExecutableMetadata(string path, bool productName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return (productName ? info.ProductName : info.CompanyName)?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return ""; }
    }

    internal static string? GetInstallDirectory(GameEntry game)
    {
        if (!string.IsNullOrWhiteSpace(game.InstallDirectory) && Directory.Exists(game.InstallDirectory)) return game.InstallDirectory;
        string? directory = Path.GetDirectoryName(game.ExePath);
        return Directory.Exists(directory) ? directory : null;
    }

    internal static string GetSavedGamesDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");

    private static bool LooksLikeSettingsFile(string name)
    {
        string value = name.ToLowerInvariant();
        return new[] { "setting", "config", "option", "graphic", "display", "video", "render", "resolution", "engine", "user" }
            .Any(value.Contains);
    }


    internal static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
}
