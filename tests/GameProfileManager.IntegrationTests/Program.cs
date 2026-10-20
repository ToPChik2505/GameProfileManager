using GameProfileManager.Integrations;
using GameProfileManager.Core;
using GameProfileManager.Application;
using GameProfileManager.Infrastructure;

foreach (var display in WindowsDisplayDiscovery.Read())
    Console.WriteLine($"Windows display: {display.Name}, {display.ResolutionLabel}, {display.RefreshLabel}, signal={display.SignalRefreshLabel}, {display.Connection}, primary={display.IsPrimary}, dynamic={display.DynamicRefresh}");

if (args.Contains("--steam-scan"))
{
    var games = SteamDiscovery.FindInstalled();
    Console.WriteLine($"Steam discovery: {games.Count} installed entries across configured libraries");
    if (games.Count == 0) throw new Exception("Steam discovery found no games on this PC");
    if (args.Contains("--steam-details"))
        foreach (var game in games) Console.WriteLine($"{game.SteamAppId}: {game.Name} -> {game.ExePath}");
}

try
{
    using var nv = new NvidiaClient();
    var read = nv.Read(@"C:\Windows\System32\notepad.exe");
    Console.WriteLine($"NVIDIA DRS read: VRR={read.Vrr}, VSync={read.VSync}, FPS={read.Fps}");
}
catch (Exception ex) { Console.WriteLine("NVIDIA error: " + ex); }
try
{
    using var rtss = new RtssClient(null);
    Console.WriteLine($"RTSS installed: {rtss.InstallDirectory}, Notepad profile: {rtss.ProfileExists("notepad.exe")}, FPS: {rtss.ReadFps("notepad.exe")}, limiter mode: {rtss.ReadLimiterMode("notepad.exe")}");
}
catch (Exception ex) { Console.WriteLine("RTSS error: " + ex); }
if (args.Contains("--write-test"))
{
    const string name = "GameProfileManagerSmoke.exe";
    using var rtss = new RtssClient(null);
    bool existed = rtss.ProfileExists(name);
    int? previous = rtss.ReadFps(name);
    if (existed) throw new InvalidOperationException("Smoke profile already exists; refusing to modify it.");
    try
    {
        rtss.WriteSettings(name, 37, RtssLimiterMode.FrontEdgeSync);
        Console.WriteLine($"RTSS write/read: {rtss.ReadFps(name)}, limiter mode: {rtss.ReadLimiterMode(name)}");
    }
    finally
    {
        rtss.Restore(name, existed, previous, null);
        Console.WriteLine($"RTSS profile removed: {!rtss.ProfileExists(name)}");
    }
}
if (args.Contains("--nvidia-write-test"))
{
    var game = new GameEntry { Name = "Game Profile Manager smoke test", ExePath = Path.Combine(AppContext.BaseDirectory, "GameProfileManagerNvSmoke.exe") };
    File.WriteAllBytes(game.ExePath, [0x4D, 0x5A]);
    try
    {
        using var nv = new NvidiaClient();
        if (nv.Read(game.ExePath).ProfileExisted) throw new InvalidOperationException("Smoke executable already has a profile.");
        try
        {
            nv.Apply(game, new PerformanceProfile { Vrr = VrrMode.FixedRefresh, VSync = VSyncMode.On, FpsLimit = 40 });
            Console.WriteLine($"NVIDIA fixed refresh readback: {nv.Read(game.ExePath).Vrr}");
            nv.Apply(game, new PerformanceProfile { Vrr = VrrMode.GSync, VSync = VSyncMode.On, FpsLimit = 60 });
            Console.WriteLine($"NVIDIA G-SYNC readback: {nv.Read(game.ExePath).Vrr}");
            nv.Restore(game.ExePath, new SettingBackup { ManagedGameId = game.Id, NvidiaProfileExisted = false });
            if (nv.Read(game.ExePath).ProfileExisted) throw new Exception("NVIDIA restore left the temporary profile behind");
            Console.WriteLine("NVIDIA restore removed the temporary profile");
        }
        finally
        {
            nv.DeleteManagedProfile(game.Id);
            Console.WriteLine($"NVIDIA profile removed: {!nv.Read(game.ExePath).ProfileExisted}");
        }
    }
    finally { File.Delete(game.ExePath); }
}
if (args.Contains("--sync-test"))
{
    string temp = Path.Combine(AppContext.BaseDirectory, "sync-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    string exe = Path.Combine(temp, "GameProfileManagerSyncSmoke.exe");
    File.WriteAllBytes(exe, [0x4D, 0x5A]);
    var store = new GameProfileManager.Infrastructure.SqliteStateStore(Path.Combine(temp, "state"));
    var service = SyncServiceFactory.Create(store);
    var profile = new PerformanceProfile { Name = "Smoke", Vrr = VrrMode.FixedRefresh, VSync = VSyncMode.On, FpsLimit = 41 };
    var game = new GameEntry { Name = "Smoke", ExePath = exe, ProfileId = profile.Id };
    var state = new AppState { RtssDirectory = Path.Combine(temp, "missing-rtss") };
    state.Profiles.Add(profile); state.Games.Add(game);
    try
    {
        using var nv = new NvidiaClient();
        var before = nv.Read(exe);
        service.ApplyAll(state);
        var after = nv.Read(exe);
        Console.WriteLine($"Preflight sync status: {game.Status}; NVIDIA VRR: {after.Vrr}; RTSS: {game.Detail}");
        if (after != before || state.Backups.Count != 0 || game.Status is not ApplyStatus.Unsupported)
            throw new Exception("Preflight allowed a partial NVIDIA write while RTSS was absent");
    }
    finally
    {
        try { using var nv = new NvidiaClient(); nv.DeleteManagedProfile(game.Id); } catch { }
        Directory.Delete(temp, true);
    }
}
