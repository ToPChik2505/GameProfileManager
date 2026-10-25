using GameProfileManager.Application;
using GameProfileManager.Core;
using GameProfileManager.Infrastructure;

namespace GameProfileManager;

public sealed partial class MainViewModel
{
    public long GameConfigStateVersion => Interlocked.Read(ref _gameConfigStateVersion);

    public void NotifyGameConfigStateChanged()
    {
        Interlocked.Increment(ref _gameConfigStateVersion);
        Changed(nameof(GameConfigStateVersion));
    }

    public IReadOnlyList<GameConfigSnapshot> GameConfigSnapshots(Guid gameId) =>
        _state.GameConfigSnapshots.Where(item => item.GameId == gameId).OrderByDescending(item => item.CreatedAt).ToArray();

    public bool HasGameConfigSnapshotInfo(GameEntry game) =>
        game.GameConfigSettings.Sources.Count > 0 || _state.GameConfigSnapshots.Any(item => item.GameId == game.Id);

    public async Task<(string Name, string Detail)> ReadCurrentGameConfigSnapshotAsync(GameEntry game)
    {
        var settings = game.GameConfigSettings;
        var snapshots = _state.GameConfigSnapshots.Where(item => item.GameId == game.Id).ToArray();
        var lastApplied = snapshots.FirstOrDefault(item => item.Id == settings.LastAppliedSnapshotId);
        string lastAppliedDescription = lastApplied is null ? "" :
            $"Последнее применение GPM: «{lastApplied.Name}»" +
            (settings.LastAppliedAt is { } at ? $" · {at.ToLocalTime():g}" : "") + ".";

        if (settings.Sources.Count == 0)
            return (snapshots.Length == 0 ? "Не настроено" : "Нет активных источников",
                "Добавьте и подтвердите файлы настроек на вкладке «Графические файлы».");
        if (snapshots.Length == 0)
            return ("Снимок ещё не сохранён", "Сохраните снимок на вкладке «Графические файлы».");

        var process = _gameProcessGuard.Check(game);
        if (process.State != GameProcessState.Closed)
            return (lastApplied?.Name ?? "Не проверен",
                process.Detail + (lastAppliedDescription.Length > 0 ? " " + lastAppliedDescription : ""));

        AppState stateSnapshot = AppStateSnapshot.Capture(_state);
        GameEntry gameSnapshot = stateSnapshot.Games.First(item => item.Id == game.Id);
        IReadOnlyList<Guid> matchingIds;
        try
        {
            matchingIds = await Task.Run(() => _gameConfigTransactions.FindCurrentSavedSnapshotIds(stateSnapshot, gameSnapshot));
        }
        catch (Exception ex)
        {
            return (lastApplied?.Name ?? "Не удалось проверить",
                "Не удалось сверить текущие файлы: " + ex.Message +
                (lastAppliedDescription.Length > 0 ? " " + lastAppliedDescription : ""));
        }

        if (matchingIds.Count > 0)
        {
            string[] matchingNames = matchingIds.Select(id => snapshots.FirstOrDefault(item => item.Id == id)?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Cast<string>().ToArray();
            bool matchesLastApplied = settings.LastAppliedSnapshotId is { } appliedId && matchingIds.Contains(appliedId);
            string detail = matchesLastApplied
                ? "Текущие файлы совпадают с последним снимком, применённым GPM." +
                    (settings.LastAppliedAt is { } appliedAt ? $" Применён {appliedAt.ToLocalTime():g}." : "")
                : "Текущие файлы совпадают с сохранённым снимком.";

            var target = GameConfigCoordinator.ResolveTarget(_state, game, _displays.ToArray());
            var targetSnapshot = target.SnapshotId is { } targetId
                ? snapshots.FirstOrDefault(item => item.Id == targetId) : null;
            if (settings.AutoApply && targetSnapshot is not null && !matchingIds.Contains(targetSnapshot.Id))
                detail += $" Для эффективного дисплея назначен снимок «{targetSnapshot.Name}»; состояние: {GameConfigStatusLabel(settings.Status).ToLowerInvariant()}.";
            if (matchingNames.Length > 1)
                detail += " Содержимое одинаково у нескольких снимков.";
            return (string.Join(" / ", matchingNames), detail);
        }

        string noMatchDetail = "Текущие файлы не совпадают ни с одним сохранённым снимком.";
        if (lastAppliedDescription.Length > 0) noMatchDetail += " " + lastAppliedDescription;
        var currentTarget = GameConfigCoordinator.ResolveTarget(_state, game, _displays.ToArray());
        var assigned = currentTarget.SnapshotId is { } assignedId
            ? snapshots.FirstOrDefault(item => item.Id == assignedId) : null;
        if (settings.AutoApply && assigned is not null)
            noMatchDetail += $" Назначен «{assigned.Name}»; состояние: {GameConfigStatusLabel(settings.Status).ToLowerInvariant()}.";
        return ("Нет совпадения", noMatchDetail);
    }

    public IReadOnlyList<GameConfigAssignment> GameConfigAssignments(Guid gameId) =>
        _state.GameConfigAssignments.Where(item => item.GameId == gameId).ToArray();

    public IReadOnlyList<GameConfigSafetyCopy> GameConfigSafetyCopies(Guid gameId) =>
        _state.GameConfigSafetyCopies.Where(item => item.GameId == gameId).OrderByDescending(item => item.CreatedAt).ToArray();

    public string GameConfigSafetyCopyPreview(GameConfigSafetyCopy copy)
    {
        var stored = _state.GameConfigSafetyCopies.FirstOrDefault(item => item.Id == copy.Id && item.GameId == copy.GameId)
            ?? throw new InvalidOperationException("Защитная копия не найдена.");
        if (!stored.Existed) return "В момент создания копии целевого файла или значения не существовало.";
        byte[] bytes = _gameConfigStore.ReadBlob(stored.ContentPath);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!hash.Equals(stored.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Хеш защитной копии не совпал. Данные не показаны.");
        var source = _state.Games.FirstOrDefault(game => game.Id == stored.GameId)?.GameConfigSettings.Sources
            .FirstOrDefault(source => source.Id == stored.SourceId);
        string extension = source is { Kind: GameConfigSourceKind.File } fileSource
            ? Path.GetExtension(fileSource.TargetPath) : ".json";
        if (!GameConfigPathSafety.IsLikelyBinaryExtension(extension))
        {
            try
            {
                var encoding = bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) ? System.Text.Encoding.Unicode :
                    bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }) ? System.Text.Encoding.BigEndianUnicode :
                    new System.Text.UTF8Encoding(false, true);
                string content = encoding.GetString(bytes).TrimStart('\uFEFF');
                return content.Length > 16000 ? content[..16000] + "\n\n… показаны первые 16 000 символов" : content;
            }
            catch (System.Text.DecoderFallbackException) { }
        }
        string preview = Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 256)));
        return $"Бинарные данные: {bytes.Length:N0} байт. Начало файла в HEX (до 256 байт):\n{preview}";
    }

    public string GameConfigDisplayLabel(string displayId) =>
        _displays.FirstOrDefault(item => item.Id.Equals(displayId, StringComparison.OrdinalIgnoreCase))?.Name ??
        DisplayNameCache.Get(_state, displayId) + " — отключён";

    public GameConfigTarget ResolveGameConfigTarget(GameEntry game) =>
        GameConfigCoordinator.ResolveTarget(_state, game, _displays.ToArray());

    public static string GameConfigStatusLabel(GameConfigApplyStatus status) => status switch
    {
        GameConfigApplyStatus.NotConfigured => "Не настроено",
        GameConfigApplyStatus.Ready => "Готово",
        GameConfigApplyStatus.Pending => "Ожидает",
        GameConfigApplyStatus.WaitingForGameExit => "Ожидает закрытия игры",
        GameConfigApplyStatus.Applying => "Применяется",
        GameConfigApplyStatus.Applied => "Применено",
        GameConfigApplyStatus.Conflict => "Конфликт",
        GameConfigApplyStatus.RecoveryRequired => "Требуется восстановление",
        GameConfigApplyStatus.Error => "Ошибка",
        _ => status.ToString()
    };

    public IReadOnlyList<string> IncompleteGameConfigTransactions => _gameConfigTransactions.IncompleteTransactions();
    public int OrphanGameConfigSnapshotCount => _gameConfigStore is GameConfigSnapshotStore store
        ? store.FindOrphanSnapshots(_state).Count : 0;
    public bool HasOriginalGameConfigBackups(Guid gameId) =>
        _state.Games.FirstOrDefault(game => game.Id == gameId)?.GameConfigSettings.Sources.Any(source =>
            _state.GameConfigOriginalBackups.Any(backup => backup.GameId == gameId && backup.SourceId == source.Id &&
                (string.IsNullOrWhiteSpace(backup.TargetPath) || backup.TargetPath.Equals(source.TargetPath, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrWhiteSpace(backup.RegistryValueName) || backup.RegistryValueName.Equals(source.RegistryValueName, StringComparison.OrdinalIgnoreCase)))) == true;

    private bool HasGameConfigArtifactsToPreserve(Guid gameId)
    {
        var game = _state.Games.FirstOrDefault(item => item.Id == gameId);
        return game?.GameConfigSettings.Status == GameConfigApplyStatus.RecoveryRequired ||
            _state.GameConfigSnapshots.Any(item => item.GameId == gameId) ||
            _state.GameConfigAssignments.Any(item => item.GameId == gameId) ||
            _state.GameConfigOriginalBackups.Any(item => item.GameId == gameId) ||
            _state.GameConfigSafetyCopies.Any(item => item.GameId == gameId);
    }

    public Task<IReadOnlyList<GameConfigCandidate>> FindGameConfigCandidatesAsync(GameEntry game) =>
        _gameConfigDiscovery.FindCandidatesAsync(game);

    public Task<IReadOnlyList<GameConfigCandidate>> FindOnlineGameConfigCandidatesAsync(GameEntry game) =>
        _gameConfigDiscovery.FindOnlineCandidatesAsync(game);

    public bool PcgamingWikiCredentialsConfigured
    {
        get
        {
            try { return _pcgamingWikiCredentialStore.Read() is not null; }
            catch { return false; }
        }
    }

    public string PcgamingWikiCredentialsLabel
    {
        get
        {
            try
            {
                var credentials = _pcgamingWikiCredentialStore.Read();
                return credentials is null ? "Bot-пароль не настроен" : $"Сохранён bot-логин: {credentials.Username}";
            }
            catch (Exception ex) { return "Не удалось прочитать сохранённый bot-пароль: " + ex.Message; }
        }
    }

    public string PcgamingWikiBotUsername
    {
        get
        {
            try { return _pcgamingWikiCredentialStore.Read()?.Username ?? ""; }
            catch { return ""; }
        }
    }

    public string PcgamingWikiSearchModeLabel => PcgamingWikiCredentialsConfigured
        ? "Точный режим: PCGamingWiki GameData через Cargo API"
        : "Обычный режим: разбор данных статьи, результаты могут быть неполными";

    public void SavePcgamingWikiCredentials(string username, string password)
    {
        _pcgamingWikiCredentialStore.Save(new PcgamingWikiCredentials(username, password));
        Changed(nameof(PcgamingWikiCredentialsConfigured));
        Changed(nameof(PcgamingWikiCredentialsLabel));
        Changed(nameof(PcgamingWikiBotUsername));
        Changed(nameof(PcgamingWikiSearchModeLabel));
        Notice = "Bot-учётные данные сохранены в Диспетчере учётных данных Windows. Пароль не попадает в базу и резервные копии GPM.";
    }

    public void DeletePcgamingWikiCredentials()
    {
        _pcgamingWikiCredentialStore.Delete();
        Changed(nameof(PcgamingWikiCredentialsConfigured));
        Changed(nameof(PcgamingWikiCredentialsLabel));
        Changed(nameof(PcgamingWikiBotUsername));
        Changed(nameof(PcgamingWikiSearchModeLabel));
        Notice = "Bot-пароль PCGamingWiki удалён из Диспетчера учётных данных Windows.";
    }

    public async Task TestPcgamingWikiAccessAsync()
    {
        if (!BeginActivity("Проверяем доступ к PCGamingWiki Cargo API…")) return;
        try
        {
            await _gameConfigDiscovery.TestOnlineAccessAsync();
            Notice = "Вход PCGamingWiki подтверждён, Cargo API доступен.";
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            Notice = "Проверка PCGamingWiki не пройдена: " + ex.Message;
        }
        finally { EndActivity(); }
    }

    public void AddGameConfigFileSources(GameEntry game, IEnumerable<(string Path, string DefinitionId, bool AllowBinary)> files)
    {
        AddGameConfigCandidates(game, files.Select(candidate => new GameConfigCandidate(candidate.Path,
            candidate.DefinitionId, "Выбрано вручную", SourceKind: GameConfigSourceKind.File,
            AllowBinary: candidate.AllowBinary)));
    }

    public void AddGameConfigCandidates(GameEntry game, IEnumerable<GameConfigCandidate> candidates)
    {
        EnsureGameConfigEditable(game);
        GameConfigCandidate[] selected = candidates.ToArray();
        var newSources = new List<GameConfigSource>();
        var existingFiles = game.GameConfigSettings.Sources.Where(item => item.Kind == GameConfigSourceKind.File)
            .Select(item => Path.GetFullPath(item.TargetPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingRegistry = game.GameConfigSettings.Sources.Where(item => item.Kind == GameConfigSourceKind.Registry)
            .Select(item => item.TargetPath + "|" + item.RegistryValueName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (GameConfigCandidate candidate in selected)
        {
            if (candidate.SourceKind == GameConfigSourceKind.File)
            {
                if (!GameConfigPathSafety.ValidateCandidate(candidate.Path, candidate.AllowBinary, out string reason))
                    throw new InvalidOperationException(reason);
                string path = Path.GetFullPath(candidate.Path);
                if (!File.Exists(path)) throw new FileNotFoundException("Выбранный файл больше не существует.", path);
                if (existingFiles.Contains(path)) continue;
                if (_state.Games.Where(item => item.Id != game.Id).SelectMany(item => item.GameConfigSettings.Sources)
                    .Any(item => item.Kind == GameConfigSourceKind.File && Path.GetFullPath(item.TargetPath).Equals(path, StringComparison.OrdinalIgnoreCase)))
                    throw new GameConfigConflictException("Этот файл уже выбран источником графики другой игры GPM.");
                newSources.Add(new GameConfigSource
                {
                    GameId = game.Id, Kind = GameConfigSourceKind.File, TargetPath = path,
                    ApprovedRootPath = Path.GetDirectoryName(path)!, DefinitionId = candidate.DefinitionId,
                    UserConfirmed = true, BinaryExplicitlyAllowed = candidate.AllowBinary
                });
                existingFiles.Add(path);
                continue;
            }

            var registrySource = new GameConfigSource
            {
                GameId = game.Id, Kind = GameConfigSourceKind.Registry, TargetPath = candidate.Path.Trim(),
                RegistryValueName = candidate.RegistryValueName.Trim(),
                DefinitionId = string.IsNullOrWhiteSpace(candidate.DefinitionId) ? "discovered-registry" : candidate.DefinitionId,
                UserConfirmed = true
            };
            if (!RegistryGameConfigBackend.IsAllowed(registrySource, out string registryReason))
                throw new InvalidOperationException(registryReason);
            string registryIdentity = registrySource.TargetPath + "|" + registrySource.RegistryValueName;
            if (existingRegistry.Contains(registryIdentity)) continue;
            if (_state.Games.Where(item => item.Id != game.Id).SelectMany(item => item.GameConfigSettings.Sources)
                .Any(item => item.Kind == GameConfigSourceKind.Registry &&
                    item.TargetPath.Equals(registrySource.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                    item.RegistryValueName.Equals(registrySource.RegistryValueName, StringComparison.OrdinalIgnoreCase)))
                throw new GameConfigConflictException("Это значение уже выбрано источником графики другой игры GPM.");
            newSources.Add(registrySource);
            existingRegistry.Add(registryIdentity);
        }

        if (newSources.Count == 0) return;
        foreach (GameConfigSource source in newSources) game.GameConfigSettings.Sources.Add(source);
        RefreshGameConfigStatus(game, "Список источников изменён. Создайте новый снимок.");
        SaveOnly(false);
    }

    public void AddGameConfigRegistrySource(GameEntry game, string keyPath, string valueName)
    {
        AddGameConfigCandidates(game, [new GameConfigCandidate(keyPath.Trim(), "manual-registry",
            "Значение HKCU добавлено по точному пути и имени.", SourceKind: GameConfigSourceKind.Registry,
            RegistryValueName: valueName.Trim())]);
    }

    public void RemoveGameConfigSource(GameEntry game, Guid sourceId)
    {
        EnsureGameConfigEditable(game);
        var source = game.GameConfigSettings.Sources.FirstOrDefault(item => item.Id == sourceId);
        if (source is null) return;
        if (_state.GameConfigSnapshots.Any(snapshot => snapshot.GameId == game.Id && snapshot.Items.Any(item => item.SourceId == sourceId)) ||
            _state.GameConfigOriginalBackups.Any(item => item.GameId == game.Id && item.SourceId == sourceId) ||
            _state.GameConfigSafetyCopies.Any(item => item.GameId == game.Id && item.SourceId == sourceId))
            throw new InvalidOperationException("Источник используется снимками или защитными копиями. Сначала экспортируйте данные и удалите связанные снимки; исходная копия будет сохранена.");
        game.GameConfigSettings.Sources.Remove(source);
        game.GameConfigSettings.LastAppliedHashes.Remove(sourceId);
        game.GameConfigSettings.LastAppliedExists.Remove(sourceId);
        RefreshGameConfigStatus(game, "Источник удалён.");
        SaveOnly(false);
    }

    public void EditGameConfigFileSource(GameEntry game, Guid sourceId, string selectedPath, bool allowBinary)
    {
        EnsureGameConfigEditable(game);
        var source = game.GameConfigSettings.Sources.FirstOrDefault(item => item.Id == sourceId && item.Kind == GameConfigSourceKind.File)
            ?? throw new InvalidOperationException("Файловый источник не найден.");
        if (!GameConfigPathSafety.ValidateCandidate(selectedPath, allowBinary, out string reason))
            throw new InvalidOperationException(reason);
        string path = Path.GetFullPath(selectedPath);
        if (!File.Exists(path)) throw new FileNotFoundException("Выбранный файл больше не существует.", path);
        if (path.Equals(source.TargetPath, StringComparison.OrdinalIgnoreCase)) return;
        bool collision = _state.Games.SelectMany(item => item.GameConfigSettings.Sources)
            .Any(item => !ReferenceEquals(item, source) && item.Kind == GameConfigSourceKind.File &&
                Path.GetFullPath(item.TargetPath).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (collision) throw new GameConfigConflictException("Этот файл уже используется другим источником GPM.");
        PrepareGameConfigSourceRebind(game, source);
        source.TargetPath = path;
        source.ApprovedRootPath = Path.GetDirectoryName(path)!;
        source.BinaryExplicitlyAllowed = allowBinary;
        source.DefinitionId = "manual-rebound";
        CompleteGameConfigSourceRebind(game, sourceId);
    }

    public void EditGameConfigRegistrySource(GameEntry game, Guid sourceId, string keyPath, string valueName)
    {
        EnsureGameConfigEditable(game);
        var source = game.GameConfigSettings.Sources.FirstOrDefault(item => item.Id == sourceId && item.Kind == GameConfigSourceKind.Registry)
            ?? throw new InvalidOperationException("Источник реестра не найден.");
        var candidate = new GameConfigSource
        {
            Id = source.Id, GameId = game.Id, Kind = GameConfigSourceKind.Registry,
            TargetPath = keyPath.Trim(), RegistryValueName = valueName.Trim(), UserConfirmed = true,
            DefinitionId = "manual-rebound"
        };
        if (!RegistryGameConfigBackend.IsAllowed(candidate, out string reason)) throw new InvalidOperationException(reason);
        if (source.TargetPath.Equals(candidate.TargetPath, StringComparison.OrdinalIgnoreCase) &&
            source.RegistryValueName.Equals(candidate.RegistryValueName, StringComparison.OrdinalIgnoreCase)) return;
        bool collision = _state.Games.SelectMany(item => item.GameConfigSettings.Sources)
            .Any(item => !ReferenceEquals(item, source) && item.Kind == GameConfigSourceKind.Registry &&
                item.TargetPath.Equals(candidate.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                item.RegistryValueName.Equals(candidate.RegistryValueName, StringComparison.OrdinalIgnoreCase));
        if (collision) throw new GameConfigConflictException("Это значение уже используется другим источником GPM.");
        PrepareGameConfigSourceRebind(game, source);
        source.TargetPath = candidate.TargetPath;
        source.RegistryValueName = candidate.RegistryValueName;
        source.DefinitionId = candidate.DefinitionId;
        CompleteGameConfigSourceRebind(game, sourceId);
    }

    private void PrepareGameConfigSourceRebind(GameEntry game, GameConfigSource source)
    {
        foreach (var backup in _state.GameConfigOriginalBackups.Where(item => item.GameId == game.Id && item.SourceId == source.Id))
        {
            if (string.IsNullOrWhiteSpace(backup.TargetPath)) backup.TargetPath = source.TargetPath;
            if (source.Kind == GameConfigSourceKind.Registry && string.IsNullOrWhiteSpace(backup.RegistryValueName))
                backup.RegistryValueName = source.RegistryValueName;
        }
        game.GameConfigSettings.DesiredGeneration++;
    }

    private void CompleteGameConfigSourceRebind(GameEntry game, Guid sourceId)
    {
        game.GameConfigSettings.AutoApply = false;
        game.GameConfigSettings.LastAppliedHashes.Remove(sourceId);
        game.GameConfigSettings.LastAppliedExists.Remove(sourceId);
        game.GameConfigSettings.LastAppliedSnapshotId = null;
        game.GameConfigSettings.Status = GameConfigApplyStatus.Ready;
        game.GameConfigSettings.StatusDetail = "Путь источника изменён. Автоматическое применение выключено; проверьте назначения и включите его вручную.";
        AddGameConfigEvent(game.GameConfigSettings, game.GameConfigSettings.Status, game.GameConfigSettings.StatusDetail);
        SaveOnly(false);
        NotifyGameConfigStateChanged();
    }

    public void AddGameConfigProcessPath(GameEntry game, string path)
    {
        EnsureGameConfigEditable(game);
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Укажите существующий EXE промежуточного лаунчера или процесса игры.");
        string full = Path.GetFullPath(path);
        if (!game.GameConfigSettings.AdditionalProcessPaths.Contains(full, StringComparer.OrdinalIgnoreCase))
            game.GameConfigSettings.AdditionalProcessPaths.Add(full);
        SaveOnly(false);
    }

    public void RemoveGameConfigProcessPath(GameEntry game, string path)
    {
        EnsureGameConfigEditable(game);
        game.GameConfigSettings.AdditionalProcessPaths.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        SaveOnly(false);
    }

    public async Task ExportGameConfigArchiveAsync()
    {
        if (!BeginActivity("Экспортируем снимки и защитные копии…")) return;
        string path = Path.Combine(_store.DirectoryPath, $"game-configs-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try
        {
            var error = await _syncCoordinator.ExecuteGameConfigAsync(async () =>
            {
                var state = AppStateSnapshot.Capture(_state);
                await Task.Run(async () => await _gameConfigArchiveService.ExportAsync(state, path));
            });
            if (error is not null) throw error;
            Notice = "Полный архив графических конфигураций сохранён: " + path;
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Экспорт графических конфигураций не завершён: " + ex.Message; }
        finally { EndActivity(); }
    }

    public async Task ImportGameConfigArchiveAsync(string archivePath)
    {
        if (!BeginActivity("Проверяем архив графических конфигураций…")) return;
        try
        {
            AppState? state = null;
            var error = await _syncCoordinator.ExecuteGameConfigAsync(async () =>
            {
                var operationState = AppStateSnapshot.Capture(_state);
                state = operationState;
                await Task.Run(async () => await _gameConfigArchiveService.ImportAsync(operationState, archivePath));
            });
            if (error is not null) throw error;
            if (state is null) throw new InvalidOperationException("Состояние импорта не удалось подготовить.");
            AppStateSnapshot.PublishResults(state, _state);
            SaveOnly(false);
            Notice = "Графические снимки и защитные данные импортированы. Игровые файлы не применялись; проверьте пути и включите автоматический режим вручную.";
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Импорт остановлен: " + ex.Message; }
        finally { EndActivity(); SaveOnly(false); NotifyGameConfigStateChanged(); }
    }

    public void SetGameConfigAutoApply(GameEntry game, bool enabled)
    {
        if (!CanEditGameConfigs(game)) return;
        if (game.GameConfigSettings.AutoApply == enabled) return;
        bool blockedBySafetyState = game.GameConfigSettings.Status is
            GameConfigApplyStatus.Conflict or GameConfigApplyStatus.RecoveryRequired;
        game.GameConfigSettings.AutoApply = enabled;
        if (!blockedBySafetyState)
        {
            game.GameConfigSettings.Status = game.GameConfigSettings.Sources.Count == 0
                ? GameConfigApplyStatus.NotConfigured : enabled ? GameConfigApplyStatus.Pending : GameConfigApplyStatus.Ready;
            game.GameConfigSettings.StatusDetail = enabled ? "Автоматическое применение графических файлов включено." : "Автоматическое применение графических файлов выключено.";
        }
        AddGameConfigEvent(game.GameConfigSettings, game.GameConfigSettings.Status, game.GameConfigSettings.StatusDetail);
        SaveOnly(false);
        NotifyGameConfigStateChanged();
        if (enabled) _ = ReconcileGameConfigsAsync();
    }

    public void SetGameConfigAssignment(GameEntry game, string displayId, Guid? snapshotId)
    {
        if (!CanEditGameConfigs(game)) return;
        var existing = _state.GameConfigAssignments.Where(item => item.GameId == game.Id &&
            item.DisplayId.Equals(displayId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (snapshotId is { } id && !_state.GameConfigSnapshots.Any(item => item.GameId == game.Id && item.Id == id))
            throw new InvalidOperationException("Снимок не принадлежит выбранной игре.");
        foreach (var item in existing) _state.GameConfigAssignments.Remove(item);
        if (snapshotId is { } value) _state.GameConfigAssignments.Add(new GameConfigAssignment
            { GameId = game.Id, DisplayId = displayId, SnapshotId = value });
        var target = GameConfigCoordinator.ResolveTarget(_state, game, _displays.ToArray());
        GameConfigCoordinator.SetDesired(game.GameConfigSettings, target);
        if (game.GameConfigSettings.Status is not (GameConfigApplyStatus.Conflict or GameConfigApplyStatus.RecoveryRequired))
        {
            if (game.GameConfigSettings.AutoApply && target.SnapshotId is not null)
                game.GameConfigSettings.Status = GameConfigApplyStatus.Pending;
            game.GameConfigSettings.StatusDetail = target.Reason;
        }
        AddGameConfigEvent(game.GameConfigSettings, game.GameConfigSettings.Status, game.GameConfigSettings.StatusDetail);
        SaveOnly(false);
        NotifyGameConfigStateChanged();
        _ = ReconcileGameConfigsAsync();
    }

    public async Task CreateGameConfigSnapshotAsync(GameEntry game, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) { Notice = "Введите название снимка."; return; }
        if (game.GameConfigSettings.Sources.Count == 0) { Notice = "Сначала выберите файлы графических настроек."; return; }
        var process = _gameProcessGuard.Check(game);
        if (process.State != GameProcessState.Closed)
        {
            game.GameConfigSettings.Status = GameConfigApplyStatus.WaitingForGameExit;
            game.GameConfigSettings.StatusDetail = process.Detail + " Снимок можно сохранить после закрытия игры.";
            AddGameConfigEvent(game.GameConfigSettings, game.GameConfigSettings.Status, game.GameConfigSettings.StatusDetail);
            SaveOnly(false);
            NotifyGameConfigStateChanged();
            Notice = game.GameConfigSettings.StatusDetail;
            return;
        }
        if (!BeginActivity("Сохраняем снимок графических настроек…")) return;
        try
        {
            var sources = game.GameConfigSettings.Sources.Select(CopyGameConfigSource).ToArray();
            var stateSnapshot = AppStateSnapshot.Capture(_state);
            var snapshotGame = stateSnapshot.Games.Single(item => item.Id == game.Id);
            GameConfigSnapshot? snapshot = null;
            var error = await _syncCoordinator.ExecuteGameConfigAsync(async () =>
                snapshot = await _gameConfigSnapshotService.CaptureAsync(snapshotGame, name, sources));
            if (error is not null) throw error;
            if (snapshot is null) throw new InvalidOperationException("Снимок не был создан.");
            _state.GameConfigSnapshots.Add(snapshot);
            game.GameConfigSettings.Status = GameConfigApplyStatus.Ready;
            game.GameConfigSettings.StatusDetail = $"Снимок «{snapshot.Name}» сохранён без изменения игровых файлов.";
            AddGameConfigEvent(game.GameConfigSettings, GameConfigApplyStatus.Ready, game.GameConfigSettings.StatusDetail);
            Notice = game.GameConfigSettings.StatusDetail;
        }
        catch (GameConfigDeferredException ex)
        {
            game.GameConfigSettings.Status = GameConfigApplyStatus.WaitingForGameExit;
            game.GameConfigSettings.StatusDetail = ex.Message;
            AddGameConfigEvent(game.GameConfigSettings, game.GameConfigSettings.Status, ex.Message);
            Notice = ex.Message;
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            game.GameConfigSettings.Status = GameConfigApplyStatus.Error;
            game.GameConfigSettings.StatusDetail = ex.Message;
            AddGameConfigEvent(game.GameConfigSettings, GameConfigApplyStatus.Error, ex.Message);
            Notice = "Снимок не сохранён: " + ex.Message;
        }
        finally { EndActivity(); SaveOnly(false); NotifyGameConfigStateChanged(); }
    }

    public void RenameGameConfigSnapshot(GameEntry game, Guid snapshotId, string name)
    {
        EnsureGameConfigEditable(game);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Введите название снимка.");
        var snapshot = _state.GameConfigSnapshots.FirstOrDefault(item => item.GameId == game.Id && item.Id == snapshotId)
            ?? throw new InvalidOperationException("Снимок не найден.");
        snapshot.Name = name.Trim();
        SaveOnly(false);
    }

    public void DeleteGameConfigSnapshot(GameEntry game, Guid snapshotId)
    {
        EnsureGameConfigEditable(game);
        if (_state.GameConfigAssignments.Any(item => item.GameId == game.Id && item.SnapshotId == snapshotId))
            throw new InvalidOperationException("Снимок назначен дисплею. Снимите назначения перед удалением.");
        var snapshot = _state.GameConfigSnapshots.FirstOrDefault(item => item.GameId == game.Id && item.Id == snapshotId);
        if (snapshot is null) return;
        _state.GameConfigSnapshots.Remove(snapshot);
        SaveOnly(false);
        try { _gameConfigStore.DeleteSnapshot(game.Id, snapshotId); }
        catch (Exception ex) { _store.Log(ex); Notice = "Метаданные удалены, файлы снимка оставлены для ручной очистки: " + ex.Message; }
        RefreshGameConfigStatus(game, "Снимок удалён.");
        _ = ReconcileGameConfigsAsync();
    }

    public async Task ApplyGameConfigSnapshotAsync(GameEntry game, Guid snapshotId, bool overrideConflict)
    {
        var snapshot = _state.GameConfigSnapshots.FirstOrDefault(item => item.GameId == game.Id && item.Id == snapshotId);
        if (snapshot is null) { Notice = "Снимок не найден."; return; }
        await RunGameConfigMutationAsync(game, "Применяем графическую конфигурацию…",
            (state, targetGame) => _gameConfigTransactions.ApplyAsync(state, targetGame,
                state.GameConfigSnapshots.Single(item => item.Id == snapshotId), overrideConflict));
    }

    public async Task RestoreOriginalGameConfigsAsync(GameEntry game, bool overrideConflict)
    {
        await RunGameConfigMutationAsync(game, "Восстанавливаем исходную графическую конфигурацию…",
            (state, targetGame) => _gameConfigTransactions.RestoreOriginalsAsync(state, targetGame, overrideConflict));
    }

    public async Task RecoverGameConfigTransactionAsync(string journalPath)
    {
        if (!File.Exists(journalPath) || !Path.GetFullPath(journalPath).StartsWith(
            Path.GetFullPath(_gameConfigStore.TransactionPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { Notice = "Журнал не найден."; return; }
        if (!BeginActivity("Восстанавливаем незавершённую файловую операцию…")) return;
        try
        {
            AppState? state = null;
            var error = await _syncCoordinator.ExecuteGameConfigAsync(async () =>
            {
                var operationState = AppStateSnapshot.Capture(_state);
                state = operationState;
                await _gameConfigTransactions.RecoverAsync(operationState, journalPath);
            });
            if (state is null) throw new InvalidOperationException("Состояние восстановления не удалось подготовить.");
            AppStateSnapshot.PublishResults(state, _state);
            if (error is not null) throw error;
            Notice = "Журнал проверен. Безопасно восстановленные изменения сохранены.";
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Восстановление остановлено: " + ex.Message; }
        finally { EndActivity(); SaveOnly(false); NotifyGameConfigStateChanged(); }
    }

    private async Task RunGameConfigMutationAsync(GameEntry game, string activity, Func<AppState, GameEntry, Task> operation)
    {
        if (!BeginActivity(activity)) return;
        try
        {
            SaveOnly(false, duringSync: true);
            AppState? state = null;
            var error = await _syncCoordinator.ExecuteGameConfigAsync(async () =>
            {
                var operationState = AppStateSnapshot.Capture(_state);
                state = operationState;
                var targetGame = operationState.Games.Single(item => item.Id == game.Id);
                targetGame.GameConfigSettings.Status = GameConfigApplyStatus.Applying;
                targetGame.GameConfigSettings.StatusDetail = activity;
                await operation(operationState, targetGame);
            });
            if (state is null) throw new InvalidOperationException("Состояние игры не удалось подготовить.");
            AppStateSnapshot.PublishResults(state, _state);
            if (error is not null) throw error;
            Notice = game.GameConfigSettings.StatusDetail;
        }
        catch (GameConfigDeferredException ex)
        {
            game.GameConfigSettings.Status = GameConfigApplyStatus.WaitingForGameExit;
            game.GameConfigSettings.StatusDetail = ex.Message;
            Notice = ex.Message;
        }
        catch (GameConfigConflictException ex)
        {
            game.GameConfigSettings.Status = GameConfigApplyStatus.Conflict;
            game.GameConfigSettings.StatusDetail = ex.Message;
            AddGameConfigEvent(game.GameConfigSettings, GameConfigApplyStatus.Conflict, ex.Message);
            Notice = "Конфликт графических файлов: " + ex.Message;
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            game.GameConfigSettings.Status = _gameConfigTransactions.IncompleteTransactions().Count > 0
                ? GameConfigApplyStatus.RecoveryRequired : GameConfigApplyStatus.Error;
            game.GameConfigSettings.StatusDetail = ex.Message;
            AddGameConfigEvent(game.GameConfigSettings, game.GameConfigSettings.Status, ex.Message);
            Notice = "Графическая конфигурация не изменена полностью: " + ex.Message;
        }
        finally { EndActivity(); SaveOnly(false); NotifyGameConfigStateChanged(); }
    }

    private async Task ReconcileGameConfigsAsync(bool forceCheck = false)
    {
        if (_disposed) return;
        if (!await _gameConfigReconcileGate.WaitAsync(0))
        {
            Interlocked.Exchange(ref _gameConfigReconcileRequested, 1);
            return;
        }
        try
        {
            do
            {
                Interlocked.Exchange(ref _gameConfigReconcileRequested, 0);
                foreach (var game in Games.ToArray())
                {
                    if (_disposed) return;
                    await ReconcileOneGameConfigAsync(game, forceCheck);
                    forceCheck = false;
                }
            } while (Interlocked.Exchange(ref _gameConfigReconcileRequested, 0) != 0);
        }
        finally
        {
            _gameConfigReconcileGate.Release();
            if (Interlocked.Exchange(ref _gameConfigReconcileRequested, 0) != 0 && !_disposed)
                _ = ReconcileGameConfigsAsync();
        }
    }

    private async Task ReconcileOneGameConfigAsync(GameEntry game, bool forceCheck)
    {
        var settings = game.GameConfigSettings;
        var target = GameConfigCoordinator.ResolveTarget(_state, game, _displays.ToArray());
        string previousDisplay = settings.DesiredDisplayId;
        Guid? previousSnapshot = settings.DesiredSnapshotId;
        long previousGeneration = settings.DesiredGeneration;
        GameConfigCoordinator.SetDesired(settings, target);
        if (settings.DesiredGeneration != previousGeneration || target.DisplayId != previousDisplay || target.SnapshotId != previousSnapshot)
            SaveOnly(false);
        if (settings.Status == GameConfigApplyStatus.Conflict &&
            _gameConfigTransactions.CurrentStateMatchesSavedSnapshot(_state, game))
        {
            settings.Status = settings.AutoApply && target.IsResolved && target.SnapshotId is not null
                ? GameConfigApplyStatus.Pending : GameConfigApplyStatus.Ready;
            settings.StatusDetail = "Текущие файлы совпадают с одним из сохранённых снимков; конфликт снят.";
            AddGameConfigEvent(settings, settings.Status, settings.StatusDetail);
            SaveOnly(false);
        }
        NotifyGameConfigStateChanged();
        if (!target.IsResolved || !settings.AutoApply || target.SnapshotId is not { } snapshotId ||
            settings.Status is GameConfigApplyStatus.Conflict or GameConfigApplyStatus.RecoveryRequired)
            return;
        if (!forceCheck && settings.LastAppliedSnapshotId == snapshotId && settings.Status == GameConfigApplyStatus.Applied) return;
        if (_gameConfigTransactions.IncompleteTransactions().Count > 0)
        {
            settings.Status = GameConfigApplyStatus.RecoveryRequired;
            settings.StatusDetail = "Восстановите незавершённую транзакцию перед автоматическим переключением.";
            SaveOnly(false);
            NotifyGameConfigStateChanged();
            return;
        }
        var process = _gameProcessGuard.Check(game);
        if (process.State != GameProcessState.Closed)
        {
            settings.Status = GameConfigApplyStatus.WaitingForGameExit;
            settings.StatusDetail = process.Detail;
            NotifyGameConfigStateChanged();
            return;
        }
        long generation = settings.DesiredGeneration;
        if (_state.GameConfigSnapshots.All(item => item.GameId != game.Id || item.Id != snapshotId))
        {
            settings.Status = GameConfigApplyStatus.Error;
            settings.StatusDetail = "Назначенный снимок отсутствует.";
            NotifyGameConfigStateChanged();
            return;
        }
        settings.Status = GameConfigApplyStatus.Pending;
        settings.StatusDetail = "Графический снимок ожидает применения.";
        settings.Status = GameConfigApplyStatus.Applying;
        settings.StatusDetail = "Графический снимок ожидает общей очереди операций.";
        NotifyGameConfigStateChanged();
        bool operationStarted = false;
        AppState? snapshotState = null;
        var error = await _syncCoordinator.ExecuteGameConfigAsync(async () =>
        {
            if (_disposed || !Games.Contains(game) || game.GameConfigSettings.DesiredGeneration != generation ||
                game.GameConfigSettings.DesiredSnapshotId != snapshotId || !game.GameConfigSettings.AutoApply) return;
            var operationState = AppStateSnapshot.Capture(_state);
            var operationGame = operationState.Games.Single(item => item.Id == game.Id);
            var operationSnapshot = operationState.GameConfigSnapshots.FirstOrDefault(item => item.GameId == game.Id && item.Id == snapshotId);
            if (operationSnapshot is null)
            {
                game.GameConfigSettings.Status = GameConfigApplyStatus.Error;
                game.GameConfigSettings.StatusDetail = "Назначенный снимок отсутствует.";
                NotifyGameConfigStateChanged();
                return;
            }
            snapshotState = operationState;
            operationStarted = true;
            await _gameConfigTransactions.ApplyAsync(operationState, operationGame, operationSnapshot, overrideConflict: false);
        });
        if (!operationStarted)
        {
            if (game.GameConfigSettings.Status == GameConfigApplyStatus.Applying)
            {
                game.GameConfigSettings.Status = GameConfigApplyStatus.Pending;
                game.GameConfigSettings.StatusDetail = "Назначение изменилось до начала операции.";
            }
            Interlocked.Exchange(ref _gameConfigReconcileRequested, 1);
            NotifyGameConfigStateChanged();
            return;
        }
        AppStateSnapshot.PublishResults(snapshotState!, _state);
        if (error is GameConfigDeferredException deferred)
        {
            game.GameConfigSettings.Status = GameConfigApplyStatus.WaitingForGameExit;
            game.GameConfigSettings.StatusDetail = deferred.Message;
        }
        else if (error is GameConfigConflictException conflict)
        {
            game.GameConfigSettings.Status = GameConfigApplyStatus.Conflict;
            game.GameConfigSettings.StatusDetail = conflict.Message;
            AddGameConfigEvent(game.GameConfigSettings, GameConfigApplyStatus.Conflict, conflict.Message);
        }
        else if (error is not null)
        {
            _store.Log(error);
            game.GameConfigSettings.Status = _gameConfigTransactions.IncompleteTransactions().Count > 0
                ? GameConfigApplyStatus.RecoveryRequired : GameConfigApplyStatus.Error;
            game.GameConfigSettings.StatusDetail = error.Message;
            AddGameConfigEvent(game.GameConfigSettings, game.GameConfigSettings.Status, error.Message);
        }
        SaveOnly(false);
        NotifyGameConfigStateChanged();
    }

    private void PollPendingGameConfigs()
    {
        if (Games.Any(game => game.GameConfigSettings.AutoApply &&
            game.GameConfigSettings.Status == GameConfigApplyStatus.WaitingForGameExit))
            _ = ReconcileGameConfigsAsync();
    }

    private void RefreshGameConfigStatus(GameEntry game, string message)
    {
        game.GameConfigSettings.Status = game.GameConfigSettings.Sources.Count == 0
            ? GameConfigApplyStatus.NotConfigured : GameConfigApplyStatus.Ready;
        game.GameConfigSettings.StatusDetail = message;
        AddGameConfigEvent(game.GameConfigSettings, game.GameConfigSettings.Status, message);
    }

    private static GameConfigSource CopyGameConfigSource(GameConfigSource item) => new()
    {
        Id = item.Id, GameId = item.GameId, Kind = item.Kind, TargetPath = item.TargetPath,
        ApprovedRootPath = item.ApprovedRootPath, RegistryValueName = item.RegistryValueName,
        DefinitionId = item.DefinitionId, UserConfirmed = item.UserConfirmed,
        BinaryExplicitlyAllowed = item.BinaryExplicitlyAllowed
    };

    private static void AddGameConfigEvent(GameConfigSettings settings, GameConfigApplyStatus status, string message)
    {
        settings.Events.Add(new GameConfigEvent { Status = status, Message = message });
        if (settings.Events.Count > 100) settings.Events.RemoveRange(0, settings.Events.Count - 100);
    }

    private void EnsureGameConfigEditable(GameEntry game)
    {
        if (!CanEditGameConfigs(game)) throw new InvalidOperationException(Notice);
    }

    private bool CanEditGameConfigs(GameEntry game)
    {
        if (_busy || game.GameConfigSettings.Status == GameConfigApplyStatus.Applying)
        {
            Notice = "Дождитесь завершения текущей операции с графическими файлами.";
            return false;
        }
        return true;
    }
}
