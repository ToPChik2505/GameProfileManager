using System.Security.Cryptography;
using System.Text.Json;
using GameProfileManager.Core;
using GameProfileManager.Infrastructure;

namespace GameProfileManager.Application;

public sealed class GameConfigTransactionManager(IGameConfigSnapshotStore store, GameProcessGuard processGuard)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _gameGate = new(1, 1);

    public async Task ApplyAsync(AppState state, GameEntry game, GameConfigSnapshot snapshot, bool overrideConflict,
        CancellationToken cancellationToken = default)
    {
        await _gameGate.WaitAsync(cancellationToken);
        try { await Task.Run(() => ApplyCore(state, game, snapshot, overrideConflict, cancellationToken), cancellationToken); }
        finally { _gameGate.Release(); }
    }

    public async Task RestoreOriginalsAsync(AppState state, GameEntry game, bool overrideConflict = false,
        CancellationToken cancellationToken = default)
    {
        await _gameGate.WaitAsync(cancellationToken);
        try { await Task.Run(() => RestoreOriginalsCore(state, game, overrideConflict, cancellationToken), cancellationToken); }
        finally { _gameGate.Release(); }
    }

    public bool RecoverIncomplete(AppState state)
    {
        bool changed = false;
        var journals = store.FindIncompleteTransactions();
        foreach (string journal in journals)
        {
            try
            {
                var record = JsonSerializer.Deserialize<GameConfigTransactionRecord>(File.ReadAllBytes(journal), JsonOptions);
                if (record is null) continue;
                var game = state.Games.FirstOrDefault(item => item.Id == record.GameId);
                if (game is null) continue;
                if (game.GameConfigSettings.Status == GameConfigApplyStatus.RecoveryRequired) continue;
                game.GameConfigSettings.Status = GameConfigApplyStatus.RecoveryRequired;
                game.GameConfigSettings.StatusDetail = "Найдена незавершённая файловая транзакция. Восстановите её перед новым применением.";
                AddEvent(game.GameConfigSettings, GameConfigApplyStatus.RecoveryRequired, game.GameConfigSettings.StatusDetail);
                changed = true;
            }
            catch { }
        }
        return ReconcileCompleted(state) || changed;
    }

    public IReadOnlyList<string> IncompleteTransactions() => store.FindIncompleteTransactions();

    public IReadOnlyList<Guid> FindCurrentSavedSnapshotIds(AppState state, GameEntry game)
    {
        var sources = game.GameConfigSettings.Sources.ToArray();
        if (sources.Length == 0) return [];

        EnsureNoCrossGameOwnership(state, game);
        var current = new Dictionary<Guid, CurrentSourceState>();
        foreach (var source in sources)
        {
            if (!source.UserConfirmed || source.GameId != game.Id)
                throw new InvalidOperationException("Источник не подтверждён для этой игры.");
            if (source.Kind == GameConfigSourceKind.File)
            {
                if (!GameConfigPathSafety.ValidateCandidate(source.TargetPath, source.BinaryExplicitlyAllowed, out string reason))
                    throw new InvalidOperationException(reason);
                if (!GameConfigPathSafety.IsWithin(source.TargetPath, source.ApprovedRootPath))
                    throw new InvalidOperationException("Файл вышел за подтверждённую папку.");
            }
            else if (!RegistryGameConfigBackend.IsAllowed(source, out string reason))
                throw new InvalidOperationException(reason);

            var value = ReadCurrent(source);
            current.Add(source.Id, new CurrentSourceState(value.Exists, value.Bytes, Hash(value.Bytes)));
        }

        return FindMatchingSavedSnapshotIds(state, game.Id, current);
    }

    public bool CurrentStateMatchesSavedSnapshot(AppState state, GameEntry game)
    {
        try
        {
            return FindCurrentSavedSnapshotIds(state, game).Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task RecoverAsync(AppState state, string journalPath, CancellationToken cancellationToken = default)
    {
        await _gameGate.WaitAsync(cancellationToken);
        try { await Task.Run(() => RecoverCore(state, journalPath, cancellationToken), cancellationToken); }
        finally { _gameGate.Release(); }
    }

    private void ApplyCore(AppState state, GameEntry game, GameConfigSnapshot snapshot, bool overrideConflict,
        CancellationToken cancellationToken)
    {
        var settings = game.GameConfigSettings;
        if (store.FindIncompleteTransactions().Count > 0)
            throw new InvalidOperationException("Сначала восстановите незавершённую транзакцию.");
        if (snapshot.GameId != game.Id || !state.GameConfigSnapshots.Any(item => item.Id == snapshot.Id && item.GameId == game.Id))
            throw new InvalidOperationException("Снимок не принадлежит выбранной игре или отсутствует в библиотеке.");
        if (snapshot.Items.Count is < 1 or > GameConfigPathSafety.MaxSourceCount || settings.Sources.Count != snapshot.Items.Count)
            throw new InvalidOperationException("Состав источников изменился. Создайте новый снимок перед применением.");
        EnsureNoCrossGameOwnership(state, game);
        var sources = settings.Sources.ToDictionary(item => item.Id);
        var plans = new List<PlannedItem>();
        long total = 0;
        foreach (var snapshotItem in snapshot.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(snapshotItem.SourceId, out var source) || !source.UserConfirmed || source.GameId != game.Id)
                throw new InvalidOperationException("Снимок содержит неподтверждённый или изменённый источник.");
            byte[] desired = store.ReadSnapshotItem(snapshot, snapshotItem);
            string desiredHash = Hash(desired);
            if (!desiredHash.Equals(snapshotItem.Sha256, StringComparison.OrdinalIgnoreCase) || desired.Length != snapshotItem.Size)
                throw new InvalidDataException("Контрольная сумма снимка не совпала. Применение остановлено.");
            total += desired.Length;
            if (total > GameConfigPathSafety.MaxSnapshotBytes) throw new InvalidDataException("Размер снимка превышает безопасный предел.");
            if (source.Kind == GameConfigSourceKind.File)
            {
                if (!GameConfigPathSafety.ValidateCandidate(source.TargetPath, source.BinaryExplicitlyAllowed, out string reason))
                    throw new InvalidOperationException(reason);
                if (!GameConfigPathSafety.IsWithin(source.TargetPath, source.ApprovedRootPath))
                    throw new InvalidOperationException("Целевой файл вышел за подтверждённую папку.");
            }
            else if (!RegistryGameConfigBackend.IsAllowed(source, out string reason)) throw new InvalidOperationException(reason);
            var current = ReadCurrent(source);
            string currentHash = Hash(current.Bytes);
            bool currentExists = current.Exists;
            bool noChange = source.Kind == GameConfigSourceKind.File
                ? snapshotItem.SourceExisted == currentExists && desiredHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase)
                : desiredHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase);
            plans.Add(new PlannedItem(source, snapshotItem, desired, current.Bytes, currentExists, currentHash, current.Attributes, noChange));
        }

        bool matchesSavedSnapshot = MatchesSavedSnapshotState(state, game.Id, plans.ToDictionary(item => item.Source.Id,
            item => new CurrentSourceState(item.CurrentExists, item.CurrentBytes, item.CurrentHash)));
        foreach (var item in plans)
        {
            bool conflict = settings.LastAppliedHashes.TryGetValue(item.Source.Id, out string? expected) &&
                (!item.CurrentHash.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                 settings.LastAppliedExists.TryGetValue(item.Source.Id, out bool expectedExists) && item.CurrentExists != expectedExists);
            if (!conflict || matchesSavedSnapshot) continue;

            string copyPath = $"game-configs/{game.Id:N}/safety/{Guid.NewGuid():N}.bin";
            store.WriteImmutable(copyPath, item.CurrentBytes);
            state.GameConfigSafetyCopies.Add(new GameConfigSafetyCopy
            {
                GameId = game.Id, SourceId = item.Source.Id, ContentPath = copyPath, Sha256 = item.CurrentHash,
                Existed = item.CurrentExists, Reason = "Конфликт: рабочее содержимое изменилось после последнего применения"
            });
            if (!overrideConflict)
                throw new GameConfigConflictException($"Файл изменился вне GPM после последнего применения: {item.Source.TargetPath}. Автоматическое переключение остановлено; защитная копия создана. Текущие настройки не совпадают ни с одним сохранённым снимком. Если это нужные настройки, сохраните их новым снимком; иначе явно примените снимок или восстановите исходные файлы.");
        }

        var active = plans.Where(item => !item.NoChange).ToArray();
        if (active.Length == 0)
        {
            settings.LastAppliedSnapshotId = snapshot.Id;
            settings.Status = GameConfigApplyStatus.Applied;
            settings.StatusDetail = "Выбранный снимок уже соответствует рабочим файлам.";
            settings.LastAppliedAt = DateTimeOffset.Now;
            settings.LastAppliedHashes = snapshot.Items.ToDictionary(item => item.SourceId, item => item.Sha256);
            settings.LastAppliedExists = snapshot.Items.ToDictionary(item => item.SourceId, item => item.SourceExisted);
            AddEvent(settings, settings.Status, settings.StatusDetail);
            return;
        }
        CheckGameClosed(game);
        var tx = NewTransaction(game.Id, snapshot.Id, active);
        tx.AppliedHashes = snapshot.Items.ToDictionary(item => item.SourceId, item => item.Sha256);
        tx.AppliedExists = snapshot.Items.ToDictionary(item => item.SourceId, item => item.SourceExisted);
        string txDirectory = Path.Combine(store.TransactionPath, tx.Id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(txDirectory, "backups"));
        Directory.CreateDirectory(Path.Combine(txDirectory, "temporary"));
        string journalPath = Path.Combine(txDirectory, "journal.json");
        tx.Status = "Preparing";
        WriteJournal(journalPath, tx);
        foreach (var item in active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string backupRelative = $"transactions/{tx.Id:N}/backups/{item.Source.Id:N}.bin";
            GameConfigSnapshotStore.WriteDurable(store.GetFullPath(backupRelative), item.CurrentBytes);
            var entry = tx.Items.Single(candidate => candidate.SourceId == item.Source.Id);
            entry.PreviousBlob = backupRelative;
            entry.PreviousHash = item.CurrentHash;
            entry.PreviousAttributes = item.CurrentAttributes;
            WriteJournal(journalPath, tx);
            if (item.Source.Kind == GameConfigSourceKind.File && item.SnapshotItem.SourceExisted)
            {
                string targetDirectory = Path.GetDirectoryName(item.Source.TargetPath)!;
                string temporaryPath = Path.Combine(targetDirectory, ".gpm-" + tx.Id.ToString("N") + "-" + item.Source.Id.ToString("N") + ".tmp");
                if (File.Exists(temporaryPath)) throw new IOException("Обнаружен временный файл предыдущей операции.");
                entry.TemporaryPath = temporaryPath;
                WriteJournal(journalPath, tx);
                GameConfigSnapshotStore.WriteDurable(temporaryPath, item.DesiredBytes);
            }
        }
        tx.Status = "Prepared";
        WriteJournal(journalPath, tx);
        settings.Status = GameConfigApplyStatus.Applying;
        settings.StatusDetail = "Записываем снимок по журналируемой транзакции…";
        AddEvent(settings, settings.Status, settings.StatusDetail);

        try
        {
            foreach (var item in active)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckGameClosed(game);
                VerifyUnchanged(item);
                var entry = tx.Items.Single(candidate => candidate.SourceId == item.Source.Id);
                var original = CaptureOriginalOnce(state, game, item);
                var safety = CaptureSafetyCopy(state, game, item, "Непосредственно перед изменением");
                entry.OriginalBackupExisted = original.Existed;
                entry.OriginalBackupPath = original.ContentPath;
                entry.OriginalBackupHash = original.Sha256;
                entry.OriginalTargetPath = original.TargetPath;
                entry.OriginalRegistryValueName = original.RegistryValueName;
                entry.OriginalRegistryKind = original.RegistryKind;
                entry.OriginalAttributes = original.Attributes;
                entry.SafetyCopyId = safety.Id;
                entry.SafetyCopyPath = safety.ContentPath;
                entry.SafetyCopyHash = safety.Sha256;
                entry.SafetyCopyExisted = safety.Existed;
                entry.SafetyCopyReason = safety.Reason;
                entry.Started = true;
                tx.Status = "Applying item";
                WriteJournal(journalPath, tx);
                if (item.Source.Kind == GameConfigSourceKind.File) ApplyFile(item, entry, txDirectory);
                else RegistryGameConfigBackend.Write(item.Source, item.DesiredBytes);
                entry.Applied = true;
                WriteJournal(journalPath, tx);
                CheckGameClosed(game);
                VerifyDesired(item);
            }
            tx.CompletedAt = DateTimeOffset.Now;
            tx.Completed = true;
            tx.Status = "Completed";
            WriteJournal(journalPath, tx);
            settings.LastAppliedSnapshotId = snapshot.Id;
            settings.LastAppliedAt = tx.CompletedAt;
            settings.LastAppliedHashes = snapshot.Items.ToDictionary(item => item.SourceId, item => item.Sha256);
            settings.LastAppliedExists = snapshot.Items.ToDictionary(item => item.SourceId, item => item.SourceExisted);
            settings.Status = GameConfigApplyStatus.Applied;
            settings.StatusDetail = $"Применён снимок «{snapshot.Name}». Созданы исходная и защитные копии.";
            AddEvent(settings, settings.Status, settings.StatusDetail);
        }
        catch (Exception ex)
        {
            try
            {
                RollbackRecord(tx, journalPath, cancellationToken);
                settings.Status = GameConfigApplyStatus.Error;
                settings.StatusDetail = "Применение не завершилось; внесённые изменения восстановлены: " + ex.Message;
                AddEvent(settings, settings.Status, settings.StatusDetail);
            }
            catch (Exception recoveryException)
            {
                settings.Status = GameConfigApplyStatus.RecoveryRequired;
                settings.StatusDetail = "Автоматический откат остановлен для защиты изменённых данных. Восстановите журнал вручную: " + recoveryException.Message;
                AddEvent(settings, settings.Status, settings.StatusDetail);
                throw new InvalidOperationException(settings.StatusDetail, ex);
            }
            throw;
        }
    }

    private void RestoreOriginalsCore(AppState state, GameEntry game, bool overrideConflict, CancellationToken cancellationToken)
    {
        CheckGameClosed(game);
        var backups = state.GameConfigOriginalBackups.Where(item => item.GameId == game.Id).ToArray();
        if (backups.Length == 0) throw new InvalidOperationException("Исходные файлы ещё не были сохранены GPM.");
        if (store.FindIncompleteTransactions().Count > 0) throw new InvalidOperationException("Сначала восстановите незавершённую операцию.");
        var sources = game.GameConfigSettings.Sources.ToDictionary(item => item.Id);
        var plans = new List<OriginalPlan>();
        foreach (var backup in backups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(backup.SourceId, out var source)) continue;
            string capturedTarget = string.IsNullOrWhiteSpace(backup.TargetPath) ? source.TargetPath : backup.TargetPath;
            if (!capturedTarget.Equals(source.TargetPath, StringComparison.OrdinalIgnoreCase)) continue;
            string capturedValueName = string.IsNullOrWhiteSpace(backup.RegistryValueName) ? source.RegistryValueName : backup.RegistryValueName;
            if (!capturedValueName.Equals(source.RegistryValueName, StringComparison.OrdinalIgnoreCase)) continue;
            if (source.Kind == GameConfigSourceKind.File)
            {
                if (!GameConfigPathSafety.ValidateCandidate(source.TargetPath, source.BinaryExplicitlyAllowed, out string reason) ||
                    !GameConfigPathSafety.IsWithin(source.TargetPath, source.ApprovedRootPath))
                    throw new InvalidOperationException("Небезопасный источник восстановления: " + reason);
            }
            else if (!RegistryGameConfigBackend.IsAllowed(source, out string reason))
                throw new InvalidOperationException("Небезопасный источник восстановления: " + reason);
            var current = ReadCurrent(source);
            byte[] original = backup.Existed ? store.ReadBlob(backup.ContentPath) : source.Kind == GameConfigSourceKind.Registry
                ? JsonSerializer.SerializeToUtf8Bytes(new RegistryValueEnvelope()) : [];
            if (!Hash(original).Equals(backup.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Исходная копия не прошла проверку SHA-256. Восстановление остановлено.");
            var safetyCopy = CaptureSafetyCopy(state, game,
                new PlannedItem(source, new GameConfigSnapshotItem(), current.Bytes, current.Bytes, current.Exists,
                    Hash(current.Bytes), current.Attributes, false), "Перед восстановлением исходных значений");
            plans.Add(new OriginalPlan(source, backup, original, current.Bytes, current.Exists, Hash(current.Bytes), current.Attributes,
                safetyCopy));
        }
        if (plans.Count == 0) throw new InvalidOperationException("Исходные копии не связаны с активными источниками игры.");
        foreach (var plan in plans)
        {
            var setting = game.GameConfigSettings;
            if (setting.LastAppliedHashes.TryGetValue(plan.Source.Id, out string? expected) &&
                (!Hash(plan.CurrentBytes).Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                 setting.LastAppliedExists.TryGetValue(plan.Source.Id, out bool expectedExists) && plan.CurrentExists != expectedExists))
            {
                if (!overrideConflict)
                    throw new GameConfigConflictException("Рабочие параметры изменились после применения GPM. Создана защитная копия; повторите восстановление с явным подтверждением, чтобы перезаписать текущее состояние.");
            }
        }
        Guid id = Guid.NewGuid();
        var tx = new GameConfigTransactionRecord { Id = id, GameId = game.Id, Status = "Restoring originals" };
        string txDirectory = Path.Combine(store.TransactionPath, id.ToString("N"));
        Directory.CreateDirectory(Path.Combine(txDirectory, "backups"));
        string journal = Path.Combine(txDirectory, "journal.json");
        foreach (var plan in plans)
        {
            string previousBlob = $"transactions/{id:N}/backups/{plan.Source.Id:N}.bin";
            GameConfigSnapshotStore.WriteDurable(store.GetFullPath(previousBlob), plan.CurrentBytes);
            tx.Items.Add(new GameConfigTransactionItem
            {
                SourceId = plan.Source.Id, Kind = plan.Source.Kind, Target = plan.Source.TargetPath,
                RegistryValueName = plan.Source.RegistryValueName, PreviousBlob = previousBlob,
                PreviousExisted = plan.CurrentExists, PreviousHash = plan.CurrentHash,
                PreviousAttributes = plan.CurrentAttributes, DesiredExists = plan.Backup.Existed, DesiredHash = Hash(plan.OriginalBytes),
                ApprovedRootPath = plan.Source.ApprovedRootPath,
                SafetyCopyId = plan.SafetyCopy.Id, SafetyCopyPath = plan.SafetyCopy.ContentPath,
                SafetyCopyHash = plan.SafetyCopy.Sha256, SafetyCopyExisted = plan.SafetyCopy.Existed,
                SafetyCopyReason = plan.SafetyCopy.Reason
            });
        }
        WriteJournal(journal, tx);
        try
        {
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested(); CheckGameClosed(game);
                VerifyUnchanged(plan);
                var entry = tx.Items.Single(item => item.SourceId == plan.Source.Id);
                entry.Started = true;
                tx.Status = "Restoring original item";
                if (plan.Source.Kind == GameConfigSourceKind.File && plan.Backup.Existed)
                    entry.TemporaryPath = Path.Combine(Path.GetDirectoryName(plan.Source.TargetPath)!, $".gpm-{tx.Id:N}-{plan.Source.Id:N}.tmp");
                WriteJournal(journal, tx);
                if (plan.Source.Kind == GameConfigSourceKind.Registry) RegistryGameConfigBackend.Write(plan.Source, plan.OriginalBytes);
                else RestoreFile(plan.Source.TargetPath, plan.Backup.Existed ? plan.OriginalBytes : null, txDirectory,
                    plan.Backup.Attributes, entry.TemporaryPath);
                entry.Applied = true; WriteJournal(journal, tx);
            }
            tx.CompletedAt = DateTimeOffset.Now;
            tx.Completed = true; tx.Status = "Originals restored"; WriteJournal(journal, tx);
            game.GameConfigSettings.LastAppliedHashes.Clear();
            game.GameConfigSettings.LastAppliedExists.Clear();
            game.GameConfigSettings.LastAppliedSnapshotId = null;
            game.GameConfigSettings.LastAppliedAt = tx.CompletedAt;
            game.GameConfigSettings.Status = GameConfigApplyStatus.Applied;
            game.GameConfigSettings.StatusDetail = "Исходные значения графических конфигураций восстановлены.";
            AddEvent(game.GameConfigSettings, game.GameConfigSettings.Status, game.GameConfigSettings.StatusDetail);
        }
        catch
        {
            RollbackRecord(tx, journal, cancellationToken);
            throw;
        }
    }

    private void RecoverCore(AppState state, string journalPath, CancellationToken cancellationToken)
    {
        string fullJournal = Path.GetFullPath(journalPath);
        string txRoot = Path.GetFullPath(store.TransactionPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!fullJournal.StartsWith(txRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullJournal))
            throw new InvalidDataException("Журнал восстановления находится вне папки GPM.");
        var tx = JsonSerializer.Deserialize<GameConfigTransactionRecord>(File.ReadAllBytes(fullJournal), JsonOptions)
            ?? throw new InvalidDataException("Журнал транзакции повреждён.");
        string expectedDirectory = Path.Combine(txRoot, tx.Id.ToString("N"));
        if (!Path.GetDirectoryName(fullJournal)!.Equals(expectedDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ID транзакции не совпадает с каталогом журнала.");
        if (tx.Completed) return;
        RollbackRecord(tx, fullJournal, cancellationToken);
        var game = state.Games.FirstOrDefault(item => item.Id == tx.GameId);
        if (game is not null)
        {
            game.GameConfigSettings.Status = GameConfigApplyStatus.Ready;
            game.GameConfigSettings.StatusDetail = "Незавершённая операция отменена, исходное рабочее состояние восстановлено.";
            AddEvent(game.GameConfigSettings, game.GameConfigSettings.Status, game.GameConfigSettings.StatusDetail);
        }
    }

    private void RollbackRecord(GameConfigTransactionRecord tx, string journalPath, CancellationToken cancellationToken)
    {
        foreach (var item in tx.Items.Where(entry => entry.Started || entry.Applied).Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new GameConfigSource
            {
                Id = item.SourceId, GameId = tx.GameId, Kind = item.Kind, TargetPath = item.Target,
                RegistryValueName = item.RegistryValueName, ApprovedRootPath = item.ApprovedRootPath, UserConfirmed = true
            };
            if (item.Kind == GameConfigSourceKind.File)
            {
                if (!GameConfigPathSafety.ValidateCandidate(item.Target, allowBinary: true, out string reason))
                    throw new InvalidOperationException("Откат запрещён для небезопасного пути: " + reason);
                if (!GameConfigPathSafety.IsWithin(item.Target, item.ApprovedRootPath))
                    throw new InvalidDataException("Целевой путь вышел за подтверждённую папку.");
            }
            else if (!RegistryGameConfigBackend.IsAllowed(source, out string registryReason))
                throw new InvalidDataException("Откат реестра запрещён: " + registryReason);
            var now = ReadCurrent(source);
            string currentHash = Hash(now.Bytes);
            if (currentHash.Equals(item.PreviousHash, StringComparison.OrdinalIgnoreCase) && now.Exists == item.PreviousExisted)
            {
                if (item.Kind == GameConfigSourceKind.File && now.Exists && item.PreviousAttributes is { } previousAttributes &&
                    now.Attributes != previousAttributes)
                    File.SetAttributes(item.Target, previousAttributes & ~FileAttributes.ReparsePoint);
                item.Applied = false;
                item.Started = false;
                WriteJournal(journalPath, tx);
                continue;
            }
            if (item.DesiredExists && !currentHash.Equals(item.DesiredHash, StringComparison.OrdinalIgnoreCase))
                throw new GameConfigConflictException("Целевой файл изменился после транзакции; откат не будет его перезаписывать.");
            if (!item.DesiredExists && now.Exists)
                throw new GameConfigConflictException("В целевом пути появился новый файл; откат не будет его удалять.");
            byte[] previous = store.ReadBlob(item.PreviousBlob ?? throw new InvalidDataException("В журнале отсутствует защитная копия."));
            if (!Hash(previous).Equals(item.PreviousHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Хеш защитной копии в журнале не совпал.");
            if (item.Kind == GameConfigSourceKind.Registry) RegistryGameConfigBackend.Write(source, previous);
            else
            {
                string? rollbackTemporary = item.PreviousExisted
                    ? Path.Combine(Path.GetDirectoryName(item.Target)!, $".gpm-rollback-{tx.Id:N}-{item.SourceId:N}.tmp") : null;
                item.RollbackTemporaryPath = rollbackTemporary;
                WriteJournal(journalPath, tx);
                RestoreFile(item.Target, item.PreviousExisted ? previous : null, Path.GetDirectoryName(journalPath)!,
                    item.PreviousAttributes, rollbackTemporary);
            }
            item.Applied = false;
            item.Started = false;
            item.RollbackTemporaryPath = null;
            WriteJournal(journalPath, tx);
        }
        foreach (var item in tx.Items.Where(entry => !entry.Started))
        {
            DeleteTransactionTemporary(item.TemporaryPath, item, tx);
            DeleteTransactionTemporary(item.RollbackTemporaryPath, item, tx);
        }
        tx.RecoveryCompleted = true; tx.Status = "Rolled back"; WriteJournal(journalPath, tx);
    }

    private void ApplyFile(PlannedItem item, GameConfigTransactionItem entry, string txDirectory)
    {
        string destination = item.Source.TargetPath;
        if (!item.SnapshotItem.SourceExisted)
        {
            if (File.Exists(destination))
            {
                string archived = Path.Combine(txDirectory, "temporary", item.Source.Id.ToString("N") + ".removed");
                File.Move(destination, archived);
                entry.TemporaryPath = archived;
            }
            return;
        }
        string temporary = entry.TemporaryPath ?? throw new InvalidDataException("В журнале отсутствует временный путь.");
        if (!File.Exists(temporary)) throw new IOException("Подготовленный временный файл пропал.");
        ReplaceFile(temporary, destination, item.SnapshotItem.OriginalAttributes);
    }

    private void RestoreFile(string destination, byte[]? desired, string txDirectory, FileAttributes? attributes,
        string? temporaryPath = null)
    {
        if (!GameConfigPathSafety.ValidateCandidate(destination, allowBinary: true, out string reason))
            throw new InvalidOperationException("Небезопасный путь восстановления: " + reason);
        string? directory = Path.GetDirectoryName(destination);
        if (directory is null) throw new InvalidDataException("Некорректный путь файла.");
        Directory.CreateDirectory(directory);
        if (desired is null)
        {
            if (File.Exists(destination))
            {
                var current = File.GetAttributes(destination);
                if ((current & FileAttributes.ReadOnly) != 0) File.SetAttributes(destination, current & ~FileAttributes.ReadOnly);
                File.Delete(destination);
            }
            return;
        }
        string temp = temporaryPath ?? Path.Combine(directory, ".gpm-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        if (File.Exists(temp))
        {
            if (!Hash(File.ReadAllBytes(temp)).Equals(Hash(desired), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Существующий временный файл восстановления содержит неожиданные данные.");
        }
        else
        {
            string staging = temp + ".writing-" + Guid.NewGuid().ToString("N");
            GameConfigSnapshotStore.WriteDurable(staging, desired);
            File.Move(staging, temp);
        }
        ReplaceFile(temp, destination, attributes);
    }

    private static void DeleteTransactionTemporary(string? path, GameConfigTransactionItem item, GameConfigTransactionRecord tx)
    {
        if (string.IsNullOrWhiteSpace(path) || item.Kind != GameConfigSourceKind.File) return;
        if (!GameConfigPathSafety.ValidateCandidate(item.Target, allowBinary: true, out _) ||
            !GameConfigPathSafety.IsWithin(item.Target, item.ApprovedRootPath)) return;
        string fullPath = Path.GetFullPath(path);
        string targetDirectory = Path.GetFullPath(Path.GetDirectoryName(item.Target)!);
        string expectedDesired = Path.Combine(targetDirectory, $".gpm-{tx.Id:N}-{item.SourceId:N}.tmp");
        string expectedRollback = Path.Combine(targetDirectory, $".gpm-rollback-{tx.Id:N}-{item.SourceId:N}.tmp");
        if ((fullPath.Equals(expectedDesired, StringComparison.OrdinalIgnoreCase) ||
             fullPath.Equals(expectedRollback, StringComparison.OrdinalIgnoreCase)) && File.Exists(fullPath))
            File.Delete(fullPath);
    }

    private static void ReplaceFile(string temporary, string destination, FileAttributes? attributes)
    {
        FileAttributes? originalAttributes = File.Exists(destination) ? File.GetAttributes(destination) : null;
        if (originalAttributes is { } existing && (existing & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(destination, existing & ~FileAttributes.ReadOnly);
        try
        {
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            if (attributes is { } value) File.SetAttributes(destination, value & ~FileAttributes.ReparsePoint);
        }
        catch
        {
            if (originalAttributes is { } restore && File.Exists(destination))
            {
                try { File.SetAttributes(destination, restore & ~FileAttributes.ReparsePoint); }
                catch { }
            }
            throw;
        }
    }

    private static void VerifyUnchanged(PlannedItem item) => VerifyUnchanged(item.Source, item.CurrentExists,
        item.CurrentHash, item.CurrentAttributes);

    private static void VerifyUnchanged(OriginalPlan item) => VerifyUnchanged(item.Source, item.CurrentExists,
        item.CurrentHash, item.CurrentAttributes);

    private static void VerifyUnchanged(GameConfigSource source, bool expectedExists, string expectedHash,
        FileAttributes? expectedAttributes)
    {
        var current = ReadCurrent(source);
        if (!current.Exists.Equals(expectedExists) || !Hash(current.Bytes).Equals(expectedHash, StringComparison.OrdinalIgnoreCase) ||
            current.Attributes != expectedAttributes)
            throw new GameConfigConflictException($"Файл изменился во время подготовки: {source.TargetPath}. Операция остановлена.");
    }

    private static void VerifyDesired(PlannedItem item)
    {
        var current = ReadCurrent(item.Source);
        if (item.Source.Kind == GameConfigSourceKind.File && !item.SnapshotItem.SourceExisted)
        {
            if (current.Exists) throw new IOException("После применения ожидаемого отсутствия файл всё ещё существует.");
        }
        else if (!Hash(current.Bytes).Equals(item.SnapshotItem.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Повторное чтение целевого файла не совпало со снимком.");
    }

    private static (bool Exists, byte[] Bytes, FileAttributes? Attributes) ReadCurrent(GameConfigSource source)
    {
        if (source.Kind == GameConfigSourceKind.Registry)
        {
            byte[] value = RegistryGameConfigBackend.Read(source);
            bool exists = JsonDocument.Parse(value).RootElement.GetProperty("Exists").GetBoolean();
            return (exists, value, null);
        }
        if (!File.Exists(source.TargetPath)) return (false, [], null);
        var info = new FileInfo(source.TargetPath);
        if (info.Length > GameConfigPathSafety.MaxFileBytes)
            throw new InvalidDataException("Целевой файл превышает ограничение 32 МБ.");
        byte[] bytes = ReadFileBounded(source.TargetPath);
        if (bytes.Length > GameConfigPathSafety.MaxFileBytes)
            throw new InvalidDataException("Целевой файл превысил ограничение 32 МБ во время чтения.");
        return (true, bytes, File.GetAttributes(source.TargetPath));
    }

    private bool MatchesSavedSnapshotState(AppState state, Guid gameId,
        IReadOnlyDictionary<Guid, CurrentSourceState> current)
    {
        if (current.Count == 0) return false;
        foreach (var snapshot in state.GameConfigSnapshots.Where(item => item.GameId == gameId))
            if (SnapshotMatchesCurrent(snapshot, current)) return true;
        return false;
    }

    private IReadOnlyList<Guid> FindMatchingSavedSnapshotIds(AppState state, Guid gameId,
        IReadOnlyDictionary<Guid, CurrentSourceState> current)
    {
        if (current.Count == 0) return [];
        return state.GameConfigSnapshots.Where(item => item.GameId == gameId && SnapshotMatchesCurrent(item, current))
            .Select(item => item.Id).ToArray();
    }

    private bool SnapshotMatchesCurrent(GameConfigSnapshot snapshot,
        IReadOnlyDictionary<Guid, CurrentSourceState> current)
    {
        if (snapshot.Items.Count != current.Count ||
            snapshot.Items.Select(item => item.SourceId).Distinct().Count() != current.Count) return false;

        foreach (var item in snapshot.Items)
        {
            if (!current.TryGetValue(item.SourceId, out var value) || value.Exists != item.SourceExisted ||
                value.Bytes.LongLength != item.Size || !value.Hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                byte[] stored = store.ReadSnapshotItem(snapshot, item);
                if (stored.LongLength != item.Size || !Hash(stored).Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        return true;
    }

    private static byte[] ReadFileBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan);
        using var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > GameConfigPathSafety.MaxFileBytes)
                throw new InvalidDataException("Целевой файл превысил ограничение 32 МБ во время чтения.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private GameConfigOriginalBackup CaptureOriginalOnce(AppState state, GameEntry game, PlannedItem item)
    {
        var existing = state.GameConfigOriginalBackups.FirstOrDefault(backup => backup.GameId == game.Id &&
            backup.SourceId == item.Source.Id &&
            (string.IsNullOrWhiteSpace(backup.TargetPath) || backup.TargetPath.Equals(item.Source.TargetPath, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(backup.RegistryValueName) || backup.RegistryValueName.Equals(item.Source.RegistryValueName, StringComparison.OrdinalIgnoreCase)));
        if (existing is not null) return existing;
        string contentPath = "";
        if (item.CurrentExists)
            contentPath = store.WriteImmutable($"game-configs/{game.Id:N}/originals/{item.Source.Id:N}-{Guid.NewGuid():N}.bin", item.CurrentBytes);
        var backup = new GameConfigOriginalBackup
        {
            GameId = game.Id, SourceId = item.Source.Id, TargetPath = item.Source.TargetPath,
            RegistryValueName = item.Source.RegistryValueName, Existed = item.CurrentExists,
            ContentPath = contentPath, Sha256 = item.CurrentHash,
            RegistryKind = item.Source.Kind == GameConfigSourceKind.Registry ? RegistryGameConfigBackend.Kind(item.CurrentBytes) : "",
            Attributes = item.CurrentAttributes
        };
        state.GameConfigOriginalBackups.Add(backup);
        return backup;
    }

    private GameConfigSafetyCopy CaptureSafetyCopy(AppState state, GameEntry game, PlannedItem item, string reason)
    {
        string path = $"game-configs/{game.Id:N}/safety/{Guid.NewGuid():N}.bin";
        store.WriteImmutable(path, item.CurrentBytes);
        var copy = new GameConfigSafetyCopy
        {
            GameId = game.Id, SourceId = item.Source.Id, ContentPath = path, Sha256 = item.CurrentHash,
            Existed = item.CurrentExists, Reason = reason
        };
        state.GameConfigSafetyCopies.Add(copy);
        return copy;
    }

    private static void EnsureNoCrossGameOwnership(AppState state, GameEntry game)
    {
        foreach (var source in game.GameConfigSettings.Sources)
        {
            var owner = state.Games.Where(other => other.Id != game.Id).SelectMany(other => other.GameConfigSettings.Sources)
                .FirstOrDefault(other => other.Kind == source.Kind &&
                    string.Equals(other.Kind == GameConfigSourceKind.File ? Path.GetFullPath(other.TargetPath) : other.TargetPath + "|" + other.RegistryValueName,
                        source.Kind == GameConfigSourceKind.File ? Path.GetFullPath(source.TargetPath) : source.TargetPath + "|" + source.RegistryValueName,
                        StringComparison.OrdinalIgnoreCase));
            if (owner is not null) throw new GameConfigConflictException("Другой профиль GPM уже управляет этим путём или значением реестра.");
        }
    }

    private void CheckGameClosed(GameEntry game)
    {
        var result = processGuard.Check(game);
        if (result.State != GameProcessState.Closed) throw new GameConfigDeferredException(result.Detail, result.State);
    }

    private static GameConfigTransactionRecord NewTransaction(Guid gameId, Guid snapshotId, IReadOnlyList<PlannedItem> items) =>
        new()
        {
            Id = Guid.NewGuid(), GameId = gameId, SnapshotId = snapshotId,
            Items = items.Select(item => new GameConfigTransactionItem
            {
                SourceId = item.Source.Id, Kind = item.Source.Kind, Target = item.Source.TargetPath,
                RegistryValueName = item.Source.RegistryValueName, PreviousExisted = item.CurrentExists,
                PreviousAttributes = item.CurrentAttributes, PreviousHash = item.CurrentHash, DesiredHash = item.SnapshotItem.Sha256,
                DesiredExists = item.SnapshotItem.SourceExisted,
                ApprovedRootPath = item.Source.ApprovedRootPath
            }).ToList()
        };

    private static void WriteJournal(string path, GameConfigTransactionRecord record)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        string temporary = path + ".writing-" + Guid.NewGuid().ToString("N");
        GameConfigSnapshotStore.WriteDurable(temporary, bytes);
        try
        {
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            // The temporary name is unique to this write; a failed atomic replace must not
            // leave a partial journal that can be mistaken for the transaction record.
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static void AddEvent(GameConfigSettings settings, GameConfigApplyStatus status, string message)
    {
        settings.Events.Add(new GameConfigEvent { Status = status, Message = message });
        if (settings.Events.Count > 100) settings.Events.RemoveRange(0, settings.Events.Count - 100);
    }

    private bool ReconcileCompleted(AppState state)
    {
        if (!Directory.Exists(store.TransactionPath)) return false;
        bool changed = false;
        var records = Directory.EnumerateFiles(store.TransactionPath, "journal.json", SearchOption.AllDirectories)
            .Select(path =>
            {
                try { return (Path: path, Record: JsonSerializer.Deserialize<GameConfigTransactionRecord>(File.ReadAllBytes(path), JsonOptions)); }
                catch { return (Path: path, Record: null); }
            })
            .Where(item => item.Record is { Completed: true })
            .OrderBy(item => item.Record!.CompletedAt ?? item.Record!.CreatedAt).ThenBy(item => item.Record!.Id).ToArray();

        // Completed journals are the durable source of truth during the small window
        // between a file transaction and the corresponding SQLite save.
        foreach (var item in records)
        {
            try
            {
                var record = item.Record!;
                DateTimeOffset completedAt = record.CompletedAt ?? record.CreatedAt;
                var game = state.Games.FirstOrDefault(item => item.Id == record.GameId);
                if (game is null) continue;
                foreach (var transactionItem in record.Items)
                {
                    if (transactionItem.OriginalBackupExisted is { } existed &&
                        !state.GameConfigOriginalBackups.Any(backup => backup.GameId == game.Id && backup.SourceId == transactionItem.SourceId &&
                            (string.IsNullOrWhiteSpace(backup.TargetPath) || backup.TargetPath.Equals(transactionItem.OriginalTargetPath, StringComparison.OrdinalIgnoreCase)) &&
                            (string.IsNullOrWhiteSpace(backup.RegistryValueName) || backup.RegistryValueName.Equals(transactionItem.OriginalRegistryValueName, StringComparison.OrdinalIgnoreCase))))
                    {
                        string path = transactionItem.OriginalBackupPath ?? "";
                        if (existed && !string.IsNullOrWhiteSpace(path) &&
                            Hash(store.ReadBlob(path)).Equals(transactionItem.OriginalBackupHash, StringComparison.OrdinalIgnoreCase))
                        {
                            state.GameConfigOriginalBackups.Add(new GameConfigOriginalBackup
                            {
                                GameId = game.Id, SourceId = transactionItem.SourceId, TargetPath = transactionItem.OriginalTargetPath,
                                RegistryValueName = transactionItem.OriginalRegistryValueName,
                                Existed = true, ContentPath = path,
                                Sha256 = transactionItem.OriginalBackupHash ?? "", RegistryKind = transactionItem.OriginalRegistryKind,
                                Attributes = transactionItem.OriginalAttributes, CapturedAt = completedAt
                            });
                            changed = true;
                        }
                        else if (!existed)
                        {
                            state.GameConfigOriginalBackups.Add(new GameConfigOriginalBackup
                            {
                                GameId = game.Id, SourceId = transactionItem.SourceId, TargetPath = transactionItem.OriginalTargetPath,
                                RegistryValueName = transactionItem.OriginalRegistryValueName,
                                Existed = false,
                                Sha256 = transactionItem.OriginalBackupHash ?? "", RegistryKind = transactionItem.OriginalRegistryKind,
                                CapturedAt = completedAt
                            });
                            changed = true;
                        }
                    }
                    if (transactionItem.SafetyCopyId is { } safetyId &&
                        !state.GameConfigSafetyCopies.Any(copy => copy.Id == safetyId) &&
                        !string.IsNullOrWhiteSpace(transactionItem.SafetyCopyPath) &&
                        Hash(store.ReadBlob(transactionItem.SafetyCopyPath)).Equals(transactionItem.SafetyCopyHash, StringComparison.OrdinalIgnoreCase))
                    {
                        state.GameConfigSafetyCopies.Add(new GameConfigSafetyCopy
                        {
                            Id = safetyId, GameId = game.Id, SourceId = transactionItem.SourceId,
                            ContentPath = transactionItem.SafetyCopyPath!, Sha256 = transactionItem.SafetyCopyHash ?? "",
                            Existed = transactionItem.SafetyCopyExisted ?? false, CreatedAt = completedAt,
                            Reason = transactionItem.SafetyCopyReason
                        });
                        changed = true;
                    }
                }

                if (game.GameConfigSettings.LastAppliedAt > completedAt) continue;
                if (record.SnapshotId is not { } snapshotId || state.GameConfigSnapshots.All(snapshot => snapshot.Id != snapshotId))
                {
                    if (record.Status.Equals("Originals restored", StringComparison.OrdinalIgnoreCase))
                    {
                        bool differs = game.GameConfigSettings.LastAppliedSnapshotId is not null ||
                            game.GameConfigSettings.LastAppliedHashes.Count > 0 || game.GameConfigSettings.LastAppliedExists.Count > 0 ||
                            game.GameConfigSettings.LastAppliedAt != completedAt ||
                            game.GameConfigSettings.Status != GameConfigApplyStatus.Applied;
                        game.GameConfigSettings.LastAppliedSnapshotId = null;
                        game.GameConfigSettings.LastAppliedHashes.Clear();
                        game.GameConfigSettings.LastAppliedExists.Clear();
                        game.GameConfigSettings.LastAppliedAt = completedAt;
                        game.GameConfigSettings.Status = GameConfigApplyStatus.Applied;
                        game.GameConfigSettings.StatusDetail = "Восстановление исходных значений подтверждено завершённым журналом.";
                        changed |= differs;
                    }
                    continue;
                }
                var hashes = record.AppliedHashes.Count > 0
                    ? new Dictionary<Guid, string>(record.AppliedHashes)
                    : record.Items.ToDictionary(transactionItem => transactionItem.SourceId, transactionItem => transactionItem.DesiredHash);
                bool same = game.GameConfigSettings.LastAppliedSnapshotId == snapshotId &&
                    game.GameConfigSettings.LastAppliedAt == completedAt &&
                    game.GameConfigSettings.LastAppliedHashes.Count == hashes.Count &&
                    hashes.All(entry => game.GameConfigSettings.LastAppliedHashes.TryGetValue(entry.Key, out string? value) &&
                        value.Equals(entry.Value, StringComparison.OrdinalIgnoreCase)) &&
                    game.GameConfigSettings.LastAppliedExists.Count == record.AppliedExists.Count &&
                    record.AppliedExists.All(entry => game.GameConfigSettings.LastAppliedExists.TryGetValue(entry.Key, out bool value) && value == entry.Value) &&
                    game.GameConfigSettings.Status == GameConfigApplyStatus.Applied;
                if (!same)
                {
                    game.GameConfigSettings.LastAppliedSnapshotId = snapshotId;
                    game.GameConfigSettings.LastAppliedHashes = hashes;
                    game.GameConfigSettings.LastAppliedExists = record.AppliedExists.Count > 0
                        ? new Dictionary<Guid, bool>(record.AppliedExists)
                        : record.Items.ToDictionary(transactionItem => transactionItem.SourceId, transactionItem => transactionItem.DesiredExists);
                    game.GameConfigSettings.LastAppliedAt = completedAt;
                    game.GameConfigSettings.Status = GameConfigApplyStatus.Applied;
                    game.GameConfigSettings.StatusDetail = "Применение восстановлено по завершённому журналу.";
                    changed = true;
                }
            }
            catch { }
        }
        return changed;
    }

    private sealed record PlannedItem(GameConfigSource Source, GameConfigSnapshotItem SnapshotItem, byte[] DesiredBytes,
        byte[] CurrentBytes, bool CurrentExists, string CurrentHash, FileAttributes? CurrentAttributes, bool NoChange);
    private sealed record CurrentSourceState(bool Exists, byte[] Bytes, string Hash);
    private sealed record OriginalPlan(GameConfigSource Source, GameConfigOriginalBackup Backup, byte[] OriginalBytes,
        byte[] CurrentBytes, bool CurrentExists, string CurrentHash, FileAttributes? CurrentAttributes, GameConfigSafetyCopy SafetyCopy);
}

public sealed class GameConfigConflictException(string message) : IOException(message);
public sealed class GameConfigDeferredException(string message, GameProcessState state) : IOException(message)
{
    public GameProcessState ProcessState { get; } = state;
}
