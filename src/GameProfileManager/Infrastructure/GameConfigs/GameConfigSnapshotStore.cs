using System.Security.Cryptography;
using GameProfileManager.Application;
using GameProfileManager.Core;

namespace GameProfileManager.Infrastructure;

public interface IGameConfigSnapshotStore
{
    string RootPath { get; }
    string TransactionPath { get; }
    Task WriteSnapshotAsync(GameConfigSnapshot snapshot, IReadOnlyDictionary<Guid, byte[]> content, CancellationToken cancellationToken = default);
    byte[] ReadSnapshotItem(GameConfigSnapshot snapshot, GameConfigSnapshotItem item);
    string WriteImmutable(string relativePath, byte[] content);
    byte[] ReadBlob(string relativePath);
    string GetFullPath(string relativePath);
    void DeleteSnapshot(Guid gameId, Guid snapshotId);
    IReadOnlyList<string> FindIncompleteTransactions();
}

public sealed class GameConfigSnapshotStore(string? dataDirectory = null) : IGameConfigSnapshotStore
{
    private readonly string _dataDirectory = dataDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameProfileManager");
    public string RootPath => Path.Combine(_dataDirectory, "game-configs");
    public string TransactionPath => Path.Combine(_dataDirectory, "transactions");

    public async Task WriteSnapshotAsync(GameConfigSnapshot snapshot, IReadOnlyDictionary<Guid, byte[]> content,
        CancellationToken cancellationToken = default)
    {
        string parent = Path.Combine(RootPath, snapshot.GameId.ToString("N"));
        string staging = Path.Combine(parent, ".staging-" + snapshot.Id.ToString("N"));
        string destination = Path.Combine(parent, snapshot.Id.ToString("N"));
        Directory.CreateDirectory(parent);
        if (Directory.Exists(destination)) throw new IOException("Такой ID снимка уже существует.");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(Path.Combine(staging, "content"));
        try
        {
            foreach (var item in snapshot.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.ContentPath = "game-configs/" + snapshot.GameId.ToString("N") + "/" + snapshot.Id.ToString("N") + "/content/" + item.SourceId.ToString("N") + ".bin";
                await File.WriteAllBytesAsync(Path.Combine(staging, "content", item.SourceId.ToString("N") + ".bin"), content[item.SourceId], cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public byte[] ReadSnapshotItem(GameConfigSnapshot snapshot, GameConfigSnapshotItem item)
    {
        string path = GetFullPath(item.ContentPath);
        string expectedRoot = Path.GetFullPath(Path.Combine(RootPath, snapshot.GameId.ToString("N"), snapshot.Id.ToString("N"))) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь содержимого снимка выходит за его каталог.");
        return ReadBlob(item.ContentPath);
    }

    public string WriteImmutable(string relativePath, byte[] content)
    {
        if (content.Length > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes)
            throw new InvalidDataException("Содержимое защитной копии превышает ограничение 32 МБ.");
        string path = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes)
                throw new InvalidDataException("Существующая защитная копия превышает ограничение 32 МБ.");
            string old = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            string next = Convert.ToHexString(SHA256.HashData(content));
            if (!old.Equals(next, StringComparison.OrdinalIgnoreCase)) throw new IOException("Защитная копия с таким ID уже существует с другим содержимым.");
            return relativePath;
        }
        WriteDurable(path, content);
        return relativePath;
    }

    public byte[] ReadBlob(string relativePath)
    {
        string path = GetFullPath(relativePath);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Содержимое графической копии не найдено.", path);
        if (info.Length > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes)
            throw new InvalidDataException("Содержимое графической копии превышает ограничение 32 МБ.");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes)
            throw new InvalidDataException("Содержимое графической копии изменилось и превышает ограничение 32 МБ.");
        return bytes;
    }

    public string GetFullPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("Путь хранилища должен быть относительным и безопасным.");
        string path = Path.GetFullPath(Path.Combine(_dataDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.GetFullPath(_dataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь выходит за каталог GPM.");
        return path;
    }

    public void DeleteSnapshot(Guid gameId, Guid snapshotId)
    {
        string path = Path.Combine(RootPath, gameId.ToString("N"), snapshotId.ToString("N"));
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public IReadOnlyList<string> FindIncompleteTransactions()
    {
        if (!Directory.Exists(TransactionPath)) return [];
        var result = new List<string>();
        foreach (string journal in Directory.EnumerateFiles(TransactionPath, "journal.json", SearchOption.AllDirectories))
        {
            try
            {
                var record = System.Text.Json.JsonSerializer.Deserialize<GameConfigTransactionRecord>(File.ReadAllBytes(journal));
                if (record is { Completed: false, RecoveryCompleted: false }) result.Add(journal);
            }
            catch { result.Add(journal); }
        }
        return result;
    }

    public IReadOnlyList<string> FindOrphanSnapshots(AppState state)
    {
        if (!Directory.Exists(RootPath)) return [];
        var known = state.GameConfigSnapshots.Select(item => Path.Combine(RootPath, item.GameId.ToString("N"), item.Id.ToString("N")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateDirectories(RootPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).StartsWith(".staging-", StringComparison.OrdinalIgnoreCase) ||
                Guid.TryParseExact(Path.GetFileName(path), "N", out _) && !known.Contains(path))
            .ToArray();
    }

    internal static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

public sealed class GameConfigSnapshotService(IGameConfigSnapshotStore store, GameProcessGuard processGuard)
{
    public async Task<GameConfigSnapshot> CaptureAsync(GameEntry game, string name, IReadOnlyList<GameConfigSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (sources.Count is < 1 or > GameProfileManager.Application.GameConfigPathSafety.MaxSourceCount)
            throw new InvalidOperationException($"Выберите от 1 до {GameProfileManager.Application.GameConfigPathSafety.MaxSourceCount} файлов или значений.");
        EnsureGameClosed(game);
        var content = new Dictionary<Guid, byte[]>();
        var items = new List<GameConfigSnapshotItem>();
        long total = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureGameClosed(game);
            if (!source.UserConfirmed || source.GameId != game.Id) throw new InvalidOperationException("Каждый источник должен быть подтверждён для этой игры.");
            byte[] bytes;
            bool exists;
            FileAttributes? attributes = null;
            string kind = "";
            if (source.Kind == GameConfigSourceKind.File)
            {
                if (!GameProfileManager.Application.GameConfigPathSafety.ValidateCandidate(source.TargetPath, source.BinaryExplicitlyAllowed, out string reason))
                    throw new InvalidOperationException(reason);
                exists = File.Exists(source.TargetPath);
                if (exists)
                {
                    var info = new FileInfo(source.TargetPath);
                    if (!GameProfileManager.Application.GameConfigPathSafety.IsWithin(source.TargetPath, source.ApprovedRootPath))
                        throw new InvalidOperationException("Файл вышел за подтверждённую папку.");
                    if (info.Length > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes)
                        throw new InvalidOperationException("Файл превышает ограничение 32 МБ.");
                    attributes = File.GetAttributes(source.TargetPath);
                    DateTime lastWrite = File.GetLastWriteTimeUtc(source.TargetPath);
                    bytes = await ReadFileBoundedAsync(source.TargetPath, cancellationToken);
                    var after = new FileInfo(source.TargetPath);
                    if (!after.Exists || after.Length != bytes.LongLength || after.Length > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes ||
                        after.LastWriteTimeUtc != lastWrite)
                        throw new IOException("Файл изменился во время чтения; снимок не создан.");
                }
                else bytes = [];
            }
            else
            {
                if (!RegistryGameConfigBackend.IsAllowed(source, out string reason)) throw new InvalidOperationException(reason);
                try { bytes = RegistryGameConfigBackend.Read(source); exists = System.Text.Json.JsonDocument.Parse(bytes).RootElement.GetProperty("Exists").GetBoolean(); }
                catch (System.Security.SecurityException ex) { throw new InvalidOperationException("Нет доступа к выбранному значению HKCU.", ex); }
                kind = RegistryGameConfigBackend.Kind(bytes);
            }
            total += bytes.Length;
            if (total > GameProfileManager.Application.GameConfigPathSafety.MaxSnapshotBytes)
                throw new InvalidOperationException("Общий размер снимка превышает 128 МБ.");
            content.Add(source.Id, bytes);
            items.Add(new GameConfigSnapshotItem
            {
                SourceId = source.Id, Size = bytes.LongLength, Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                SourceExisted = exists, OriginalAttributes = attributes, RegistryKind = kind
            });
        }
        EnsureGameClosed(game);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureGameClosed(game);
            byte[] verification;
            if (source.Kind == GameConfigSourceKind.File)
            {
                if (!GameProfileManager.Application.GameConfigPathSafety.ValidateCandidate(source.TargetPath, source.BinaryExplicitlyAllowed, out string reason))
                    throw new InvalidOperationException("Источник стал небезопасным во время чтения: " + reason);
                if (!GameProfileManager.Application.GameConfigPathSafety.IsWithin(source.TargetPath, source.ApprovedRootPath))
                    throw new InvalidOperationException("Источник вышел за подтверждённую папку во время чтения.");
                if (File.Exists(source.TargetPath))
                {
                    var info = new FileInfo(source.TargetPath);
                    if (info.Length > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes)
                        throw new InvalidOperationException("Файл вырос за предел 32 МБ во время чтения.");
                    verification = await ReadFileBoundedAsync(source.TargetPath, cancellationToken);
                }
                else verification = [];
            }
            else verification = RegistryGameConfigBackend.Read(source);
            if (!content.TryGetValue(source.Id, out byte[]? captured) ||
                !System.Security.Cryptography.SHA256.HashData(verification).AsSpan().SequenceEqual(
                    System.Security.Cryptography.SHA256.HashData(captured)))
                throw new IOException("Источник изменился при сохранении снимка; снимок не создан.");
        }
        var snapshot = new GameConfigSnapshot { GameId = game.Id, Name = name.Trim(), Items = items };
        await store.WriteSnapshotAsync(snapshot, content, cancellationToken);
        return snapshot;
    }

    private void EnsureGameClosed(GameEntry game)
    {
        var check = processGuard.Check(game);
        if (check.State != GameProcessState.Closed)
            throw new GameConfigDeferredException(check.Detail + " Сохраните снимок после закрытия игры.", check.State);
    }

    private static async Task<byte[]> ReadFileBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > GameProfileManager.Application.GameConfigPathSafety.MaxFileBytes)
                throw new InvalidOperationException("Файл превышает ограничение 32 МБ.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
