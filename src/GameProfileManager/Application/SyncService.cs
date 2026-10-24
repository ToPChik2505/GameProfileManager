using System.Diagnostics;
using GameProfileManager.Core;
using GameProfileManager.Integrations;

namespace GameProfileManager.Application;

public sealed class SyncService
{
    private readonly IStateStore _store;
    private readonly Func<string?, IRtssSettings> _rtssFactory;
    private readonly Func<INvidiaSettings> _nvidiaFactory;
    private readonly Func<IReadOnlyList<DisplayInfo>> _displayProvider;

    public SyncService(IStateStore store, Func<string?, IRtssSettings> rtssFactory,
        Func<INvidiaSettings> nvidiaFactory, Func<IReadOnlyList<DisplayInfo>>? displayProvider = null)
    {
        _store = store;
        _rtssFactory = rtssFactory;
        _nvidiaFactory = nvidiaFactory;
        _displayProvider = displayProvider ?? (() => []);
    }

    public string? NvidiaStatus { get; private set; }
    public string? RtssStatus { get; private set; }
    public bool RtssReady { get; private set; }

    public void CheckIntegrations(AppState state)
    {
        RtssReady = false;
        try
        {
            using var rtss = _rtssFactory(state.RtssDirectory);
            RtssReady = rtss.IsRunning;
            RtssStatus = (RtssReady ? "RTSS запущен: " : "RTSS закрыт: ") + rtss.InstallDirectory;
        }
        catch (Exception ex) { RtssStatus = "RTSS недоступен: " + ex.Message; }
        try { using var nvidia = _nvidiaFactory(); NvidiaStatus = "NVAPI DRS доступен"; }
        catch (Exception ex) { NvidiaStatus = "NVIDIA недоступна: " + ex.Message; }
    }

    public void CheckSaved(AppState state)
    {
        CheckSaved(state, ReadDisplays(), null);
    }

    /// <summary>Собирает диагностические факты только чтением и обновляет статусы в отделённом снимке.</summary>
    public DiagnosticsProbeSnapshot CheckDiagnostics(AppState state)
    {
        CheckIntegrations(state);
        var displays = ReadDisplays();
        var observations = new Dictionary<Guid, GameDiagnosticsReadback>();
        CheckSaved(state, displays, observations);
        return new DiagnosticsProbeSnapshot(RtssStatus, RtssReady, NvidiaStatus,
            NvidiaStatus?.StartsWith("NVAPI DRS доступен", StringComparison.OrdinalIgnoreCase) == true,
            displays.ToArray(), observations, DateTimeOffset.Now);
    }

