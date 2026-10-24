using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using GameProfileManager.Core;
using GameProfileManager.Infrastructure;

namespace GameProfileManager.Application;

/// <summary>Versioned ZIP interchange for snapshots and their independent recovery copies.</summary>
public sealed class GameConfigArchiveService(IGameConfigSnapshotStore store)
{
    private const int CurrentVersion = 1;
    private const long MaxImportBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task ExportAsync(AppState state, string archivePath, CancellationToken cancellationToken = default)
    {
        var manifest = new ArchiveManifest
        {
            Version = CurrentVersion,
            Settings = state.Games.Select(game => new ArchiveGameSettings
            {
                GameId = game.Id,
                Settings = JsonSerializer.Deserialize<GameConfigSettings>(JsonSerializer.SerializeToUtf8Bytes(game.GameConfigSettings, JsonOptions), JsonOptions)
                    ?? new GameConfigSettings()
            }).ToList(),
            Snapshots = state.GameConfigSnapshots,
            Assignments = state.GameConfigAssignments,
            OriginalBackups = state.GameConfigOriginalBackups,
            SafetyCopies = state.GameConfigSafetyCopies
        };
        var payload = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Snapshots.SelectMany(snapshot => snapshot.Items))
            Add(item.ContentPath, item.Sha256);
        foreach (var item in manifest.OriginalBackups.Where(item => !string.IsNullOrWhiteSpace(item.ContentPath)))
            Add(item.ContentPath, item.Sha256);
        foreach (var item in manifest.SafetyCopies.Where(item => !string.IsNullOrWhiteSpace(item.ContentPath)))
            Add(item.ContentPath, item.Sha256);

