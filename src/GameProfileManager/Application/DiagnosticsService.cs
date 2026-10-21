using GameProfileManager.Core;

namespace GameProfileManager.Application;

public enum DiagnosticSeverity { Error, Warning, Info, Ok }

public enum DiagnosticRule
{
    RtssUnavailable,
    NvidiaUnavailable,
    ExecutableMissing,
    DuplicateExecutableName,
    ProfileMissing,
    DisplayUnavailable,
    InvalidFpsFraction,
    DynamicRefresh,
    ProfilePending,
    InvalidManualFps,
    FixedRefreshRatio,
    RtssReadbackMismatch,
    NvidiaReadbackMismatch,
    RtssReadbackUnavailable,
    NvidiaReadbackUnavailable,
    AdditionalFpsLimiter,
    RestartRequired,
    OriginalSettingsPending,
    SignalRefreshApproximation,
    ReadbackConfirmed
}

public enum DiagnosticActionTarget { Settings, Game, ChooseExe }

/// <summary>Результат одного диагностического правила, включая основание и безопасное действие.</summary>
public sealed record DiagnosticResult(
    DiagnosticRule RuleId,
    Guid? GameId,
    string? GameName,
    string Subject,
    DiagnosticSeverity Severity,
    string Summary,
    string Details,
    string SuggestedAction,
    DiagnosticActionTarget Target,
    bool CanAutoFix);

public sealed record GameDiagnosticsReadback
{
    public bool ExecutableExists { get; init; }
    public bool DuplicateExecutableName { get; init; }
    public bool? RtssAvailable { get; set; }
    public bool? RtssProfileExists { get; set; }
    public int? ExpectedRtssFps { get; set; }
    public int? ActualRtssFps { get; set; }
    public int? ExpectedRtssLimiterMode { get; set; }
    public int? ActualRtssLimiterMode { get; set; }
    public bool? NvidiaAvailable { get; set; }
    public bool? NvidiaMatched { get; set; }
    public uint? ActualNvidiaFpsLimit { get; set; }
    public bool NvidiaFpsLimitExplicit { get; set; }
    public uint? ActualNvidiaVrr { get; set; }
    public bool NvidiaVrrExplicit { get; set; }
    public uint? ActualNvidiaVSync { get; set; }
    public bool NvidiaVSyncExplicit { get; set; }
    public bool RestartRequired { get; set; }
    public bool GameProcessRunning { get; set; }
}

public sealed record DiagnosticsProbeSnapshot(
    string? RtssStatus,
    bool RtssAvailable,
    string? NvidiaStatus,
    bool NvidiaAvailable,
    IReadOnlyList<DisplayInfo> Displays,
    IReadOnlyDictionary<Guid, GameDiagnosticsReadback> Games,
    DateTimeOffset CheckedAt);

public sealed record DiagnosticProfileSnapshot(Guid Id, int FpsLimit, FpsLimitMode FpsMode,
    int FpsDivisor, VrrMode Vrr, VSyncMode VSync);

public sealed record DiagnosticGameSnapshot(Guid Id, string Name, string ExePath, Guid? ProfileId,
    string DisplayId, string DisplayName, ApplyStatus Status, string Detail, DateTimeOffset? LastApplied, bool HasBackup, DiagnosticProfileSnapshot? Profile,
    GameDiagnosticsReadback? Readback);

public sealed record DiagnosticsSnapshot(string DefaultDisplayId, IReadOnlyList<DiagnosticGameSnapshot> Games,
    IReadOnlyList<DisplayInfo> Displays, string? RtssStatus, bool? RtssAvailable,
    string? NvidiaStatus, bool? NvidiaAvailable, DateTimeOffset? CheckedAt);