    private void CheckSaved(AppState state, IReadOnlyList<DisplayInfo> displays,
        Dictionary<Guid, GameDiagnosticsReadback>? observations)
    {
        // Клиенты создаются только при первой допустимой игре и используются
        // в пределах одного прохода; каждый Read по-прежнему открывает свежий DRS-сеанс.
        var rtssClient = new Lazy<IRtssSettings>(() => _rtssFactory(state.RtssDirectory));
        var nvidiaClient = new Lazy<INvidiaSettings>(_nvidiaFactory);
        try
        {
        foreach (var game in state.Games)
        {
            var readback = new GameDiagnosticsReadback
            {
                ExecutableExists = !string.IsNullOrWhiteSpace(game.ExePath) && File.Exists(game.ExePath),
                DuplicateExecutableName = !string.IsNullOrWhiteSpace(game.ExePath) && HasDuplicateExeName(state, game)
            };
            observations?.Add(game.Id, readback);
            var resolution = ProfileResolver.Resolve(state, game, displays);
            if (!resolution.IsResolved)
            {
                if (game.AutoSwitchProfile)
                {
                    game.Status = ApplyStatus.Unsupported;
                    game.Detail = resolution.Reason + "; внешние настройки не изменены";
                }
                else
                {
                    game.Status = ApplyStatus.Pending;
                    game.Detail = state.Backups.Any(b => b.ManagedGameId == game.Id)
                        ? "Профиль отключён. Нажмите «Применить», чтобы восстановить исходные параметры."
                        : "Профиль не выбран: параметры не применяются";
                }
                continue;
            }
            var profile = resolution.Profile!;
            if (profile is null || string.IsNullOrWhiteSpace(game.ExePath))
            {
                game.Status = ApplyStatus.Pending;
                game.Detail = state.Backups.Any(b => b.ManagedGameId == game.Id)
                    ? "Профиль отключён. Нажмите «Применить», чтобы восстановить исходные параметры."
                    : "Профиль не выбран: параметры не применяются";
                continue;
            }
            if (!readback.ExecutableExists) { game.Status = ApplyStatus.Error; game.Detail = "Файл игры не найден"; continue; }
            if (readback.DuplicateExecutableName)
            { game.Status = ApplyStatus.Unsupported; game.Detail = "Несколько записей используют одно имя .exe в RTSS"; continue; }
            var fps = ResolveFps(state, game, profile, displays, resolution);
            readback.ExpectedRtssFps = fps.Fps;
            if (fps.Fps is null) { game.Status = ApplyStatus.Unsupported; game.Detail = fps.Detail; continue; }
            var limiterMode = RtssLimiterModeResolver.Resolve(state, profile);
            if (!Enum.IsDefined(limiterMode))
            { game.Status = ApplyStatus.Error; game.Detail = "Некорректный режим лимитера RTSS"; continue; }
            readback.ExpectedRtssLimiterMode = (int)limiterMode;

            bool rtssMatched = false, nvidiaMatched = false, rtssAvailable = false, nvidiaAvailable = false;
            string rtssDetail = "", nvidiaDetail = "";
            try
            {
                var rtss = rtssClient.Value;
                readback.RtssAvailable = rtss.IsRunning;
                if (!rtss.IsRunning) rtssDetail = "RTSS закрыт; лимит FPS сейчас не действует";
                else
                {
                    rtssAvailable = true;
                    string exeName = Path.GetFileName(game.ExePath);
                    bool exists = rtss.ProfileExists(exeName);
                    int? actualFps = exists ? rtss.ReadFps(exeName) : null;
                    readback.RtssProfileExists = exists;
                    readback.ActualRtssFps = actualFps;
                    bool fpsMatched = exists && actualFps == fps.Fps;
                    int? actualLimiterMode = exists ? rtss.ReadLimiterMode(exeName) : null;
                    readback.ActualRtssLimiterMode = actualLimiterMode;
                    bool limiterMatched = exists && actualLimiterMode == (int)limiterMode;
                    rtssMatched = fpsMatched && limiterMatched;
                    string limiterDifference = "режим лимитера отличается (ожидается " +
                        ProfileLabels.Rtss(limiterMode) + ", прочитано " +
                        (actualLimiterMode is int actual ? ProfileLabels.Rtss((RtssLimiterMode)actual) : "нет данных") + ")";
                    rtssDetail = rtssMatched ? "лимит FPS и режим подтверждены" : !exists ? "профиль игры отсутствует" :
                        !fpsMatched && !limiterMatched ? "лимит FPS отличается; " + limiterDifference :
                        !fpsMatched ? "лимит FPS отличается" : limiterDifference;
                }
            }
            catch (Exception ex) { readback.RtssAvailable = false; rtssDetail = ex.Message; _store.Log(ex); }
            try
            {
                var nvidia = nvidiaClient.Value;
                var values = nvidia.Read(game.ExePath);
                nvidiaAvailable = true;
                readback.NvidiaAvailable = true;
                nvidiaMatched = nvidia.Matches(values, profile);
                readback.NvidiaMatched = nvidiaMatched;
                readback.ActualNvidiaVrr = values.Vrr;
                readback.NvidiaVrrExplicit = values.VrrExplicit;
                readback.ActualNvidiaVSync = values.VSync;
                readback.NvidiaVSyncExplicit = values.VSyncExplicit;
                readback.ActualNvidiaFpsLimit = values.Fps;
                readback.NvidiaFpsLimitExplicit = values.FpsExplicit;
                nvidiaDetail = nvidiaMatched ? "подтверждён" : "отличается";
            }
            catch (Exception ex) { readback.NvidiaAvailable = false; nvidiaDetail = ex.Message; _store.Log(ex); }
            readback.GameProcessRunning = IsGameRunning(game.ExePath);
            readback.RestartRequired = game.Status == ApplyStatus.RestartRequired && readback.GameProcessRunning;
            game.Status = !rtssAvailable || !nvidiaAvailable ? ApplyStatus.Unsupported :
                rtssMatched && nvidiaMatched ? ApplyStatus.Applied : ApplyStatus.Pending;
            game.Detail = $"RTSS: {rtssDetail}; NVIDIA: {nvidiaDetail}" +
                (game.Status == ApplyStatus.Applied ? "; работа VRR в игре не измерена" : "");
        }
        }
        finally
        {
            if (nvidiaClient.IsValueCreated) nvidiaClient.Value.Dispose();
            if (rtssClient.IsValueCreated) rtssClient.Value.Dispose();
        }
    }