        string fullArchive = Path.GetFullPath(archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullArchive)!);
        string temporary = fullArchive + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using (var stream = manifestEntry.Open())
                    await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken);
                foreach (var entry in payload)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var zipEntry = zip.CreateEntry("payload/" + entry.Key, CompressionLevel.Optimal);
                    await using var target = zipEntry.Open();
                    await target.WriteAsync(entry.Value, cancellationToken);
                }
            }
            File.Move(temporary, fullArchive, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }

        void Add(string relative, string expectedHash)
        {
            ValidateContentPath(relative);
            if (!payload.TryGetValue(relative, out byte[]? bytes))
            {
                bytes = store.ReadBlob(relative);
                if (!Hash(bytes).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Защитная копия или снимок не прошли проверку SHA-256: " + relative);
                payload.Add(relative, bytes);
            }
            else if (!Hash(bytes).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Для одного пути сохранены разные контрольные суммы.");
        }
    }

    public async Task ImportAsync(AppState state, string archivePath, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        if (zip.Entries.Count > 20000) throw new InvalidDataException("В архиве слишком много элементов.");
        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expandedSize = 0;
        foreach (var entry in zip.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part == "..") ||
                normalized.Contains(':') || !byName.TryAdd(normalized, entry))
                throw new InvalidDataException("В архиве обнаружен повторный или небезопасный путь.");
            if (normalized == "manifest.json")
            {
                if (entry.Length > 64 * 1024 * 1024) throw new InvalidDataException("Манифест архива слишком велик.");
            }
            else
            {
                if (!normalized.StartsWith("payload/game-configs/", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Архив содержит элемент вне payload/game-configs.");
                if (entry.Length > GameConfigPathSafety.MaxFileBytes) throw new InvalidDataException("Элемент архива превышает 32 МБ.");
                expandedSize += entry.Length;
                if (expandedSize > MaxImportBytes) throw new InvalidDataException("Распакованный архив превышает 512 МБ.");
            }
        }
        if (!byName.TryGetValue("manifest.json", out var manifestEntry)) throw new InvalidDataException("В архиве нет manifest.json.");
        ArchiveManifest manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Манифест архива пуст.");
        if (manifest.Version != CurrentVersion) throw new InvalidDataException("Версия архива не поддерживается.");
        if (manifest.Snapshots is null || manifest.Settings is null || manifest.Assignments is null ||
            manifest.OriginalBackups is null || manifest.SafetyCopies is null ||
            manifest.Snapshots.Count > 10000 || manifest.Settings.Count > 10000)
            throw new InvalidDataException("В архиве слишком много игровых записей.");
        if (manifest.Settings.Select(entry => entry.GameId).Distinct().Count() != manifest.Settings.Count ||
            manifest.Snapshots.Select(snapshot => snapshot.Id).Distinct().Count() != manifest.Snapshots.Count)
            throw new InvalidDataException("В архиве повторяются ID игр или снимков.");
        foreach (var entry in manifest.Settings)
            if (state.Games.All(game => game.Id != entry.GameId))
                throw new InvalidOperationException("Сначала импортируйте библиотеку игр с теми же ID, затем импортируйте графические конфигурации.");

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var expectedSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Snapshots.SelectMany(snapshot => snapshot.Items))
        {
            if (item.Size is < 0 or > GameConfigPathSafety.MaxFileBytes) throw new InvalidDataException("В манифесте указан недопустимый размер снимка.");
            Register(item.ContentPath, item.Sha256, item.Size);
        }
        foreach (var item in manifest.OriginalBackups.Where(item => !string.IsNullOrWhiteSpace(item.ContentPath))) Register(item.ContentPath, item.Sha256);
        foreach (var item in manifest.SafetyCopies.Where(item => !string.IsNullOrWhiteSpace(item.ContentPath))) Register(item.ContentPath, item.Sha256);

        var content = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateContentPath(entry.Key);
            string name = "payload/" + entry.Key;
            if (!byName.TryGetValue(name, out var zipEntry)) throw new InvalidDataException("В архиве отсутствует защищённый файл: " + entry.Key);
            byte[] bytes = await ReadBoundedEntryAsync(zipEntry, cancellationToken);
            if (expectedSizes.TryGetValue(entry.Key, out long expectedSize) && bytes.LongLength != expectedSize)
                throw new InvalidDataException("Размер снимка в архиве не совпадает с манифестом: " + entry.Key);
            if (Hash(bytes).Equals(entry.Value, StringComparison.OrdinalIgnoreCase) is false)
                throw new InvalidDataException("В архиве не совпала контрольная сумма: " + entry.Key);
            content.Add(entry.Key, bytes);
        }

        var importedSettings = manifest.Settings.ToDictionary(entry => entry.GameId, entry => entry.Settings);
        var mergedSettings = state.Games.ToDictionary(game => game.Id, game => game.GameConfigSettings);
        foreach (var entry in manifest.Settings)
        {
            if (entry.Settings.Sources.Count > GameConfigPathSafety.MaxSourceCount ||
                entry.Settings.Sources.Select(source => source.Id).Distinct().Count() != entry.Settings.Sources.Count ||
                entry.Settings.Sources.Any(source => source.GameId != entry.GameId || !source.UserConfirmed))
                throw new InvalidDataException("В архиве повреждён список графических источников.");
            foreach (var source in entry.Settings.Sources)
            {
                if (source.Kind == GameConfigSourceKind.File)
                {
                    if (!GameConfigPathSafety.ValidateCandidate(source.TargetPath, allowBinary: true, out string reason))
                        throw new InvalidDataException("Импорт содержит небезопасный файл: " + reason);
                    if (!GameConfigPathSafety.IsWithin(source.TargetPath, source.ApprovedRootPath))
                        throw new InvalidDataException("Импортированный путь выходит за подтверждённую область.");
                }
                else if (!RegistryGameConfigBackend.IsAllowed(source, out string reason))
                    throw new InvalidDataException("Импорт содержит недопустимое значение реестра: " + reason);
            }
            mergedSettings[entry.GameId] = entry.Settings;
        }
        var owners = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in mergedSettings)
            foreach (var source in entry.Value.Sources)
            {
                string target = source.Kind == GameConfigSourceKind.File
                    ? Path.GetFullPath(source.TargetPath) : source.TargetPath + "|" + source.RegistryValueName;
                if (owners.TryGetValue(source.Kind + "|" + target, out Guid owner) && owner != entry.Key)
                    throw new InvalidDataException("Импорт создаёт конфликт: один целевой источник назначен двум играм.");
                owners[source.Kind + "|" + target] = entry.Key;
            }

        var importedSnapshots = manifest.Snapshots.ToDictionary(snapshot => snapshot.Id);
        foreach (var snapshot in manifest.Snapshots)
        {
            if (snapshot.Items is null || !importedSettings.TryGetValue(snapshot.GameId, out var settings) ||
                snapshot.Items.Count is < 1 or > GameConfigPathSafety.MaxSourceCount ||
                snapshot.Items.Count != settings.Sources.Count ||
                snapshot.Items.Select(item => item.SourceId).Distinct().Count() != snapshot.Items.Count ||
                snapshot.Items.Any(item => settings.Sources.All(source => source.Id != item.SourceId)))
                throw new InvalidDataException("Снимок не совпадает с каталогом источников архива.");
            foreach (var item in snapshot.Items)
            {
                string expectedPath = $"game-configs/{snapshot.GameId:N}/{snapshot.Id:N}/content/{item.SourceId:N}.bin";
                if (!item.ContentPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Содержимое снимка находится вне ожидаемого каталога.");
            }
            var existing = state.GameConfigSnapshots.FirstOrDefault(item => item.Id == snapshot.Id);
            if (existing is not null && !Equivalent(existing, snapshot))
                throw new InvalidDataException("ID снимка уже существует с другим содержимым.");
        }
        if (manifest.Assignments.Any(item => item.GameId == Guid.Empty || string.IsNullOrWhiteSpace(item.DisplayId)) ||
            manifest.Assignments.GroupBy(item => (item.GameId, item.DisplayId), new GameDisplayAssignmentComparer()).Any(group => group.Count() > 1))
            throw new InvalidDataException("В архиве повторяются или повреждены назначения дисплеев.");
        foreach (var item in manifest.Assignments)
        {
            var assigned = importedSnapshots.GetValueOrDefault(item.SnapshotId) ??
                state.GameConfigSnapshots.FirstOrDefault(snapshot => snapshot.Id == item.SnapshotId);
            if (!state.Games.Any(game => game.Id == item.GameId) || assigned is null || assigned.GameId != item.GameId)
                throw new InvalidDataException("Назначение архива ссылается на отсутствующий снимок.");
        }
        foreach (var item in manifest.OriginalBackups)
        {
            if (!importedSettings.TryGetValue(item.GameId, out var settings) || settings.Sources.All(source => source.Id != item.SourceId))
                throw new InvalidDataException("Исходная копия архива не связана с выбранным источником.");
            if (item.Existed && !item.ContentPath.StartsWith($"game-configs/{item.GameId:N}/originals/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Исходная копия расположена вне ожидаемого каталога.");
            var source = settings.Sources.First(source => source.Id == item.SourceId);
            string originalTarget = string.IsNullOrWhiteSpace(item.TargetPath) ? source.TargetPath : item.TargetPath;
            string originalValueName = string.IsNullOrWhiteSpace(item.RegistryValueName) ? source.RegistryValueName : item.RegistryValueName;
            if (source.Kind == GameConfigSourceKind.File &&
                !GameConfigPathSafety.ValidateCandidate(originalTarget, allowBinary: true, out _))
                throw new InvalidDataException("Исходная копия связана с небезопасным путём.");
            if (source.Kind == GameConfigSourceKind.Registry)
            {
                if (!RegistryGameConfigBackend.IsAllowed(new GameConfigSource { Kind = source.Kind, TargetPath = originalTarget,
                        RegistryValueName = originalValueName }, out _))
                    throw new InvalidDataException("Исходная копия связана с недопустимым значением реестра.");
            }
        }
        foreach (var item in manifest.SafetyCopies)
        {
            if (!importedSettings.TryGetValue(item.GameId, out var settings) || settings.Sources.All(source => source.Id != item.SourceId) ||
                !item.ContentPath.StartsWith($"game-configs/{item.GameId:N}/safety/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Защитная копия архива не связана с выбранным источником.");
        }
        if (byName.Keys.Any(name => name != "manifest.json" && !expected.ContainsKey(name["payload/".Length..])))
            throw new InvalidDataException("Архив содержит неописанные файлы.");

        // Validate every payload before writing any file into the GPM data directory.
        foreach (var item in content) store.WriteImmutable(item.Key, item.Value);
        foreach (var snapshot in manifest.Snapshots)
        {
            var existing = state.GameConfigSnapshots.FirstOrDefault(item => item.Id == snapshot.Id);
            if (existing is not null)
            {
                continue;
            }
            state.GameConfigSnapshots.Add(snapshot);
        }
        foreach (var item in manifest.Assignments)
        {
            state.GameConfigAssignments.RemoveAll(existing => existing.GameId == item.GameId &&
                existing.DisplayId.Equals(item.DisplayId, StringComparison.OrdinalIgnoreCase));
            state.GameConfigAssignments.Add(item);
        }
        foreach (var backup in manifest.OriginalBackups)
        {
            var source = importedSettings[backup.GameId].Sources.Single(item => item.Id == backup.SourceId);
            bool exists = state.GameConfigOriginalBackups.Any(item => item.GameId == backup.GameId && item.SourceId == backup.SourceId &&
                (string.IsNullOrWhiteSpace(item.TargetPath) ? source.TargetPath : item.TargetPath).Equals(
                    string.IsNullOrWhiteSpace(backup.TargetPath) ? source.TargetPath : backup.TargetPath,
                    StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(item.RegistryValueName) ? source.RegistryValueName : item.RegistryValueName).Equals(
                    string.IsNullOrWhiteSpace(backup.RegistryValueName) ? source.RegistryValueName : backup.RegistryValueName,
                    StringComparison.OrdinalIgnoreCase));
            if (!exists) state.GameConfigOriginalBackups.Add(backup);
        }
        foreach (var safety in manifest.SafetyCopies)
            if (state.GameConfigSafetyCopies.All(item => item.Id != safety.Id)) state.GameConfigSafetyCopies.Add(safety);
        foreach (var entry in manifest.Settings)
        {
            var game = state.Games.Single(item => item.Id == entry.GameId);
            entry.Settings.AutoApply = false;
            entry.Settings.Status = entry.Settings.Sources.Count == 0 ? GameConfigApplyStatus.NotConfigured : GameConfigApplyStatus.Ready;
            entry.Settings.StatusDetail = "Графические данные импортированы. Проверьте пути и включите автоматическое применение вручную.";
            game.GameConfigSettings = entry.Settings;
        }

        void Register(string path, string hash, long? expectedSize = null)
        {
            ValidateContentPath(path);
            if (expected.TryGetValue(path, out string? previous) && !previous.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Путь архива ссылается на разные контрольные суммы.");
            expected[path] = hash;
            if (expectedSize is { } size)
            {
                if (expectedSizes.TryGetValue(path, out long previousSize) && previousSize != size)
                    throw new InvalidDataException("Путь снимка имеет разные размеры в манифесте.");
                expectedSizes[path] = size;
            }
        }
    }

    private static async Task<byte[]> ReadBoundedEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var source = entry.Open();
        using var output = new MemoryStream(checked((int)entry.Length));
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > entry.Length || total > GameConfigPathSafety.MaxFileBytes)
                throw new InvalidDataException("Элемент архива распаковывается сверх указанного размера.");
            output.Write(buffer, 0, read);
        }
        if (total != entry.Length) throw new InvalidDataException("Размер распакованного элемента не совпадает с ZIP-заголовком.");
        return output.ToArray();
    }

    private static bool SameTarget(GameConfigSource a, GameConfigSource b) => a.Kind == b.Kind &&
        string.Equals(a.Kind == GameConfigSourceKind.File ? Path.GetFullPath(a.TargetPath) : a.TargetPath + "|" + a.RegistryValueName,
            b.Kind == GameConfigSourceKind.File ? Path.GetFullPath(b.TargetPath) : b.TargetPath + "|" + b.RegistryValueName,
            StringComparison.OrdinalIgnoreCase);

    private sealed class GameDisplayAssignmentComparer : IEqualityComparer<(Guid GameId, string DisplayId)>
    {
        public bool Equals((Guid GameId, string DisplayId) x, (Guid GameId, string DisplayId) y) =>
            x.GameId == y.GameId && x.DisplayId.Equals(y.DisplayId, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((Guid GameId, string DisplayId) value) =>
            HashCode.Combine(value.GameId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.DisplayId));
    }

    private static bool Equivalent(GameConfigSnapshot left, GameConfigSnapshot right) => left.GameId == right.GameId &&
        left.Items.Count == right.Items.Count && left.Items.All(a => right.Items.Any(b => b.SourceId == a.SourceId &&
            b.Sha256.Equals(a.Sha256, StringComparison.OrdinalIgnoreCase) && b.Size == a.Size));

    private static void ValidateContentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(part => part is "" or "." or "..") || !path.StartsWith("game-configs/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("В манифесте указан небезопасный путь содержимого.");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public sealed class ArchiveManifest
    {
        public int Version { get; set; }
        public List<ArchiveGameSettings> Settings { get; set; } = [];
        public List<GameConfigSnapshot> Snapshots { get; set; } = [];
        public List<GameConfigAssignment> Assignments { get; set; } = [];
        public List<GameConfigOriginalBackup> OriginalBackups { get; set; } = [];
        public List<GameConfigSafetyCopy> SafetyCopies { get; set; } = [];
    }

    public sealed class ArchiveGameSettings
    {
        public Guid GameId { get; set; }
        public GameConfigSettings Settings { get; set; } = new();
    }
}