/// <summary>Формирует диагностические результаты только из переданного снимка; внешние клиенты не вызываются.</summary>
public static class DiagnosticsService
{
    public static DiagnosticsSnapshot Capture(AppState state, IReadOnlyList<DisplayInfo> displays,
        string? rtssStatus, bool? rtssAvailable, string? nvidiaStatus, bool? nvidiaAvailable,
        DiagnosticsProbeSnapshot? probe = null)
    {
        var probeGames = probe?.Games;
        var games = state.Games.Select(game =>
        {
            var profile = state.Profiles.FirstOrDefault(candidate => candidate.Id == game.ProfileId);
            string selectedDisplayId = !string.IsNullOrWhiteSpace(game.DisplayId) ? game.DisplayId : state.DefaultDisplayId;
            return new DiagnosticGameSnapshot(game.Id, game.Name, game.ExePath, game.ProfileId,
                game.DisplayId, DisplayNameCache.Get(state, selectedDisplayId), game.Status, game.Detail, game.LastApplied,
                SettingBackupOwnership.HasForGame(state, game),
                profile is null ? null : new(profile.Id, profile.FpsLimit, profile.FpsMode,
                    profile.FpsDivisor, profile.Vrr, profile.VSync),
                probeGames is not null && probeGames.TryGetValue(game.Id, out var readback) ? readback : null);
        }).ToArray();

        return new DiagnosticsSnapshot(state.DefaultDisplayId, games,
            probe?.Displays.ToArray() ?? displays.ToArray(),
            probe?.RtssStatus ?? rtssStatus, probe?.RtssAvailable ?? rtssAvailable,
            probe?.NvidiaStatus ?? nvidiaStatus,
            probe?.NvidiaAvailable ?? nvidiaAvailable, probe?.CheckedAt);
    }