    public void ApplyAll(AppState state)
    {
        var displays = ReadDisplays();
        foreach (var game in state.Games) ApplyCore(state, game, displays);
        state.LastSync = DateTimeOffset.Now;
        _store.Save(state);
    }

    public void ApplyOne(AppState state, GameEntry game)
    {
        if (!state.Games.Contains(game)) throw new ArgumentException("Игра отсутствует в библиотеке.", nameof(game));
        ApplyCore(state, game, ReadDisplays());
        state.LastSync = DateTimeOffset.Now;
        _store.Save(state);
    }

    private IReadOnlyList<DisplayInfo> ReadDisplays()
    {
        try { return _displayProvider(); }
        catch (Exception ex) { _store.Log(ex); return []; }
    }

    private void ApplyCore(AppState state, GameEntry game, IReadOnlyList<DisplayInfo> displays)
    {
        var resolution = ProfileResolver.Resolve(state, game, displays);
        if (!resolution.IsResolved)
        {
            // An unresolved automatic display choice must never restore or rewrite the currently active settings.
            if (game.AutoSwitchProfile)
            {
                game.Status = ApplyStatus.Unsupported;
                game.Detail = resolution.Reason + "; текущие внешние настройки оставлены без изменений";
            }
            else if (state.Backups.Any(b => b.ManagedGameId == game.Id)) Restore(state, game);
            else { game.Status = ApplyStatus.Pending; game.Detail = resolution.Reason; }
            return;
        }
        var profile = resolution.Profile!;
        if (string.IsNullOrWhiteSpace(game.ExePath)) { game.Status = ApplyStatus.Pending; game.Detail = "Выберите игровой .exe"; return; }
        if (!File.Exists(game.ExePath)) { game.Status = ApplyStatus.Error; game.Detail = "Файл игры не найден"; return; }
        if (HasDuplicateExeName(state, game))
        { game.Status = ApplyStatus.Unsupported; game.Detail = "RTSS сопоставляет профиль по имени .exe; обнаружено совпадение у разных игр"; return; }
        if (string.IsNullOrWhiteSpace(profile.Name))
        { game.Status = ApplyStatus.Error; game.Detail = "Некорректный профиль"; return; }
        var fps = ResolveFps(state, game, profile, displays, resolution);
        if (fps.Fps is not int targetFps)
        {
            // A selected profile still owns its captured originals. A transient display failure
            // is not a request to restore them or to touch either external provider.
            game.Status = ApplyStatus.Unsupported;
            game.Detail = fps.Detail + "; текущие внешние настройки оставлены без изменений";
            return;
        }
        var limiterMode = RtssLimiterModeResolver.Resolve(state, profile);
        if (!Enum.IsDefined(limiterMode))
        { game.Status = ApplyStatus.Error; game.Detail = "Некорректный режим лимитера RTSS"; return; }

        IRtssSettings? rtss = null;
        INvidiaSettings? nvidia = null;
        SettingBackup? snapshot = null;
        SettingBackup? backup = null;
        bool createdBackup = false, rtssAttempted = false, nvidiaAttempted = false;
        DateTimeOffset? previousApplied = game.LastApplied;
        try
        {
            string exeName = Path.GetFileName(game.ExePath);
            rtss = _rtssFactory(state.RtssDirectory);
            rtss.ProbeWrite(exeName, targetFps);
            bool rtssExisted = rtss.ProfileExists(exeName);
            int? previousFps = rtss.ReadFps(exeName);
            int? previousLimiterMode = rtss.ReadLimiterMode(exeName);
            if (rtssExisted && previousFps is null)
                throw new NotSupportedException("Исходный FPS RTSS неизвестен; запись заблокирована.");
            if (rtssExisted && previousLimiterMode is null)
                throw new NotSupportedException("Исходный режим лимитера RTSS неизвестен; запись заблокирована.");

            nvidia = _nvidiaFactory();
            nvidia.ProbeWrite();
            var previousNvidia = nvidia.Read(game.ExePath);
            // Совпадающие значения уже подтверждены чтением обеих интеграций.
            // Не создаём новый бэкап и не записываем профиль повторно.
            if (game.LastApplied is not null &&
                state.Backups.Any(saved => saved.ManagedGameId == game.Id &&
                    saved.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase)) &&
                rtssExisted && previousFps == targetFps && previousLimiterMode == (int)limiterMode &&
                nvidia.Matches(previousNvidia, profile))
            {
                bool restartRequired = game.Status == ApplyStatus.RestartRequired && IsGameRunning(game.ExePath);
                game.Status = restartRequired ? ApplyStatus.RestartRequired : ApplyStatus.Applied;
                game.Detail = "RTSS и NVIDIA: значения совпадают при чтении; " + fps.Detail +
                    (restartRequired ? "; перезапустите игру" : "; работа VRR в игре не измерена");
                return;
            }
            snapshot = Snapshot(game, game.ExePath, rtssExisted, previousFps, previousLimiterMode, previousNvidia);

            backup = SettingBackupOwnership.FindForGame(state, game);
            if (backup is not null && !SettingBackupOwnership.SameExecutablePath(backup.ExePath, game.ExePath))
                throw new InvalidOperationException("Путь игры изменён, но исходная резервная копия относится к прежнему EXE. Сначала восстановите прежние значения.");
            if (backup is null)
            {
                backup = new SettingBackup { ExePath = game.ExePath, ManagedGameId = game.Id };
                state.Backups.Add(backup);
                createdBackup = true;
            }
            else if (backup.ManagedGameId == Guid.Empty)
                backup.ManagedGameId = game.Id;
            if (!backup.RtssCaptured) CopyRtss(snapshot, backup);
            else if (backup.RtssProfileExisted && backup.RtssSyncLimiter is null)
                backup.RtssSyncLimiter = previousLimiterMode; // Старые бэкапы не содержат этот параметр.
            if (!backup.NvidiaCaptured) CopyNvidia(snapshot, backup);
            _store.Save(state); // Исходные значения обеих интеграций сохраняются до внешней записи.

            rtssAttempted = true;
            rtss.WriteSettings(exeName, targetFps, limiterMode);
            nvidiaAttempted = true;
            nvidia.Apply(game, profile);

            bool running = IsGameRunning(game.ExePath);
            game.Status = running ? ApplyStatus.RestartRequired : ApplyStatus.Applied;
            game.Detail = $"RTSS и NVIDIA: сохранено и проверено; {fps.Detail}" + (running ? "; перезапустите игру" : "");
            game.LastApplied = DateTimeOffset.Now;
            _store.Save(state);
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            var rollbackErrors = snapshot is null ? [] : Rollback(game.ExePath, snapshot, rtss, nvidia, rtssAttempted, nvidiaAttempted);
            if (createdBackup && rollbackErrors.Count == 0 && backup is not null) state.Backups.Remove(backup);
            game.LastApplied = previousApplied;
            game.Status = rtssAttempted || nvidiaAttempted || rollbackErrors.Count > 0 ? ApplyStatus.Error : FailureStatus(ex);
            game.Detail = rollbackErrors.Count > 0
                ? "Применение прервано: " + ex.Message + "; откат не завершён: " + string.Join("; ", rollbackErrors)
                : rtssAttempted || nvidiaAttempted
                    ? "Применение прервано: " + ex.Message + "; прежние значения восстановлены"
                    : "Проверка интеграций: " + ex.Message;
            _store.Save(state);
        }
        finally { nvidia?.Dispose(); rtss?.Dispose(); }
    }

    private static FpsResolution ResolveFps(AppState state, GameEntry game, PerformanceProfile profile,
        IReadOnlyList<DisplayInfo> displays, ProfileResolution resolution)
    {
        if (!game.AutoSwitchProfile || profile.FpsMode == FpsLimitMode.Manual)
            return DisplayFps.Resolve(state, game, profile, displays);
        if (resolution.Display is null)
            return new(null, null, null, "Для автоматического выбора профиля не определён дисплей");
        var selectedGame = new GameEntry { DisplayId = resolution.Display.Id };
        var selectedState = new AppState { DefaultDisplayId = resolution.Display.Id };
        return DisplayFps.Resolve(selectedState, selectedGame, profile, [resolution.Display]);
    }

    public void Restore(AppState state, GameEntry game)
    {
        var backup = SettingBackupOwnership.FindForGame(state, game)
            ?? throw new InvalidOperationException("Резервные значения для этой игры не найдены.");
        if (backup.ManagedGameId == Guid.Empty) backup.ManagedGameId = game.Id;
        IRtssSettings? rtss = null;
        INvidiaSettings? nvidia = null;
        SettingBackup? snapshot = null;
        bool rtssAttempted = false, nvidiaAttempted = false;
        try
        {
            bool rtssExisted = false;
            int? currentFps = null;
            int? currentLimiterMode = null;
            NvidiaSettingsSnapshot currentNvidia = default;
            string backupExePath = backup.ExePath;
            string exeName = Path.GetFileName(backupExePath);
            if (backup.RtssCaptured)
            {
                if (backup.RtssProfileExisted && backup.RtssFps is null)
                    throw new NotSupportedException("Исходный FPS RTSS неизвестен; восстановление заблокировано.");
                rtss = _rtssFactory(state.RtssDirectory);
                rtss.ProbeWrite(exeName, backup.RtssProfileExisted ? backup.RtssFps : null);
                rtssExisted = rtss.ProfileExists(exeName);
                currentFps = rtss.ReadFps(exeName);
                currentLimiterMode = rtss.ReadLimiterMode(exeName);
                if (rtssExisted && currentFps is null)
                    throw new NotSupportedException("Текущий FPS RTSS неизвестен; откат восстановления невозможен.");
                if (rtssExisted && currentLimiterMode is null)
                    throw new NotSupportedException("Текущий режим лимитера RTSS неизвестен; откат восстановления невозможен.");
            }
            if (backup.NvidiaCaptured)
            {
                nvidia = _nvidiaFactory();
                nvidia.ProbeWrite();
                currentNvidia = nvidia.Read(backupExePath);
            }
            snapshot = Snapshot(game, backupExePath, rtssExisted, currentFps, currentLimiterMode, currentNvidia);
            if (backup.RtssCaptured)
            {
                rtssAttempted = true;
                rtss!.Restore(exeName, backup.RtssProfileExisted, backup.RtssFps, backup.RtssSyncLimiter);
            }
            if (backup.NvidiaCaptured)
            {
                nvidiaAttempted = true;
                nvidia!.Restore(backupExePath, backup);
            }
            state.Backups.Remove(backup);
            game.Status = ApplyStatus.Pending;
            game.Detail = "Исходные значения восстановлены";
            _store.Save(state);
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            var rollbackErrors = snapshot is null ? [] : Rollback(backup.ExePath, snapshot, rtss, nvidia, rtssAttempted, nvidiaAttempted);
            if (!state.Backups.Contains(backup)) state.Backups.Add(backup);
            game.Status = rollbackErrors.Count > 0 || rtssAttempted || nvidiaAttempted ? ApplyStatus.Error : FailureStatus(ex);
            game.Detail = rollbackErrors.Count > 0
                ? "Восстановление прервано: " + ex.Message + "; откат не завершён: " + string.Join("; ", rollbackErrors)
                : rtssAttempted || nvidiaAttempted
                    ? "Восстановление прервано: " + ex.Message + "; прежние значения оставлены"
                    : "Проверка интеграций: " + ex.Message;
            _store.Save(state);
        }
        finally { nvidia?.Dispose(); rtss?.Dispose(); }
    }

    private List<string> Rollback(string exePath, SettingBackup snapshot, IRtssSettings? rtss, INvidiaSettings? nvidia,
        bool rtssAttempted, bool nvidiaAttempted)
    {
        var errors = new List<string>();
        if (nvidiaAttempted && nvidia is not null)
        {
            try { nvidia.Restore(exePath, snapshot); }
            catch (Exception ex) { errors.Add("NVIDIA: " + ex.Message); _store.Log(ex); }
        }
        if (rtssAttempted && rtss is not null)
        {
            try { rtss.Restore(Path.GetFileName(exePath), snapshot.RtssProfileExisted, snapshot.RtssFps, snapshot.RtssSyncLimiter); }
            catch (Exception ex) { errors.Add("RTSS: " + ex.Message); _store.Log(ex); }
        }
        return errors;
    }

    private static SettingBackup Snapshot(GameEntry game, string exePath, bool rtssExisted, int? rtssFps, int? rtssLimiterMode,
        NvidiaSettingsSnapshot nvidia)
    {
        return new SettingBackup
        {
            ExePath = exePath, ManagedGameId = game.Id,
            RtssProfileExisted = rtssExisted, RtssFps = rtssFps, RtssSyncLimiter = rtssLimiterMode, RtssCaptured = true,
            NvidiaProfileExisted = nvidia.ProfileExisted, NvidiaVrr = nvidia.Vrr, NvidiaVrrExplicit = nvidia.VrrExplicit,
            NvidiaVSync = nvidia.VSync, NvidiaVSyncExplicit = nvidia.VSyncExplicit,
            NvidiaFpsLimit = nvidia.Fps, NvidiaFpsLimitExplicit = nvidia.FpsExplicit, NvidiaCaptured = true
        };
    }

    private static void CopyRtss(SettingBackup source, SettingBackup target)
    {
        target.RtssProfileExisted = source.RtssProfileExisted;
        target.RtssFps = source.RtssFps;
        target.RtssSyncLimiter = source.RtssSyncLimiter;
        target.RtssCaptured = true;
    }

    private static void CopyNvidia(SettingBackup source, SettingBackup target)
    {
        target.NvidiaProfileExisted = source.NvidiaProfileExisted;
        target.NvidiaVrr = source.NvidiaVrr; target.NvidiaVrrExplicit = source.NvidiaVrrExplicit;
        target.NvidiaVSync = source.NvidiaVSync; target.NvidiaVSyncExplicit = source.NvidiaVSyncExplicit;
        target.NvidiaFpsLimit = source.NvidiaFpsLimit; target.NvidiaFpsLimitExplicit = source.NvidiaFpsLimitExplicit;
        target.NvidiaCaptured = true;
    }

    private static bool HasDuplicateExeName(AppState state, GameEntry game) => state.Games.Any(other =>
        other != game && !string.IsNullOrWhiteSpace(other.ExePath) &&
        Path.GetFileName(other.ExePath).Equals(Path.GetFileName(game.ExePath), StringComparison.OrdinalIgnoreCase));

    private static bool IsGameRunning(string exePath)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath));
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static ApplyStatus FailureStatus(Exception ex) => ex is NotSupportedException or FileNotFoundException or
        DirectoryNotFoundException or DllNotFoundException ? ApplyStatus.Unsupported : ApplyStatus.Error;

}
