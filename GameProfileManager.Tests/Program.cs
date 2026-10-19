using GameProfileManager.Core;
using GameProfileManager.Infrastructure;
using GameProfileManager.Integrations;

string temp = Path.Combine(Path.GetTempPath(), "gpm-tests-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(temp);
    var store = new SqliteStateStore(Path.Combine(temp, "db"));
    var state = store.Load();
    Assert(state.Profiles.Count == 3, "three starter profiles");
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
    var cinematic = state.Profiles.Single(p => p.Name == "Кинематографичный");
    var gameA = new GameEntry { Name = "A", ExePath = @"C:\Games\A.exe", ProfileId = cinematic.Id };
    var gameB = new GameEntry { Name = "B", ExePath = @"C:\Games\B.exe", ProfileId = cinematic.Id };
    state.Games.Add(gameA); state.Games.Add(gameB);
    cinematic.FpsLimit = 45;
    state.DefaultDisplayId = "display-b";
    state.MinimizeToTray = true;
    gameA.DisplayId = "display-a";
    store.Save(state);
    var loaded = store.Load();
    Assert(loaded.Profiles.Single(p => p.Id == cinematic.Id).FpsLimit == 45, "edited profile persists");
    Assert(loaded.Games.Count(g => g.ProfileId == cinematic.Id) == 2, "shared assignment persists");
    Assert(loaded.DefaultDisplayId == "display-b" && loaded.MinimizeToTray &&
           loaded.Games.Single(g => g.Id == gameA.Id).DisplayId == "display-a" &&
           loaded.Profiles.Single(p => p.Id == cinematic.Id).FpsMode == FpsLimitMode.Manual,
        "display selection, tray preference and legacy manual FPS persist");
    Console.WriteLine("PASS SQLite roundtrip and centralized assignment");

    // An unassigned entry with an already completed backup must be removed on ApplyAll.
    var detached = new AppState();
    var detachedGame = new GameEntry { Name = "Detached", ExePath = @"C:\Games\Detached.exe" };
    detached.Games.Add(detachedGame);
    detached.Backups.Add(new SettingBackup { ExePath = detachedGame.ExePath, ManagedGameId = detachedGame.Id });
    new SyncService(store).ApplyAll(detached);
    Assert(detachedGame.ProfileId is null && detached.Backups.Count == 0, "clearing profile restores and drops saved backup");
    Console.WriteLine("PASS empty profile selection does not apply settings");

    string exeA = Path.Combine(temp, "FirstGame.exe");
    string exeB = Path.Combine(temp, "SecondGame.exe");
    File.WriteAllBytes(exeA, [0x4D, 0x5A]);
    File.WriteAllBytes(exeB, [0x4D, 0x5A]);
    var displayA = new DisplayInfo("a", "Экран A", @"\\.\DISPLAY1", 2560, 1440,
        120000, 1000, true, "DisplayPort", false, false);
    var displayB = displayA with { Id = "b", Name = "Экран B", IsPrimary = false, RefreshNumerator = 144000 };
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
    var rtssFake = new FakeRtss { Exists = true, Fps = 45 };
    var nvidiaFake = new FakeNvidia();
    var sync = new SyncService(store, _ => rtssFake, () => nvidiaFake);
    var single = SelectionState();
    sync.ApplyOne(single, single.Games[0]);
    Assert(single.Games[0].Status == ApplyStatus.Applied && single.Games[1].Status == ApplyStatus.Pending,
        "single-game apply leaves other games untouched");
    Assert(single.Backups.Count == 1 && single.Backups[0].RtssFps == 45 && rtssFake.Fps == 73,
        "single-game apply saves original settings before writing");
    sync.Restore(single, single.Games[0]);
    Assert(rtssFake.Fps == 45 && !nvidiaFake.ProfileExists && single.Backups.Count == 0,
        "restoring a game returns both integrations to original values");

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

    var rollbackRtss = new FakeRtss { Exists = true, Fps = 45 };
    var rollbackNvidia = new FakeNvidia { FailAfterApply = true };
    var rollbackState = SelectionState();
    new SyncService(store, _ => rollbackRtss, () => rollbackNvidia).ApplyOne(rollbackState, rollbackState.Games[0]);
    Assert(rollbackState.Games[0].Status == ApplyStatus.Error && rollbackRtss.Fps == 45 &&
           !rollbackNvidia.ProfileExists && rollbackState.Backups.Count == 0,
        "failure during NVIDIA write rolls both integrations back");
    var failRtss = new FakeRtss { Exists = true, Fps = 45, FailAfterWrite = true };
    var noNvidiaWrite = new FakeNvidia();
    var rtssFailureState = SelectionState();
    new SyncService(store, _ => failRtss, () => noNvidiaWrite).ApplyOne(rtssFailureState, rtssFailureState.Games[0]);
    Assert(rtssFailureState.Games[0].Status == ApplyStatus.Error && failRtss.Fps == 45 &&
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
    public bool ProbeFailure { get; set; }
    public bool FailAfterWrite { get; set; }
    public int WriteCalls { get; private set; }
    public bool ProfileExists(string exeName) => Exists;
    public int? ReadFps(string exeName) => Fps;
    public void ProbeWrite(string exeName, int? targetFps)
    {
        if (ProbeFailure) throw new NotSupportedException("RTSS недоступен");
    }
    public void WriteFps(string exeName, int fps)
    {
        WriteCalls++; Exists = true; Fps = fps;
        if (FailAfterWrite) throw new InvalidOperationException("Сбой RTSS после записи");
    }
    public void Restore(string exeName, bool existed, int? previousFps) { Exists = existed; Fps = previousFps; }
    public void Dispose() { }
}

sealed class FakeNvidia : INvidiaSettings
{
    public bool ProfileExists { get; private set; }
    public uint? Vrr { get; private set; }
    public uint? VSync { get; private set; }
    public uint? Fps { get; private set; }
    public bool ProbeFailure { get; set; }
    public bool FailAfterApply { get; set; }
    public int ApplyCalls { get; private set; }
    public (bool ProfileExisted, uint? Vrr, bool VrrExplicit, uint? VSync, bool VSyncExplicit, uint? Fps, bool FpsExplicit) Read(string exePath) =>
        (ProfileExists, Vrr, ProfileExists, VSync, ProfileExists, Fps, ProfileExists);
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
        ProfileExists = backup.NvidiaProfileExisted;
        Vrr = backup.NvidiaVrr;
        VSync = backup.NvidiaVSync;
        Fps = backup.NvidiaFpsLimit;
    }
    public void Dispose() { }
}