    public static IReadOnlyList<DiagnosticResult> Analyze(DiagnosticsSnapshot snapshot)
    {
        var results = new List<DiagnosticResult>();
        if (snapshot.RtssAvailable == false)
            results.Add(new(DiagnosticRule.RtssUnavailable, null, null, "RTSS",
                DiagnosticSeverity.Error, "RTSS недоступен", snapshot.RtssStatus ?? "Состояние RTSS не подтверждено.",
                "Запустите RTSS или укажите путь к его установке, затем повторите проверку.",
                DiagnosticActionTarget.Settings, false));
        if (snapshot.NvidiaAvailable == false)
            results.Add(new(DiagnosticRule.NvidiaUnavailable, null, null, "NVIDIA DRS",
                DiagnosticSeverity.Error, "NVIDIA DRS недоступен", snapshot.NvidiaStatus ?? "Состояние NVIDIA DRS не подтверждено.",
                "Проверьте драйвер NVIDIA и повторите проверку.", DiagnosticActionTarget.Settings, false));

        var duplicateNames = snapshot.Games.Where(game => !string.IsNullOrWhiteSpace(game.ExePath))
            .GroupBy(game => Path.GetFileName(game.ExePath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var game in snapshot.Games)
            AnalyzeGame(snapshot, game, duplicateNames, results);

        return results;
    }

    private static void AnalyzeGame(DiagnosticsSnapshot snapshot, DiagnosticGameSnapshot game,
        HashSet<string> duplicateNames, List<DiagnosticResult> results)
    {
        string exeName = Path.GetFileName(game.ExePath);
        if (game.ProfileId is null)
        {
            if (game.HasBackup)
                Add(DiagnosticRule.OriginalSettingsPending, DiagnosticSeverity.Info,
                    "Исходные настройки ожидают восстановления", game.Detail,
                    "Откройте игру и выполните восстановление прежних параметров.", DiagnosticActionTarget.Game);
            return;
        }

        if (game.Status == ApplyStatus.Pending && game.Readback is null)
            Add(DiagnosticRule.ProfilePending, DiagnosticSeverity.Info,
                "Профиль ещё не проверен", game.Detail,
                "Выполните чтение параметров или примените профиль к игре.", DiagnosticActionTarget.Game);

        if (game.Profile is null)
        {
            Add(DiagnosticRule.ProfileMissing, DiagnosticSeverity.Error,
                "Назначенный профиль не найден", "Игра ссылается на отсутствующий профиль.",
                "Выберите существующий профиль игры.", DiagnosticActionTarget.Game);
            return;
        }

        if (string.IsNullOrWhiteSpace(game.ExePath))
        {
            Add(DiagnosticRule.ExecutableMissing, DiagnosticSeverity.Error,
                "Не выбран игровой .exe", "Для назначенного профиля не указан исполняемый файл.",
                "Выберите исполняемый файл игры.", DiagnosticActionTarget.ChooseExe);
            return;
        }

        if (game.Readback?.ExecutableExists == false ||
            game.Status == ApplyStatus.Error && game.Detail.Contains("Файл игры не найден", StringComparison.OrdinalIgnoreCase))
            Add(DiagnosticRule.ExecutableMissing, DiagnosticSeverity.Error,
                "Файл игры не найден", game.ExePath,
                "Укажите актуальный игровой .exe.", DiagnosticActionTarget.ChooseExe);

        if (duplicateNames.Contains(exeName) || game.Readback?.DuplicateExecutableName == true)
        {
            var paths = snapshot.Games.Where(other => Path.GetFileName(other.ExePath)
                    .Equals(exeName, StringComparison.OrdinalIgnoreCase))
                .Select(other => other.ExePath).Distinct(StringComparer.OrdinalIgnoreCase);
            Add(DiagnosticRule.DuplicateExecutableName, DiagnosticSeverity.Error,
                "Совпадает имя EXE в RTSS", string.Join(" · ", paths),
                "Выберите для одной из игр EXE с уникальным именем.", DiagnosticActionTarget.ChooseExe);
        }

        var profile = game.Profile;
        var appState = new AppState { DefaultDisplayId = snapshot.DefaultDisplayId };
        string selectedDisplayId = !string.IsNullOrWhiteSpace(game.DisplayId) ? game.DisplayId : snapshot.DefaultDisplayId;
        if (!string.IsNullOrWhiteSpace(selectedDisplayId))
            appState.DisplayNames[selectedDisplayId] = game.DisplayName;
        var gameEntry = new GameEntry { DisplayId = game.DisplayId };
        var profileEntry = new PerformanceProfile
        {
            FpsLimit = profile.FpsLimit, FpsMode = profile.FpsMode,
            FpsDivisor = profile.FpsDivisor, Vrr = profile.Vrr
        };
        var resolution = DisplayFps.Resolve(appState, gameEntry, profileEntry, snapshot.Displays);
        var chosenDisplay = DisplayFps.ChooseDisplay(appState, gameEntry, snapshot.Displays);
        if (profile.FpsMode == FpsLimitMode.Manual && profile.FpsLimit is < 1 or > 1000)
            Add(DiagnosticRule.InvalidManualFps, DiagnosticSeverity.Error,
                "Ручной лимит FPS задан неверно", $"Указано {profile.FpsLimit} FPS; допустимый диапазон — от 1 до 1000.",
                "Задайте корректный лимит FPS в профиле.", DiagnosticActionTarget.Game);
        bool hasSelectedDisplay = !string.IsNullOrWhiteSpace(game.DisplayId) ||
                                  !string.IsNullOrWhiteSpace(snapshot.DefaultDisplayId);
        if (hasSelectedDisplay && chosenDisplay is null ||
            profile.FpsMode == FpsLimitMode.DisplayFraction && snapshot.Displays.Count == 0)
        {
            Add(DiagnosticRule.DisplayUnavailable, DiagnosticSeverity.Error,
                "Выбранный монитор недоступен", hasSelectedDisplay
                    ? $"Монитор «{game.DisplayName}» отключён или недоступен. Его ID: {(!string.IsNullOrWhiteSpace(game.DisplayId) ? game.DisplayId : snapshot.DefaultDisplayId)}."
                    : "Windows не обнаружила экран для расчёта доли герцовки.",
                "Подключите выбранный экран либо выберите доступный экран в настройках.", DiagnosticActionTarget.Game);
        }
        else if (profile.FpsMode == FpsLimitMode.DisplayFraction && resolution.Fps is null &&
                 chosenDisplay?.DynamicRefresh == true)
        {
            Add(DiagnosticRule.DynamicRefresh, DiagnosticSeverity.Info,
                "Динамическая частота не даёт постоянную долю FPS", resolution.Detail,
                "Для расчёта доли выберите фиксированную частоту экрана или задайте FPS вручную.", DiagnosticActionTarget.Game);
        }
        else if (profile.FpsMode == FpsLimitMode.DisplayFraction && resolution.Fps is null)
        {
            Add(DiagnosticRule.InvalidFpsFraction, DiagnosticSeverity.Error,
                "Невозможно рассчитать долю герцовки", resolution.Detail,
                "Выберите доступную долю герцовки или задайте FPS вручную.", DiagnosticActionTarget.Game);
        }

        if (chosenDisplay is { DynamicRefresh: false } && profile.Vrr == VrrMode.FixedRefresh)
        {
            int? desiredFps = profile.FpsMode == FpsLimitMode.DisplayFraction
                ? resolution.Fps : profile.FpsLimit is >= 1 and <= 1000 ? profile.FpsLimit : null;
            if (desiredFps is int fps)
            {
                decimal refresh = chosenDisplay.NominalRefreshHz ?? chosenDisplay.RefreshHz;
                decimal ratio = refresh / fps;
                if (ratio != decimal.Truncate(ratio))
                    Add(DiagnosticRule.FixedRefreshRatio, DiagnosticSeverity.Warning,
                        "FPS не делит частоту экрана нацело",
                        $"{chosenDisplay.Name}: {refresh:0.###} Гц ÷ {fps} FPS = {ratio:0.###}. Для Fixed Refresh коэффициент не целый.",
                        "Проверьте совместимость выбранных FPS и фиксированной частоты.", DiagnosticActionTarget.Game);
                else if (chosenDisplay.NominalRefreshHz is int nominal && chosenDisplay.RefreshHz != nominal)
                    Add(DiagnosticRule.SignalRefreshApproximation, DiagnosticSeverity.Info,
                        "Расчёт использует номинальную частоту",
                        $"Сигнал {chosenDisplay.SignalRefreshLabel}; расчёт приложения использует номинальные {nominal} Гц. Коэффициент {ratio:0} целый.",
                        "Учитывайте небольшое отличие частоты сигнала от номинального режима.", DiagnosticActionTarget.Game);
            }
        }

        var readback = game.Readback;
        if (readback is not null)
        {
            if (snapshot.RtssAvailable == true && readback.RtssAvailable == false)
                Add(DiagnosticRule.RtssReadbackUnavailable, DiagnosticSeverity.Warning,
                    "Не удалось прочитать профиль RTSS игры", game.Detail,
                    "Проверьте доступность профиля игры в RTSS и повторите диагностику.", DiagnosticActionTarget.Game);
            if (snapshot.NvidiaAvailable == true && readback.NvidiaAvailable == false)
                Add(DiagnosticRule.NvidiaReadbackUnavailable, DiagnosticSeverity.Warning,
                    "Не удалось прочитать профиль NVIDIA этой игры", game.Detail,
                    "Проверьте DRS-профиль приложения и повторите диагностику.", DiagnosticActionTarget.Game);

            if (readback.RtssAvailable == true && readback.ExpectedRtssFps is int expectedFps &&
                (readback.RtssProfileExists != true || readback.ActualRtssFps != expectedFps ||
                 readback.ActualRtssLimiterMode != readback.ExpectedRtssLimiterMode))
                Add(DiagnosticRule.RtssReadbackMismatch, DiagnosticSeverity.Warning,
                    "RTSS отличается от назначенного профиля",
                    $"Ожидалось: {expectedFps} FPS, {LimiterName(readback.ExpectedRtssLimiterMode)}. Прочитано: " +
                    (readback.RtssProfileExists == true
                        ? $"{readback.ActualRtssFps?.ToString() ?? "нет данных"} FPS, {LimiterName(readback.ActualRtssLimiterMode)}."
                        : "профиль EXE в RTSS отсутствует."),
                    "Сверьте профиль RTSS и при необходимости примените назначенный профиль.", DiagnosticActionTarget.Game);

            if (readback.NvidiaAvailable == true && readback.NvidiaMatched == false)
                Add(DiagnosticRule.NvidiaReadbackMismatch, DiagnosticSeverity.Warning,
                    "NVIDIA DRS отличается от назначенного профиля",
                    $"Назначено: VRR {ProfileLabels.Vrr(profile.Vrr)}, V-Sync {ProfileLabels.VSync(profile.VSync)}, " +
                    "FRL_FPS = 0 (лимитер приложения в NVIDIA отключён). Прочитано из DRS: " +
                    $"VRR={ReadbackValue(readback.ActualNvidiaVrr, readback.NvidiaVrrExplicit)}, " +
                    $"V-Sync={ReadbackValue(readback.ActualNvidiaVSync, readback.NvidiaVSyncExplicit)}, " +
                    $"FRL_FPS={ReadbackValue(readback.ActualNvidiaFpsLimit, readback.NvidiaFpsLimitExplicit)}.",
                    "Проверьте статус игры и повторите применение, если это необходимо.", DiagnosticActionTarget.Game);

            if (readback.RtssAvailable == true && readback.ActualRtssFps is > 0 &&
                readback.NvidiaAvailable == true && readback.NvidiaFpsLimitExplicit && readback.ActualNvidiaFpsLimit is > 0)
                Add(DiagnosticRule.AdditionalFpsLimiter, DiagnosticSeverity.Warning,
                    "Подтверждены два FPS-лимитера",
                    $"RTSS сообщает {readback.ActualRtssFps} FPS, NVIDIA DRS явно хранит {readback.ActualNvidiaFpsLimit} FPS.",
                    "Проверьте назначенный профиль NVIDIA и устраните конфликт перед применением.", DiagnosticActionTarget.Game);

            if (game.Status == ApplyStatus.RestartRequired || readback.RestartRequired ||
                readback.GameProcessRunning && game.LastApplied is not null)
                Add(DiagnosticRule.RestartRequired, DiagnosticSeverity.Info,
                    "Проверьте, нужен ли перезапуск игры",
                    "Процесс игры запущен, а для неё сохранены параметры DRS. Если DRS менялся после запуска, может потребоваться перезапуск. Повторное чтение не подтверждает активацию настройки внутри игры.",
                    "Закройте и снова запустите игру вручную.", DiagnosticActionTarget.Game);

            if (readback.RtssAvailable == true && readback.RtssProfileExists == true &&
                readback.ExpectedRtssFps == readback.ActualRtssFps &&
                readback.ExpectedRtssLimiterMode == readback.ActualRtssLimiterMode &&
                readback.NvidiaAvailable == true && readback.NvidiaMatched == true &&
                game.Status == ApplyStatus.Applied)
                Add(DiagnosticRule.ReadbackConfirmed, DiagnosticSeverity.Ok,
                    "Прочитанные параметры совпадают", "RTSS и NVIDIA DRS вернули назначенные значения. Работа VRR внутри игры не проверялась.",
                    "", DiagnosticActionTarget.Game);
        }

        return;

        void Add(DiagnosticRule rule, DiagnosticSeverity severity, string summary, string details,
            string action, DiagnosticActionTarget target) =>
            results.Add(new DiagnosticResult(rule, game.Id, game.Name,
                string.IsNullOrWhiteSpace(game.Name) ? exeName : game.Name, severity, summary, details, action, target, false));
    }

    private static string LimiterName(int? value) => value is int mode && Enum.IsDefined((RtssLimiterMode)mode)
        ? ProfileLabels.Rtss((RtssLimiterMode)mode)
        : value is int unknown ? $"неизвестный режим ({unknown})" : "нет данных";

    private static string ReadbackValue(uint? value, bool explicitValue) => !explicitValue
        ? "не задано явно"
        : value is uint number ? $"{number} (0x{number:X8})" : "нет данных";
}
