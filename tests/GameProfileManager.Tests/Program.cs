using GameProfileManager;
using GameProfileManager.Application;
using GameProfileManager.Core;
using GameProfileManager.Infrastructure;
using GameProfileManager.Integrations;
using System.Text.Json;

string temp = Path.Combine(Path.GetTempPath(), "gpm-tests-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(temp);
    var controls = new ControllerActionMapper();
    static ControllerSnapshot Press(params ControllerButton[] buttons) =>
        new(buttons.Aggregate(0UL, (mask, button) => mask | 1UL << (int)button));
    Assert(controls.Update(Press(ControllerButton.South), 0).SequenceEqual([ControllerAction.Accept]) &&
           controls.Update(Press(ControllerButton.South), 100).Count == 0 &&
           controls.Update(default, 110).Count == 0 &&
           controls.Update(Press(ControllerButton.South), 120).SequenceEqual([ControllerAction.Accept]),
        "accept fires once per press and rearms after release");
    Assert(controls.Update(new ControllerSnapshot(0, LeftY: -20000), 200).Contains(ControllerAction.Up) &&
           controls.Update(new ControllerSnapshot(0, LeftY: -20000), 400).Count == 0 &&
           controls.Update(new ControllerSnapshot(0, LeftY: -20000), 540).Contains(ControllerAction.Up) &&
           controls.Update(Press(ControllerButton.DpadDown), 600).Contains(ControllerAction.Down),
        "stick and d-pad navigation repeat while held and change direction immediately");
    Assert(controls.Update(Press(ControllerButton.LeftShoulder, ControllerButton.RightShoulder), 700)
               .Contains(ControllerAction.NextPage) &&
           controls.Update(new ControllerSnapshot(0, RightTrigger: 26000), 710)
               .Contains(ControllerAction.PageDown),
        "shoulders switch pages and triggers scroll");
    var windowControls = new ControllerActionMapper();
    Assert(windowControls.Update(Press(ControllerButton.North), 800).SequenceEqual([ControllerAction.Tertiary]) &&
           windowControls.Update(default, 850).Count == 0 &&
           windowControls.Update(Press(ControllerButton.Start, ControllerButton.Back), 1000).Count == 0 &&
           windowControls.Update(Press(ControllerButton.Start, ControllerButton.Back), 1600).Count == 0 &&
           windowControls.Update(Press(ControllerButton.Start, ControllerButton.Back), 1700).SequenceEqual([ControllerAction.ToggleWindow]) &&
           windowControls.Update(Press(ControllerButton.Start, ControllerButton.Back), 1800).Count == 0 &&
           windowControls.Update(default, 1900).Count == 0 &&
           windowControls.Update(Press(ControllerButton.Start, ControllerButton.Back), 2000).Count == 0 &&
           windowControls.Update(Press(ControllerButton.Start, ControllerButton.Back), 2700).SequenceEqual([ControllerAction.ToggleWindow]),
        "minimize shortcut and held restore chord fire once until released");
    Console.WriteLine("PASS Xbox/DualSense action mapping and navigation repeat");
    string instanceIdentity = "GameProfileManager.Tests." + Guid.NewGuid().ToString("N");
    using var firstInstance = new SingleInstanceCoordinator(instanceIdentity);
    int activationSignals = 0;
    Assert(firstInstance.TryStart(() => Interlocked.Increment(ref activationSignals)),
        "first instance owns the process mutex");
    bool secondStarted = true;
    bool secondSignaled = false;
    var secondThread = new Thread(() =>
    {
        using var secondInstance = new SingleInstanceCoordinator(instanceIdentity);
        secondStarted = secondInstance.TryStart(() => { });
        secondSignaled = secondInstance.TrySignalExisting();
    });
    secondThread.Start();
    Assert(secondThread.Join(TimeSpan.FromSeconds(3)) && !secondStarted && secondSignaled &&
           SpinWait.SpinUntil(() => Volatile.Read(ref activationSignals) > 0, TimeSpan.FromSeconds(3)),
        "second instance is rejected and signals the first");
    firstInstance.Dispose();
    using var restartedInstance = new SingleInstanceCoordinator(instanceIdentity);
    Assert(restartedInstance.TryStart(() => { }), "mutex is released after the first instance exits");
    Console.WriteLine("PASS single instance mutex and activation signal");
    Assert(typeof(GameEntry).GetProperty("Icon") is null &&
           typeof(GameEntry).GetProperties().All(property => property.PropertyType.Namespace != "System.Windows.Media"),
        "domain game model has no WPF image dependency");
    Console.WriteLine("PASS domain model is independent from WPF images");

    var importedLibrary = new System.Collections.ObjectModel.ObservableCollection<GameEntry>();
    var manualGame = new GameEntry { Name = "Manual", ExePath = @"C:\\Games\\Sample.exe" };
    var steamDuplicate = new GameEntry { Name = "Steam duplicate", ExePath = manualGame.ExePath, SteamAppId = "1234" };
    importedLibrary.Add(manualGame);
    importedLibrary.Add(steamDuplicate);
    var steamImport = GameProfileManager.Application.GameImportService.MergeSteam(importedLibrary, [],
        [new GameEntry { Name = "Steam title", ExePath = manualGame.ExePath, SteamAppId = "1234" }]);
    Assert(steamImport.Removed == 1 && steamImport.Added == 0 && importedLibrary.Count == 1 &&
           importedLibrary[0] == manualGame && manualGame.SteamAppId == "1234",
        "Steam merge keeps manually added game without duplication");
    manualGame.ProfileId = Guid.NewGuid();
    var epicImport = GameProfileManager.Application.GameImportService.MergeEpic(importedLibrary,
        [new GameEntry { Name = "Epic title", ExePath = manualGame.ExePath, EpicAppName = "epic-sample" }]);
    Assert(epicImport.Existing == 1 && epicImport.Added == 0 && importedLibrary.Count == 1 &&
           manualGame.EpicAppName == "epic-sample" && manualGame.ProfileId.HasValue,
        "Epic merge preserves existing assignment and does not duplicate the EXE");
    Console.WriteLine("PASS Steam/Epic catalog merge without duplicate or profile loss");


    var store = new SqliteStateStore(Path.Combine(temp, "db"));
    var state = store.Load();
    var snapshotGame = new GameEntry { Name = "Снимок", ExePath = @"C:\Games\Snapshot.exe" };
    state.Games.Add(snapshotGame);
    state.Backups.Add(new SettingBackup { ExePath = snapshotGame.ExePath, ManagedGameId = snapshotGame.Id,
        RtssCaptured = true, RtssProfileExisted = true, RtssFps = 47, RtssSyncLimiter = 2,
        NvidiaCaptured = true, NvidiaVrr = 4, NvidiaVrrExplicit = true });
    var backgroundState = AppStateSnapshot.Capture(state);
    Assert(!ReferenceEquals(backgroundState.Games[0], snapshotGame) &&
           !ReferenceEquals(backgroundState.Profiles[0], state.Profiles[0]) &&
           !ReferenceEquals(backgroundState.Backups[0], state.Backups[0]) &&
           JsonSerializer.Serialize(backgroundState) == JsonSerializer.Serialize(state) &&
           backgroundState.Backups[0].RtssFps == 47 && backgroundState.Backups[0].RtssSyncLimiter == 2 &&
           backgroundState.Backups[0].NvidiaVrr == 4,
        "background snapshot detaches games, profiles and original integration backups");
    backgroundState.Games[0].Status = ApplyStatus.Applied;
    backgroundState.Games[0].Detail = "Проверено";
    backgroundState.Backups.Clear();
    Assert(snapshotGame.Status == ApplyStatus.Pending && state.Backups.Count == 1,
        "background changes do not touch UI models before publication");
    AppStateSnapshot.PublishResults(backgroundState, state);
    Assert(snapshotGame.Status == ApplyStatus.Applied && snapshotGame.Detail == "Проверено" &&
           state.Games[0] == snapshotGame && state.Backups.Count == 0,
        "completed results are published without replacing bound game instances");
    state.Games.Remove(snapshotGame);
    Console.WriteLine("PASS detached background state and UI publication");

    string exeA = Path.Combine(temp, "FirstGame.exe");
    string exeB = Path.Combine(temp, "SecondGame.exe");
    File.WriteAllBytes(exeA, [0x4D, 0x5A]);
    File.WriteAllBytes(exeB, [0x4D, 0x5A]);
    var displayA = new DisplayInfo("a", "Экран A", @"\\.\DISPLAY1", 2560, 1440,
        120000, 1000, true, "DisplayPort", false, false);

    AppState FixedRefreshState(int fps, VrrMode vrr = VrrMode.FixedRefresh, string? displayId = null)
    {
        var profile = new PerformanceProfile { FpsLimit = fps, Vrr = vrr };
        var result = new AppState { DefaultDisplayId = "a" };
        result.Profiles.Add(profile);
        result.Games.Add(new GameEntry
            { Name = "Ratio test", ExePath = exeA, ProfileId = profile.Id, DisplayId = displayId ?? "" });
        return result;
    }
    var fixed40 = FixedRefreshState(40);
    var fixed40Results = DiagnosticsService.Analyze(DiagnosticsService.Capture(fixed40, [displayA], "RTSS", true,
        "NVAPI DRS доступен", true));
    Assert(!fixed40Results.Any(result => result.RuleId is DiagnosticRule.FixedRefreshRatio or DiagnosticRule.AdditionalFpsLimiter),
        "120 Hz / 40 FPS is an integer ratio and no unobserved second limiter is invented");
    var fixed50 = FixedRefreshState(50);
    var fixed50Results = DiagnosticsService.Analyze(DiagnosticsService.Capture(fixed50, [displayA], "RTSS", true,
        "NVAPI DRS доступен", true));
    Assert(fixed50Results.Any(result => result.RuleId == DiagnosticRule.FixedRefreshRatio &&
                                       result.Severity == DiagnosticSeverity.Warning),
        "120 Hz / 50 FPS warns for a non-integer Fixed Refresh ratio");
    var gsync50 = FixedRefreshState(50, VrrMode.GSync);
    Assert(!DiagnosticsService.Analyze(DiagnosticsService.Capture(gsync50, [displayA], "RTSS", true,
            "NVAPI DRS доступен", true)).Any(result => result.RuleId == DiagnosticRule.FixedRefreshRatio),
        "G-SYNC does not receive the Fixed Refresh ratio warning");
    var nominalProfile = new PerformanceProfile { FpsMode = FpsLimitMode.DisplayFraction, FpsDivisor = 3,
        Vrr = VrrMode.FixedRefresh };
    var nominalState = new AppState { DefaultDisplayId = "a" };
    nominalState.Profiles.Add(nominalProfile);
    nominalState.Games.Add(new GameEntry { Name = "119,998 Hz", ExePath = exeA, ProfileId = nominalProfile.Id });
    var nominalResults = DiagnosticsService.Analyze(DiagnosticsService.Capture(nominalState,
        [displayA with { RefreshNumerator = 119998 }], "RTSS", true, "NVAPI DRS доступен", true));
    Assert(!nominalResults.Any(result => result.RuleId == DiagnosticRule.FixedRefreshRatio) &&
           nominalResults.Any(result => result.RuleId == DiagnosticRule.SignalRefreshApproximation &&
                                        result.Details.Contains("119,998 Гц")),
        "119.998 Hz uses the nominal 120 Hz rate and discloses signal precision");
    var dynamicProfile = new PerformanceProfile { FpsMode = FpsLimitMode.DisplayFraction, FpsDivisor = 3 };
    var dynamicState = new AppState { DefaultDisplayId = "a" };
    dynamicState.Profiles.Add(dynamicProfile);
    dynamicState.Games.Add(new GameEntry { Name = "Dynamic refresh", ExePath = exeA, ProfileId = dynamicProfile.Id });
    var dynamicResults = DiagnosticsService.Analyze(DiagnosticsService.Capture(dynamicState,
        [displayA with { DynamicRefresh = true }], "RTSS", true, "NVAPI DRS доступен", true));
    Assert(dynamicResults.Any(result => result.RuleId == DiagnosticRule.DynamicRefresh) &&
           !dynamicResults.Any(result => result.RuleId == DiagnosticRule.InvalidFpsFraction),
        "dynamic refresh is described separately from an invalid FPS fraction");
    var invalidFractionProfile = new PerformanceProfile { FpsMode = FpsLimitMode.DisplayFraction, FpsDivisor = 7 };
    var invalidFractionState = new AppState { DefaultDisplayId = "a" };
    invalidFractionState.Profiles.Add(invalidFractionProfile);
    invalidFractionState.Games.Add(new GameEntry { Name = "Invalid fraction", ExePath = exeA,
        ProfileId = invalidFractionProfile.Id });
    Assert(DiagnosticsService.Analyze(DiagnosticsService.Capture(invalidFractionState, [displayA],
                "RTSS", true, "NVAPI DRS доступен", true))
            .Any(result => result.RuleId == DiagnosticRule.InvalidFpsFraction && result.Severity == DiagnosticSeverity.Error),
        "a divisor that cannot produce an integer FPS receives a configuration error");

    string twinA = Path.Combine(temp, "one", "Twin.exe");
    string twinB = Path.Combine(temp, "two", "Twin.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(twinA)!);
    Directory.CreateDirectory(Path.GetDirectoryName(twinB)!);
    File.WriteAllBytes(twinA, [0x4D, 0x5A]);
    File.WriteAllBytes(twinB, [0x4D, 0x5A]);
    var diagnosticState = new AppState { DefaultDisplayId = "a" };
    var diagnosticProfile = new PerformanceProfile { FpsLimit = 60, Vrr = VrrMode.GSync };
    var offlineDisplayProfile = new PerformanceProfile { FpsMode = FpsLimitMode.DisplayFraction,
        FpsDivisor = 3, Vrr = VrrMode.GSync };
    diagnosticState.Profiles.Add(diagnosticProfile);
    diagnosticState.Profiles.Add(offlineDisplayProfile);
    var verifiedGame = new GameEntry { Name = "Verified", ExePath = exeA, ProfileId = diagnosticProfile.Id };
    var twinGameA = new GameEntry { Name = "Twin A", ExePath = twinA, ProfileId = diagnosticProfile.Id };
    var twinGameB = new GameEntry { Name = "Twin B", ExePath = twinB, ProfileId = diagnosticProfile.Id };
    var missingExe = new GameEntry { Name = "Missing", ExePath = Path.Combine(temp, "Missing.exe"), ProfileId = diagnosticProfile.Id };
    var offlineDisplayGame = new GameEntry { Name = "Offline display", ExePath = exeB,
        ProfileId = offlineDisplayProfile.Id, DisplayId = "disconnected" };
    diagnosticState.Games.Add(verifiedGame);
    diagnosticState.Games.Add(twinGameA);
    diagnosticState.Games.Add(twinGameB);
    diagnosticState.Games.Add(missingExe);
    diagnosticState.Games.Add(offlineDisplayGame);
    var diagnosticRtss = new FakeRtss { Exists = true, Fps = 59, LimiterMode = 1 };
    var diagnosticNvidia = new FakeNvidia
    {
        ProfileExists = true, Vrr = NvidiaClient.AllowGSync, VSync = NvidiaClient.VSyncOn, Fps = 120
    };
    var diagnosticSync = new SyncService(store, _ => diagnosticRtss, () => diagnosticNvidia, () => [displayA]);
    var diagnosticProbe = diagnosticSync.CheckDiagnostics(AppStateSnapshot.Capture(diagnosticState));
    var findings = DiagnosticsService.Analyze(DiagnosticsService.Capture(diagnosticState, [displayA],
        diagnosticProbe.RtssStatus, diagnosticProbe.RtssAvailable, diagnosticProbe.NvidiaStatus,
        diagnosticProbe.NvidiaAvailable, diagnosticProbe));
    Assert(findings.Any(result => result.RuleId == DiagnosticRule.ExecutableMissing && result.GameId == missingExe.Id) &&
           findings.Any(result => result.RuleId == DiagnosticRule.DuplicateExecutableName && result.GameId == twinGameA.Id) &&
           findings.Any(result => result.RuleId == DiagnosticRule.DisplayUnavailable && result.GameId == offlineDisplayGame.Id) &&
           findings.Any(result => result.RuleId == DiagnosticRule.AdditionalFpsLimiter && result.GameId == verifiedGame.Id) &&
           findings.Any(result => result.RuleId == DiagnosticRule.RtssReadbackMismatch && result.GameId == verifiedGame.Id) &&
           findings.Any(result => result.RuleId == DiagnosticRule.NvidiaReadbackMismatch && result.GameId == verifiedGame.Id),
        "diagnostics identify missing files, duplicate RTSS names, missing display and observed readback conflicts");
    Assert(diagnosticRtss.WriteCalls == 0 && diagnosticRtss.RestoreCalls == 0 &&
           diagnosticNvidia.ApplyCalls == 0 && diagnosticNvidia.RestoreCalls == 0,
        "diagnostic probe reads fake integrations without invoking writes, apply or restore");
    diagnosticRtss.Fps = 60;
    diagnosticNvidia.Fps = 0;
    var matchingState = AppStateSnapshot.Capture(diagnosticState);
    var matchingProbe = diagnosticSync.CheckDiagnostics(matchingState);
    var matchingResults = DiagnosticsService.Analyze(DiagnosticsService.Capture(matchingState, [displayA],
        matchingProbe.RtssStatus, matchingProbe.RtssAvailable, matchingProbe.NvidiaStatus,
        matchingProbe.NvidiaAvailable, matchingProbe));
    Assert(matchingResults.Any(result => result.RuleId == DiagnosticRule.ReadbackConfirmed &&
                                         result.GameId == verifiedGame.Id && result.Severity == DiagnosticSeverity.Ok),
        "confirmed RTSS and NVIDIA values produce an explicit OK diagnostic result");

    var unavailableSync = new SyncService(store, _ => new FakeRtss { Running = false },
        () => throw new InvalidOperationException("NVIDIA недоступна"), () => []);
    var unavailableProbe = unavailableSync.CheckDiagnostics(new AppState());
    var unavailableResults = DiagnosticsService.Analyze(DiagnosticsService.Capture(new AppState(), [],
        unavailableProbe.RtssStatus, unavailableProbe.RtssAvailable, unavailableProbe.NvidiaStatus,
        unavailableProbe.NvidiaAvailable, unavailableProbe));
    Assert(unavailableResults.Any(result => result.RuleId == DiagnosticRule.RtssUnavailable && result.Severity == DiagnosticSeverity.Error) &&
           unavailableResults.Any(result => result.RuleId == DiagnosticRule.NvidiaUnavailable && result.Severity == DiagnosticSeverity.Error),
        "RTSS and NVIDIA availability failures have separate error findings");
    Console.WriteLine("PASS typed read-only diagnostics, display rules and verified limiter conflicts");
    Assert(state.Profiles.Count == 3, "three starter profiles");
    Assert(state.RtssLimiterMode == RtssLimiterMode.FrontEdgeSync && (int)state.RtssLimiterMode == 1,
        "Front Edge Sync is the default RTSS limiter mode");
    Assert(state.Profiles.All(profile => profile.IsBuiltIn) && state.PresetProtectionMigrated,
        "new starter profiles are protected");
    Assert(state.Profiles.Select(p => p.Name).SequenceEqual(["Кинематографичный", "Сбалансированный", "Соревновательный"]), "starter profile names are Russian");
    Assert(ProfileLabels.Vrr(VrrMode.FixedRefresh) == "Фиксированная частота" &&
           ProfileLabels.VSync(VSyncMode.ApplicationControlled) == "Управляется игрой", "mode labels are Russian");
    var legacyState = new AppState();
    legacyState.Profiles.Add(new PerformanceProfile { Name = "Cinematic" });
    legacyState.Profiles.Add(new PerformanceProfile { Name = "Сбалансированный" });
    legacyState.Profiles.Add(new PerformanceProfile { Name = "Соревновательный" });
    legacyState.Profiles.Add(new PerformanceProfile { Name = "Пользовательский" });
    Assert(ProfilePresets.ProtectLegacy(legacyState) && legacyState.Profiles.Take(3).All(p => p.IsBuiltIn) &&
           !legacyState.Profiles[3].IsBuiltIn && !ProfilePresets.ProtectLegacy(legacyState),
        "legacy starter profiles are protected once without locking custom profiles");
    var editableCopy = ProfilePresets.EditableCopy(state.Profiles[0]);
    Assert(!editableCopy.IsBuiltIn && editableCopy.Id != state.Profiles[0].Id &&
           editableCopy.FpsLimit == state.Profiles[0].FpsLimit,
        "copy keeps settings but can be edited independently");
    var explicitSource = new PerformanceProfile { RtssLimiterMode = RtssLimiterMode.NvidiaReflex };
    Assert(ProfilePresets.EditableCopy(explicitSource).RtssLimiterMode == RtssLimiterMode.NvidiaReflex,
        "copy keeps the individual RTSS limiter mode");
    var oldJson = JsonSerializer.Deserialize<AppState>("""
        {"RtssLimiterMode":2,"Profiles":[{"Name":"Старый профиль"}]}
        """)!;
    Assert(oldJson.Profiles.Single().RtssLimiterMode is null &&
           RtssLimiterModeResolver.Resolve(oldJson, oldJson.Profiles.Single()) == RtssLimiterMode.BackEdgeSync,
        "a legacy JSON profile without the new field inherits the global RTSS mode");
    var oldJsonStore = new SqliteStateStore(Path.Combine(temp, "legacy-json"));
    oldJsonStore.Save(oldJson);
    var oldJsonLoaded = oldJsonStore.Load();
    Assert(oldJsonLoaded.Profiles.Single().RtssLimiterMode is null &&
           RtssLimiterModeResolver.Resolve(oldJsonLoaded, oldJsonLoaded.Profiles.Single()) == RtssLimiterMode.BackEdgeSync,
        "legacy profile inheritance survives the SQLite JSON roundtrip");
    var cinematic = state.Profiles.Single(p => p.Name == "Кинематографичный");
    var gameA = new GameEntry { Name = "A", ExePath = @"C:\Games\A.exe", ProfileId = cinematic.Id };
    var gameB = new GameEntry { Name = "B", ExePath = @"C:\Games\B.exe", ProfileId = cinematic.Id };
    state.Games.Add(gameA); state.Games.Add(gameB);
    cinematic.FpsLimit = 45;
    state.DefaultDisplayId = "display-b";
    state.MinimizeToTray = true;
    state.Theme = "Light";
    state.RtssLimiterMode = RtssLimiterMode.BackEdgeSync;
    gameA.DisplayId = "display-a";
    store.Save(state);
    var loaded = store.Load();
    Assert(loaded.Profiles.Single(p => p.Id == cinematic.Id).FpsLimit == 45, "edited profile persists");
    Assert(loaded.Games.Count(g => g.ProfileId == cinematic.Id) == 2, "shared assignment persists");
    Assert(loaded.DefaultDisplayId == "display-b" && loaded.MinimizeToTray && loaded.Theme == "Light" &&
           loaded.RtssLimiterMode == RtssLimiterMode.BackEdgeSync &&
           new AppState().Theme == "Dark" &&
           loaded.Games.Single(g => g.Id == gameA.Id).DisplayId == "display-a" &&
           loaded.Profiles.Single(p => p.Id == cinematic.Id).FpsMode == FpsLimitMode.Manual,
        "display selection, tray preference and legacy manual FPS persist");
    Console.WriteLine("PASS SQLite roundtrip and centralized assignment");

    string corruptDbDirectory = Path.Combine(temp, "corrupt-db");
    Directory.CreateDirectory(corruptDbDirectory);
    string corruptDbPath = Path.Combine(corruptDbDirectory, "profiles.db");
    const string corruptDbContents = "not a SQLite database";
    File.WriteAllText(corruptDbPath, corruptDbContents);
    bool corruptDbRejected = false;
    try { _ = new SqliteStateStore(corruptDbDirectory).Load(); }
    catch (InvalidOperationException) { corruptDbRejected = true; }
    Assert(corruptDbRejected && File.ReadAllText(corruptDbPath) == corruptDbContents,
        "corrupt SQLite state is rejected without replacing the database");
    var failingStore = new FailingStateStore(corruptDbDirectory);
    bool startupBlocked = false;
    try { _ = new MainViewModel(failingStore); }
    catch (StateLoadException) { startupBlocked = true; }
    Assert(startupBlocked && failingStore.SaveCalls == 0,
        "view model aborts startup after a load error without saving a blank state");

    // An unassigned entry with an already completed backup must be removed on ApplyAll.
    var detached = new AppState();
    var detachedGame = new GameEntry { Name = "Detached", ExePath = @"C:\Games\Detached.exe" };
    detached.Games.Add(detachedGame);
    detached.Backups.Add(new SettingBackup { ExePath = detachedGame.ExePath, ManagedGameId = detachedGame.Id });
    new SyncService(store, _ => new FakeRtss(), () => new FakeNvidia()).ApplyAll(detached);
    Assert(detachedGame.ProfileId is null && detached.Backups.Count == 0, "clearing profile restores and drops saved backup");
    Console.WriteLine("PASS empty profile selection does not apply settings");

    var displayB = displayA with { Id = "b", Name = "Экран B", IsPrimary = false, RefreshNumerator = 144000 };
    var monitor = new DisplayChangeMonitor();
    monitor.Reset([displayA]);
    Assert(!monitor.Observe([displayB]) && !monitor.Observe([displayA]) &&
           !monitor.Observe([displayB]) && monitor.Observe([displayB]) &&
           !monitor.Observe([displayB]),
        "display changes require two stable observations and ignore repeated events");
    var renamedDisplay = displayB with { Name = "Новое имя экрана" };
    Assert(!monitor.Observe([renamedDisplay]) && monitor.Observe([renamedDisplay]),
        "display presentation changes update after the same stability check");
    var ratioProfile = new PerformanceProfile { Name = "Доля", FpsMode = FpsLimitMode.DisplayFraction,
        FpsDivisor = 3, Vrr = VrrMode.GSync };
    var ratioState = new AppState();
    ratioState.Profiles.Add(ratioProfile);
    ratioState.DefaultDisplayId = "a";
    var ratioGame = new GameEntry { Name = "Ratio", ExePath = exeA, ProfileId = ratioProfile.Id };
    ratioState.Games.Add(ratioGame);
    Assert(DisplayFps.Resolve(ratioState, ratioGame, ratioProfile, [displayA, displayB]).Fps == 40 &&
           DisplayFps.ExactLimit(displayA with { RefreshNumerator = 119880 }, 3) is null &&
           DisplayFps.ExactLimit(displayA with { RefreshNumerator = 200000 }, 3) is null &&
           DisplayFps.ExactLimit(displayA with { RefreshNumerator = 200000 }, 4) == 50,
        "only exact integer divisions are accepted for V-Sync");
    var nearly120 = displayA with { RefreshNumerator = 119998 };
    Assert(nearly120.RefreshLabel == "120 Гц" && nearly120.SignalRefreshLabel == "119,998 Гц" &&
           nearly120.RefreshPrecisionNote.Contains("119,998") &&
           DisplayFps.ExactLimit(nearly120, 3) == 40 &&
           DisplayFps.ExactLimit(nearly120, 8) == 15 &&
           DisplayFps.ExactLimit(nearly120, 7) is null &&
           DisplayFps.ExactLimit(displayA with { RefreshNumerator = 119980 }, 3) is null,
        "small signal drift uses the nominal Windows mode without allowing rounded fractions");
    Assert(DisplayFps.Resolve(ratioState, ratioGame, ratioProfile, [nearly120]).Detail.Contains("сигнал 119,998 Гц"),
        "fraction explanation discloses the signal's actual rate");
    ratioState.DefaultDisplayId = "";
    Assert(DisplayFps.ChooseDisplay(ratioState, null, [displayB, displayA]) == displayA,
        "without an override, Windows primary display is used");
    ratioState.DefaultDisplayId = "a";
    ratioGame.DisplayId = "b";
    Assert(DisplayFps.Resolve(ratioState, ratioGame, ratioProfile, [displayA, displayB]).Fps == 48,
        "per-game display overrides the default");
    ratioGame.DisplayId = "";
    Assert(DisplayFps.Resolve(ratioState, ratioGame, ratioProfile, [displayA with { DynamicRefresh = true }]).Fps is null &&
           DisplayFps.Resolve(ratioState, ratioGame, ratioProfile, []).Fps is null,
        "dynamic or unavailable display blocks automatic FPS");
    ratioProfile.FpsMode = FpsLimitMode.Manual;
    ratioProfile.FpsLimit = 67;
    Assert(DisplayFps.Resolve(ratioState, ratioGame, ratioProfile, []).Fps == 67,
        "manual FPS works without display data and with G-SYNC");
    ratioProfile.FpsMode = FpsLimitMode.DisplayFraction;
    var ratioRtss = new FakeRtss { Exists = true, Fps = 45 };
    var ratioNv = new FakeNvidia();
    IReadOnlyList<DisplayInfo> currentDisplays = [displayA];
    var ratioSync = new SyncService(store, _ => ratioRtss, () => ratioNv, () => currentDisplays);
    ratioSync.ApplyOne(ratioState, ratioGame);
    Assert(ratioGame.Status == ApplyStatus.Applied && ratioRtss.Fps == 40 &&
           ratioState.Backups.Single().RtssFps == 45, "fractional apply preserves the original RTSS value");
    currentDisplays = [displayA with { RefreshNumerator = 144000 }];
    ratioSync.CheckSaved(ratioState);
    Assert(ratioGame.Status == ApplyStatus.Pending, "a changed display rate invalidates the saved FPS");
    ratioSync.ApplyOne(ratioState, ratioGame);
    Assert(ratioRtss.Fps == 48 && ratioState.Backups.Single().RtssFps == 45,
        "changed refresh recalculates FPS without replacing the original backup");
    currentDisplays = [displayA with { RefreshNumerator = 119880 }];
    ratioSync.ApplyOne(ratioState, ratioGame);
    Assert(ratioGame.Status == ApplyStatus.Unsupported && ratioRtss.Fps == 45 &&
           !ratioNv.ProfileExists && ratioState.Backups.Count == 0 &&
           ratioGame.Detail.Contains("исходные значения восстановлены"),
        "noninteger refresh restores the original RTSS and NVIDIA values");
    int ratioWrites = ratioRtss.WriteCalls;
    int nvidiaWrites = ratioNv.ApplyCalls;
    ratioSync.ApplyOne(ratioState, ratioGame);
    Assert(ratioGame.Status == ApplyStatus.Unsupported && ratioRtss.WriteCalls == ratioWrites &&
           ratioNv.ApplyCalls == nvidiaWrites && ratioState.Backups.Count == 0,
        "noninteger division blocks both integrations before any write");
    currentDisplays = [];
    ratioSync.ApplyOne(ratioState, ratioGame);
    Assert(ratioGame.Status == ApplyStatus.Unsupported && ratioRtss.WriteCalls == ratioWrites &&
           ratioNv.ApplyCalls == nvidiaWrites && ratioState.Backups.Count == 0,
        "missing display blocks both integrations before any write");
    Console.WriteLine("PASS display ratios, G-SYNC, per-game overrides and restore");
    var selectedProfile = new PerformanceProfile { Name = "Test", FpsLimit = 73 };
    AppState SelectionState()
    {
        var result = new AppState();
        result.Profiles.Add(selectedProfile);
        result.Games.Add(new GameEntry { Name = "First", ExePath = exeA, ProfileId = selectedProfile.Id });
        result.Games.Add(new GameEntry { Name = "Second", ExePath = exeB, ProfileId = selectedProfile.Id });
        return result;
    }
    var rtssFake = new FakeRtss { Exists = true, Fps = 45, LimiterMode = 0 };
    var nvidiaFake = new FakeNvidia();
    var sync = new SyncService(store, _ => rtssFake, () => nvidiaFake);
    var single = SelectionState();
    int batchRtssCreations = 0, batchNvidiaCreations = 0;
    var batchCheck = new SyncService(store,
        _ => { batchRtssCreations++; return new FakeRtss { Exists = true, Fps = 73, LimiterMode = 1 }; },
        () => { batchNvidiaCreations++; return new FakeNvidia(); });
    batchCheck.CheckSaved(single);
    Assert(batchRtssCreations == 1 && batchNvidiaCreations == 1,
        "checking multiple games reuses one RTSS and NVIDIA client per pass");
    sync.ApplyOne(single, single.Games[0]);
    Assert(single.Games[0].Status == ApplyStatus.Applied && single.Games[1].Status == ApplyStatus.Pending,
        "single-game apply leaves other games untouched");
    Assert(single.Backups.Count == 1 && single.Backups[0].RtssFps == 45 && single.Backups[0].RtssSyncLimiter == 0 &&
           rtssFake.Fps == 73 && rtssFake.LimiterMode == 1,
        "single-game apply saves original settings before writing");
    int unchangedRtssWrites = rtssFake.WriteCalls;
    int unchangedNvidiaWrites = nvidiaFake.ApplyCalls;
    sync.ApplyOne(single, single.Games[0]);
    Assert(rtssFake.WriteCalls == unchangedRtssWrites && nvidiaFake.ApplyCalls == unchangedNvidiaWrites &&
           single.Backups.Single().RtssFps == 45 && single.Backups.Single().RtssSyncLimiter == 0,
        "matching readback skips both writes and preserves the original backup");
    single.RtssLimiterMode = RtssLimiterMode.NvidiaReflex;
    sync.CheckSaved(single);
    Assert(single.Games[0].Status == ApplyStatus.Pending, "changing the global RTSS mode makes applied games pending");
    sync.ApplyOne(single, single.Games[0]);
    Assert(rtssFake.LimiterMode == 3 && single.Backups[0].RtssSyncLimiter == 0,
        "new limiter mode is written while the original mode remains in the backup");
    sync.Restore(single, single.Games[0]);
    Assert(rtssFake.Fps == 45 && rtssFake.LimiterMode == 0 && !nvidiaFake.ProfileExists && single.Backups.Count == 0,
        "restoring a game returns both integrations to original values");

    var individualState = new AppState();
    var individualA = new PerformanceProfile { Name = "A", FpsLimit = 73, RtssLimiterMode = RtssLimiterMode.Async };
    var individualB = new PerformanceProfile { Name = "B", FpsLimit = 73, RtssLimiterMode = RtssLimiterMode.BackEdgeSync };
    individualState.Profiles.Add(individualA); individualState.Profiles.Add(individualB);
    var individualGameA = new GameEntry { Name = "A", ExePath = exeA, ProfileId = individualA.Id };
    var individualGameB = new GameEntry { Name = "B", ExePath = exeB, ProfileId = individualB.Id };
    individualState.Games.Add(individualGameA); individualState.Games.Add(individualGameB);
    var individualRtss = new PerExeFakeRtss();
    individualRtss.Seed(exeA, 45, 2);
    individualRtss.Seed(exeB, 50, 3);
    var individualNvidia = new FakeNvidia();
    var individualSync = new SyncService(store, _ => individualRtss, () => individualNvidia);
    individualSync.ApplyOne(individualState, individualGameA);
    individualSync.ApplyOne(individualState, individualGameB);
    Assert(individualRtss.Writes.Select(write => write.Mode).SequenceEqual(
               [RtssLimiterMode.Async, RtssLimiterMode.BackEdgeSync]) &&
           individualState.Backups.Single(b => b.ManagedGameId == individualGameA.Id).RtssSyncLimiter == 2 &&
           individualState.Backups.Single(b => b.ManagedGameId == individualGameB.Id).RtssSyncLimiter == 3,
        "two game profiles write their distinct limiter modes and capture original values");
    individualSync.CheckSaved(individualState);
    Assert(individualGameA.Status == ApplyStatus.Applied && individualGameB.Status == ApplyStatus.Applied,
        "CheckSaved verifies each explicit limiter mode");
    individualRtss.SetMode(exeB, 1);
    individualSync.CheckSaved(individualState);
    Assert(individualGameA.Status == ApplyStatus.Applied && individualGameB.Status == ApplyStatus.Pending &&
           individualGameB.Detail.Contains("режим лимитера отличается"),
        "CheckSaved identifies a mismatch in only the affected game's limiter mode");
    individualRtss.SetMode(exeB, 2);
    individualState.RtssLimiterMode = RtssLimiterMode.NvidiaReflex;
    individualSync.CheckSaved(individualState);
    Assert(individualGameA.Status == ApplyStatus.Applied && individualGameB.Status == ApplyStatus.Applied,
        "a changed global mode does not affect explicit profile modes");
    individualA.RtssLimiterMode = null;
    individualSync.CheckSaved(individualState);
    Assert(individualGameA.Status == ApplyStatus.Pending && individualGameB.Status == ApplyStatus.Applied,
        "switching one profile to inheritance changes only its desired mode");
    individualSync.ApplyOne(individualState, individualGameA);
    individualState.RtssLimiterMode = RtssLimiterMode.FrontEdgeSync;
    individualSync.ApplyOne(individualState, individualGameA);
    Assert(individualRtss.ReadLimiterMode(Path.GetFileName(exeA)) == 1 &&
           individualState.Backups.Single(b => b.ManagedGameId == individualGameA.Id).RtssFps == 45 &&
           individualState.Backups.Single(b => b.ManagedGameId == individualGameA.Id).RtssSyncLimiter == 2,
        "repeated applies keep the original RTSS FPS and limiter backup");
    individualSync.Restore(individualState, individualGameA);
    Assert(individualRtss.ReadFps(Path.GetFileName(exeA)) == 45 &&
           individualRtss.ReadLimiterMode(Path.GetFileName(exeA)) == 2 &&
           individualState.Backups.All(b => b.ManagedGameId != individualGameA.Id),
        "restore returns the first game's original FPS and limiter mode");
    var invalidState = new AppState();
    var invalidProfile = new PerformanceProfile { RtssLimiterMode = (RtssLimiterMode)99 };
    invalidState.Profiles.Add(invalidProfile);
    var invalidGame = new GameEntry { Name = "Invalid", ExePath = exeA, ProfileId = invalidProfile.Id };
    invalidState.Games.Add(invalidGame);
    int writesBeforeInvalid = individualRtss.Writes.Count;
    individualSync.CheckSaved(invalidState);
    individualSync.ApplyOne(invalidState, invalidGame);
    Assert(invalidGame.Status == ApplyStatus.Error && individualRtss.Writes.Count == writesBeforeInvalid &&
           invalidState.Backups.Count == 0,
        "invalid explicit modes fail before any external write");
    Console.WriteLine("PASS individual RTSS limiter inheritance, readback and original restore");

    var zeroLimitState = SelectionState();
    zeroLimitState.Games[0].ProfileId = null;
    zeroLimitState.Backups.Add(new SettingBackup
    {
        ExePath = exeA, ManagedGameId = zeroLimitState.Games[0].Id,
        RtssCaptured = true, RtssProfileExisted = true, RtssFps = 0, RtssSyncLimiter = 1
    });
    var removedRtssProfile = new FakeRtss { Exists = false, Fps = 0, LimiterMode = 1 };
    new SyncService(store, _ => removedRtssProfile, () => new FakeNvidia()).Restore(zeroLimitState, zeroLimitState.Games[0]);
    Assert(removedRtssProfile.Exists && removedRtssProfile.Fps == 0 && zeroLimitState.Backups.Count == 0 &&
           zeroLimitState.Games[0].Detail == "Исходные значения восстановлены",
        "restore recreates a manually deleted RTSS profile with its original unlimited FPS value");

    var legacyBackupState = SelectionState();
    legacyBackupState.Backups.Add(new SettingBackup
    {
        ExePath = exeA, ManagedGameId = legacyBackupState.Games[0].Id,
        RtssCaptured = true, RtssProfileExisted = true, RtssFps = 45
    });
    var legacyRtss = new FakeRtss { Exists = true, Fps = 73, LimiterMode = 2 };
    var legacySync = new SyncService(store, _ => legacyRtss, () => new FakeNvidia());
    legacySync.ApplyOne(legacyBackupState, legacyBackupState.Games[0]);
    Assert(legacyBackupState.Backups[0].RtssFps == 45 && legacyBackupState.Backups[0].RtssSyncLimiter == 2,
        "legacy backup retains FPS and captures limiter mode before the first new write");
    legacySync.Restore(legacyBackupState, legacyBackupState.Games[0]);
    Assert(legacyRtss.Fps == 45 && legacyRtss.LimiterMode == 2 && legacyBackupState.Backups.Count == 0,
        "legacy backup restores original FPS and captured limiter mode");

    var duplicateExeState = SelectionState();
    duplicateExeState.Games[1].ExePath = duplicateExeState.Games[0].ExePath;
    duplicateExeState.Backups.Add(new SettingBackup
    {
        ExePath = exeA, ManagedGameId = duplicateExeState.Games[0].Id,
        RtssCaptured = true, RtssProfileExisted = true, RtssFps = 45
    });
    var duplicateRestoreRtss = new FakeRtss { Exists = true, Fps = 73 };
    var duplicateRestoreNvidia = new FakeNvidia();
    var duplicateRestoreSync = new SyncService(store, _ => duplicateRestoreRtss, () => duplicateRestoreNvidia);
    bool wrongOwnerBlocked = false;
    try { duplicateRestoreSync.Restore(duplicateExeState, duplicateExeState.Games[1]); }
    catch (InvalidOperationException) { wrongOwnerBlocked = true; }
    Assert(wrongOwnerBlocked && duplicateRestoreRtss.RestoreCalls == 0 && duplicateRestoreNvidia.RestoreCalls == 0 &&
           duplicateExeState.Backups.Single().ManagedGameId == duplicateExeState.Games[0].Id,
        "restore never applies a different game's backup when EXE paths are duplicated");

    var ambiguousLegacyState = SelectionState();
    ambiguousLegacyState.Games[1].ExePath = ambiguousLegacyState.Games[0].ExePath;
    ambiguousLegacyState.Backups.Add(new SettingBackup { ExePath = exeA, RtssCaptured = true, RtssFps = 45 });
    wrongOwnerBlocked = false;
    try { duplicateRestoreSync.Restore(ambiguousLegacyState, ambiguousLegacyState.Games[0]); }
    catch (InvalidOperationException) { wrongOwnerBlocked = true; }
    Assert(wrongOwnerBlocked && duplicateRestoreRtss.RestoreCalls == 0 && ambiguousLegacyState.Backups.Count == 1,
        "legacy backup with multiple possible owners blocks restoration");

    var foreignBackupState = new AppState();
    foreignBackupState.Profiles.Add(selectedProfile);
    var newOwnerCandidate = new GameEntry { Name = "Новая запись", ExePath = exeA, ProfileId = selectedProfile.Id };
    foreignBackupState.Games.Add(newOwnerCandidate);
    Guid previousOwnerId = Guid.NewGuid();
    foreignBackupState.Backups.Add(new SettingBackup { ExePath = exeA, ManagedGameId = previousOwnerId,
        RtssCaptured = true, RtssProfileExisted = true, RtssFps = 45 });
    var foreignRtss = new FakeRtss { Exists = true, Fps = 45 };
    var foreignNvidia = new FakeNvidia();
    new SyncService(store, _ => foreignRtss, () => foreignNvidia).ApplyOne(foreignBackupState, newOwnerCandidate);
    Assert(newOwnerCandidate.Status == ApplyStatus.Error && foreignRtss.WriteCalls == 0 && foreignNvidia.ApplyCalls == 0 &&
           foreignBackupState.Backups.Single().ManagedGameId == previousOwnerId,
        "a new game cannot overwrite settings owned by another game's backup");

    var blockedRtss = new FakeRtss { ProbeFailure = true };
    var untouchedNvidia = new FakeNvidia();
    var blockedState = SelectionState();
    new SyncService(store, _ => blockedRtss, () => untouchedNvidia).ApplyOne(blockedState, blockedState.Games[0]);
    Assert(blockedState.Games[0].Status == ApplyStatus.Unsupported && untouchedNvidia.ApplyCalls == 0 &&
           blockedRtss.WriteCalls == 0 && blockedState.Backups.Count == 0,
        "RTSS preflight failure prevents NVIDIA and backup changes");

    var writableRtss = new FakeRtss { Exists = true, Fps = 45 };
    var blockedNvidia = new FakeNvidia { ProbeFailure = true };
    var nvidiaBlockedState = SelectionState();
    new SyncService(store, _ => writableRtss, () => blockedNvidia).ApplyOne(nvidiaBlockedState, nvidiaBlockedState.Games[0]);
    Assert(writableRtss.WriteCalls == 0 && blockedNvidia.ApplyCalls == 0 && nvidiaBlockedState.Backups.Count == 0,
        "NVIDIA preflight failure prevents RTSS changes");

    var unknownLimiter = new FakeRtss { Exists = true, Fps = 45, LimiterMode = null };
    var unknownLimiterState = SelectionState();
    var untouchedForLimiter = new FakeNvidia();
    new SyncService(store, _ => unknownLimiter, () => untouchedForLimiter).ApplyOne(unknownLimiterState, unknownLimiterState.Games[0]);
    Assert(unknownLimiterState.Games[0].Status == ApplyStatus.Unsupported && unknownLimiter.WriteCalls == 0 &&
           untouchedForLimiter.ApplyCalls == 0 && unknownLimiterState.Backups.Count == 0,
        "unknown original RTSS limiter mode blocks both integrations before writing");

    var rollbackRtss = new FakeRtss { Exists = true, Fps = 45, LimiterMode = 2 };
    var rollbackNvidia = new FakeNvidia { FailAfterApply = true };
    var rollbackState = SelectionState();
    new SyncService(store, _ => rollbackRtss, () => rollbackNvidia).ApplyOne(rollbackState, rollbackState.Games[0]);
    Assert(rollbackState.Games[0].Status == ApplyStatus.Error && rollbackRtss.Fps == 45 && rollbackRtss.LimiterMode == 2 &&
           !rollbackNvidia.ProfileExists && rollbackState.Backups.Count == 0,
        "failure during NVIDIA write rolls both integrations back");
    var failRtss = new FakeRtss { Exists = true, Fps = 45, LimiterMode = 2, FailAfterWrite = true };
    var noNvidiaWrite = new FakeNvidia();
    var rtssFailureState = SelectionState();
    new SyncService(store, _ => failRtss, () => noNvidiaWrite).ApplyOne(rtssFailureState, rtssFailureState.Games[0]);
    Assert(rtssFailureState.Games[0].Status == ApplyStatus.Error && failRtss.Fps == 45 && failRtss.LimiterMode == 2 &&
           noNvidiaWrite.ApplyCalls == 0 && rtssFailureState.Backups.Count == 0,
        "failure during RTSS write rolls it back without touching NVIDIA");

    var stoppedRtss = new FakeRtss { Running = false, Exists = true, Fps = 73 };
    var stoppedState = SelectionState();
    new SyncService(store, _ => stoppedRtss, () => new FakeNvidia()).CheckSaved(stoppedState);
    Assert(stoppedState.Games[0].Status == ApplyStatus.Unsupported && stoppedState.Games[0].Detail.Contains("RTSS закрыт"),
        "a stopped RTSS process is never reported as applied");
    var availability = new SyncService(store, _ => stoppedRtss, () => new FakeNvidia());
    availability.CheckIntegrations(stoppedState);
    Assert(!availability.RtssReady && availability.RtssStatus!.Contains("закрыт"), "closed RTSS is reported in the integration status");
    stoppedRtss.Running = true;
    availability.CheckIntegrations(stoppedState);
    Assert(availability.RtssReady && availability.RtssStatus!.Contains("запущен"), "RTSS restart clears the warning state");

    var restoreState = SelectionState();
    restoreState.Backups.Add(new SettingBackup
    {
        ExePath = exeA, ManagedGameId = restoreState.Games[0].Id,
        RtssCaptured = true, RtssProfileExisted = true, RtssFps = 45,
        NvidiaCaptured = true, NvidiaProfileExisted = false
    });
    var restoreRtss = new FakeRtss { Exists = true, Fps = 73 };
    var restoreNvidia = new FakeNvidia { ProbeFailure = true };
    new SyncService(store, _ => restoreRtss, () => restoreNvidia).Restore(restoreState, restoreState.Games[0]);
    Assert(restoreRtss.Fps == 73 && restoreState.Backups.Count == 1 && restoreState.Games[0].Status == ApplyStatus.Unsupported,
        "restore preflight checks both integrations before changing RTSS");
    Console.WriteLine("PASS preflight, per-game apply, restoration and rollback");

    string steam = Path.Combine(temp, "Steam");
    string apps = Path.Combine(steam, "steamapps");
    Directory.CreateDirectory(Path.Combine(apps, "common", "One"));
    Directory.CreateDirectory(Path.Combine(apps, "common", "Two"));
    File.WriteAllText(Path.Combine(apps, "appmanifest_1.acf"), "\"AppState\" { \"appid\" \"1\" \"name\" \"One Game\" \"installdir\" \"One\" \"UserConfig\" { \"language\" \"english\" } \"MountedConfig\" { \"language\" \"russian\" } }");
    File.WriteAllText(Path.Combine(apps, "appmanifest_2.acf"), "\"AppState\" { \"appid\" \"2\" \"name\" \"Two Game\" \"installdir\" \"Two\" }");
    File.WriteAllBytes(Path.Combine(apps, "common", "One", "actual-game.exe"), [0x4D, 0x5A]);
    File.WriteAllBytes(Path.Combine(apps, "common", "Two", "first.exe"), [0x4D, 0x5A]);
    File.WriteAllBytes(Path.Combine(apps, "common", "Two", "second.exe"), [0x4D, 0x5A]);
    string wukong = Path.Combine(apps, "common", "BlackMythWukong");
    Directory.CreateDirectory(Path.Combine(wukong, "b1", "Binaries", "Win64"));
    File.WriteAllText(Path.Combine(apps, "appmanifest_2358720.acf"), "\"AppState\" { \"appid\" \"2358720\" \"name\" \"Black Myth: Wukong\" \"installdir\" \"BlackMythWukong\" }");
    File.WriteAllBytes(Path.Combine(wukong, "b1.exe"), [0x4D, 0x5A]);
    File.WriteAllBytes(Path.Combine(wukong, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe"), [0x4D, 0x5A]);
    foreach (var (id, name) in new[] { ("1812620", "DSX"), ("993090", "Lossless Scaling"), ("44", "Other Utility"), ("45", "Cached Game") })
    {
        Directory.CreateDirectory(Path.Combine(apps, "common", name));
        File.WriteAllText(Path.Combine(apps, $"appmanifest_{id}.acf"), $"\"AppState\" {{ \"appid\" \"{id}\" \"name\" \"{name}\" \"installdir\" \"{name}\" }}");
        File.WriteAllBytes(Path.Combine(apps, "common", name, "main.exe"), [0x4D, 0x5A]);
    }
    string cache = Path.Combine(steam, "appcache");
    Directory.CreateDirectory(cache);
    using (var stream = File.Create(Path.Combine(cache, "appinfo.vdf")))
    using (var writer = new BinaryWriter(stream))
    {
        writer.Write(0x07564429); writer.Write(1); writer.Write(0);
        foreach (var (id, type) in new[] { (44, "Application"), (45, "Game") })
        {
            writer.Write(id); writer.Write((byte)0); writer.Write(3);
            writer.Write((byte)1); writer.Write(5);
            writer.Write(System.Text.Encoding.UTF8.GetBytes(type)); writer.Write((byte)0);
        }
        int tableOffset = (int)stream.Position;
        stream.Position = 8; writer.Write(tableOffset); stream.Position = tableOffset;
        writer.Write(6);
        foreach (string key in new[] { "appinfo", "appid", "public_only", "common", "name", "type" })
        { writer.Write(System.Text.Encoding.UTF8.GetBytes(key)); writer.Write((byte)0); }
    }
    string otherLibrary = Path.Combine(temp, "OtherLibrary");
    string otherApps = Path.Combine(otherLibrary, "steamapps");
    Directory.CreateDirectory(Path.Combine(otherApps, "common", "Three"));
    File.WriteAllText(Path.Combine(apps, "libraryfolders.vdf"), "\"libraryfolders\" { \"1\" { \"path\" \"" + otherLibrary.Replace("\\", "\\\\") + "\" } }");
    File.WriteAllText(Path.Combine(otherApps, "appmanifest_3.acf"), "\"AppState\" { \"appid\" \"3\" \"name\" \"Three Game\" \"installdir\" \"Three\" }");
    File.WriteAllBytes(Path.Combine(otherApps, "common", "Three", "third.exe"), [0x4D, 0x5A]);
    var found = SteamDiscovery.FindInstalled(steam);
    Assert(found.Count == 5, "Steam games imported across libraries; programs excluded by appinfo and known IDs");
    Assert(found.Single(g => g.Name == "One Game").ExePath.EndsWith("actual-game.exe"), "unambiguous executable selected");
    Assert(found.Single(g => g.Name == "Two Game").ExePath == "", "ambiguous executable requires selection");
    Assert(found.Single(g => g.Name == "Black Myth: Wukong").ExePath.EndsWith("b1-Win64-Shipping.exe"), "Wukong shipping binary preferred over bootstrap executable");
    Assert(found.All(g => g.Name is not ("DSX" or "Lossless Scaling" or "Other Utility")), "non-game Steam apps excluded");
    Console.WriteLine("PASS Steam game classification, Wukong executable and ambiguity handling");

    string epicManifests = Path.Combine(temp, "EpicManifests");
    Directory.CreateDirectory(epicManifests);
    string epicOne = Path.Combine(temp, "EpicOne");
    Directory.CreateDirectory(epicOne);
    File.WriteAllBytes(Path.Combine(epicOne, "game.exe"), [0x4D, 0x5A]);
    File.WriteAllBytes(Path.Combine(epicOne, "other.exe"), [0x4D, 0x5A]);
    string epicAmbiguous = Path.Combine(temp, "EpicAmbiguous");
    Directory.CreateDirectory(epicAmbiguous);
    File.WriteAllBytes(Path.Combine(epicAmbiguous, "first.exe"), [0x4D, 0x5A]);
    File.WriteAllBytes(Path.Combine(epicAmbiguous, "second.exe"), [0x4D, 0x5A]);
    string epicWukong = Path.Combine(temp, "EpicWukong");
    Directory.CreateDirectory(Path.Combine(epicWukong, "b1", "Binaries", "Win64"));
    File.WriteAllBytes(Path.Combine(epicWukong, "b1.exe"), [0x4D, 0x5A]);
    File.WriteAllBytes(Path.Combine(epicWukong, "b1", "Binaries", "Win64", "b1-Win64-Shipping.exe"), [0x4D, 0x5A]);
    string epicUnsafe = Path.Combine(temp, "EpicUnsafe");
    Directory.CreateDirectory(epicUnsafe);
    string outsideExe = Path.Combine(temp, "outside.exe");
    File.WriteAllBytes(outsideExe, [0x4D, 0x5A]);
    void EpicItem(string file, string id, string name, string install, string launch = "", string type = "public,games,applications", bool incomplete = false)
    {
        File.WriteAllText(Path.Combine(epicManifests, file + ".item"), System.Text.Json.JsonSerializer.Serialize(new
        {
            AppName = id, DisplayName = name, InstallLocation = install, LaunchExecutable = launch,
            TechnicalType = type, bIsApplication = true, bIsExecutable = true, bIsIncompleteInstall = incomplete
        }));
    }
    EpicItem("one", "one-id", "Epic One", epicOne, "game.exe");
    EpicItem("duplicate", "one-id", "Epic One", epicOne, "game.exe");
    EpicItem("ambiguous", "ambiguous-id", "Epic Ambiguous", epicAmbiguous);
    EpicItem("wukong", "wukong-id", "Black Myth: Wukong", epicWukong, "b1.exe");
    EpicItem("unsafe", "unsafe-id", "Epic Unsafe", epicUnsafe, "..\\outside.exe");
    EpicItem("utility", "utility-id", "Epic Online Services", epicOne, "game.exe", "public,applications");
    EpicItem("incomplete", "incomplete-id", "Incomplete", epicOne, "game.exe", incomplete: true);
    File.WriteAllText(Path.Combine(epicManifests, "broken.item"), "{invalid json");
    var epicFound = EpicDiscovery.FindInstalled(epicManifests);
    Assert(epicFound.Count == 4, "EGS imports valid games once and skips utilities, incomplete and corrupt manifests");
    Assert(epicFound.Single(g => g.EpicAppName == "one-id").ExePath == Path.Combine(epicOne, "game.exe"),
        "EGS launch executable resolves a two-candidate game");
    Assert(epicFound.Single(g => g.EpicAppName == "ambiguous-id").ExePath == "",
        "ambiguous EGS executable requires user selection");
    Assert(epicFound.Single(g => g.EpicAppName == "wukong-id").ExePath.EndsWith("b1-Win64-Shipping.exe"),
        "actual shipping process is preferred to EGS bootstrap");
    Assert(epicFound.Single(g => g.EpicAppName == "unsafe-id").ExePath == "",
        "EGS launch path cannot escape installation directory");
    loaded.Games.Add(epicFound.Single(g => g.EpicAppName == "one-id"));
    store.Save(loaded);
    Assert(store.Load().Games.Single(g => g.EpicAppName == "one-id").SteamAppId is null,
        "EGS identity survives SQLite roundtrip without affecting Steam identity");
    Console.WriteLine("PASS EGS manifest filtering, executable selection and persistence");
}
finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }

static void Assert(bool value, string label)
{
    if (!value) throw new Exception("FAIL: " + label);
}

sealed class FakeRtss : IRtssSettings
{
    public string InstallDirectory => "test";
    public bool IsRunning => Running;
    public bool Running { get; set; } = true;
    public bool Exists { get; set; }
    public int? Fps { get; set; }
    public int? LimiterMode { get; set; } = 1;
    public bool ProbeFailure { get; set; }
    public bool FailAfterWrite { get; set; }
    public int WriteCalls { get; private set; }
    public int RestoreCalls { get; private set; }
    public bool ProfileExists(string exeName) => Exists;
    public int? ReadFps(string exeName) => Fps;
    public int? ReadLimiterMode(string exeName) => LimiterMode;
    public void ProbeWrite(string exeName, int? targetFps)
    {
        if (ProbeFailure) throw new NotSupportedException("RTSS недоступен");
        if (targetFps is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(targetFps));
    }
    public void WriteSettings(string exeName, int fps, RtssLimiterMode mode)
    {
        WriteCalls++; Exists = true; Fps = fps; LimiterMode = (int)mode;
        if (FailAfterWrite) throw new InvalidOperationException("Сбой RTSS после записи");
    }
    public void Restore(string exeName, bool existed, int? previousFps, int? previousLimiterMode)
    { RestoreCalls++; Exists = existed; Fps = previousFps; LimiterMode = previousLimiterMode ?? LimiterMode; }
    public void Dispose() { }
}

sealed class PerExeFakeRtss : IRtssSettings
{
    private readonly Dictionary<string, (int? Fps, int? Mode)> _profiles = new(StringComparer.OrdinalIgnoreCase);
    public string InstallDirectory => "test";
    public bool IsRunning => true;
    public List<(string ExeName, RtssLimiterMode Mode)> Writes { get; } = new();
    public void Seed(string exePath, int fps, int mode) => _profiles[Path.GetFileName(exePath)] = (fps, mode);
    public void SetMode(string exePath, int mode)
    {
        string name = Path.GetFileName(exePath);
        _profiles[name] = (_profiles[name].Fps, mode);
    }
    public bool ProfileExists(string exeName) => _profiles.ContainsKey(exeName);
    public int? ReadFps(string exeName) => _profiles.TryGetValue(exeName, out var value) ? value.Fps : null;
    public int? ReadLimiterMode(string exeName) => _profiles.TryGetValue(exeName, out var value) ? value.Mode : null;
    public void ProbeWrite(string exeName, int? targetFps) { }
    public void WriteSettings(string exeName, int fps, RtssLimiterMode mode)
    {
        Writes.Add((exeName, mode));
        _profiles[exeName] = (fps, (int)mode);
    }
    public void Restore(string exeName, bool existed, int? previousFps, int? previousLimiterMode)
    {
        if (existed) _profiles[exeName] = (previousFps, previousLimiterMode);
        else _profiles.Remove(exeName);
    }
    public void Dispose() { }
}

sealed class FakeNvidia : INvidiaSettings
{
    public bool ProfileExists { get; set; }
    public uint? Vrr { get; set; }
    public uint? VSync { get; set; }
    public uint? Fps { get; set; }
    public bool ProbeFailure { get; set; }
    public bool FailAfterApply { get; set; }
    public int ApplyCalls { get; private set; }
    public int RestoreCalls { get; private set; }
    public NvidiaSettingsSnapshot Read(string exePath) =>
        new(ProfileExists, Vrr, ProfileExists, VSync, ProfileExists, Fps, ProfileExists);
    public bool Matches(NvidiaSettingsSnapshot current, PerformanceProfile desired) =>
        current.ProfileExisted && current.VrrExplicit && current.VSyncExplicit && current.FpsExplicit &&
        current.Vrr == (desired.Vrr == VrrMode.FixedRefresh ? NvidiaClient.FixedRefresh : NvidiaClient.AllowGSync) &&
        current.VSync == (desired.VSync switch
        {
            VSyncMode.On => NvidiaClient.VSyncOn,
            VSyncMode.Off => NvidiaClient.VSyncOff,
            _ => NvidiaClient.VSyncApplication
        }) && current.Fps == 0;
    public void ProbeWrite()
    {
        if (ProbeFailure) throw new NotSupportedException("NVIDIA недоступна");
    }
    public void Apply(GameEntry game, PerformanceProfile desired)
    {
        ApplyCalls++;
        ProfileExists = true;
        Vrr = desired.Vrr == VrrMode.FixedRefresh ? NvidiaClient.FixedRefresh : NvidiaClient.AllowGSync;
        VSync = desired.VSync switch
        {
            VSyncMode.On => NvidiaClient.VSyncOn, VSyncMode.Off => NvidiaClient.VSyncOff, _ => NvidiaClient.VSyncApplication
        };
        Fps = 0;
        if (FailAfterApply) throw new InvalidOperationException("Сбой NVIDIA после записи");
    }
    public void Restore(string exePath, SettingBackup backup)
    {
        RestoreCalls++;
        ProfileExists = backup.NvidiaProfileExisted;
        Vrr = backup.NvidiaVrr;
        VSync = backup.NvidiaVSync;
        Fps = backup.NvidiaFpsLimit;
    }
    public void Dispose() { }
}

sealed class FailingStateStore(string directoryPath) : IStateStore
{
    public string DirectoryPath { get; } = directoryPath;
    public int SaveCalls { get; private set; }
    public AppState Load() => throw new InvalidOperationException("База недоступна");
    public void Save(AppState state) => SaveCalls++;
    public string ExportBackup(AppState state) => "";
    public void Log(Exception exception) { }
}
