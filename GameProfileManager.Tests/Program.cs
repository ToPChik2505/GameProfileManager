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
    Assert(state.Profiles.Select(p => p.Name).SequenceEqual(["Кинематографичный", "Сбалансированный", "Соревновательный"]), "starter profile names are Russian");
    Assert(ProfileLabels.Vrr(VrrMode.FixedRefresh) == "Фиксированная частота" &&
           ProfileLabels.VSync(VSyncMode.ApplicationControlled) == "Управляется игрой", "mode labels are Russian");
    var cinematic = state.Profiles.Single(p => p.Name == "Кинематографичный");
    var gameA = new GameEntry { Name = "A", ExePath = @"C:\Games\A.exe", ProfileId = cinematic.Id };
    var gameB = new GameEntry { Name = "B", ExePath = @"C:\Games\B.exe", ProfileId = cinematic.Id };
    state.Games.Add(gameA); state.Games.Add(gameB);
    cinematic.FpsLimit = 45;
    store.Save(state);
    var loaded = store.Load();
    Assert(loaded.Profiles.Single(p => p.Id == cinematic.Id).FpsLimit == 45, "edited profile persists");
    Assert(loaded.Games.Count(g => g.ProfileId == cinematic.Id) == 2, "shared assignment persists");
    Console.WriteLine("PASS SQLite roundtrip and centralized assignment");

    // An unassigned entry with an already completed backup must be removed on ApplyAll.
    var detached = new AppState();
    var detachedGame = new GameEntry { Name = "Detached", ExePath = @"C:\Games\Detached.exe" };
    detached.Games.Add(detachedGame);
    detached.Backups.Add(new SettingBackup { ExePath = detachedGame.ExePath, ManagedGameId = detachedGame.Id });
    new SyncService(store).ApplyAll(detached);
    Assert(detachedGame.ProfileId is null && detached.Backups.Count == 0, "clearing profile restores and drops saved backup");
    Console.WriteLine("PASS empty profile selection does not apply settings");

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
}
finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }

static void Assert(bool value, string label)
{
    if (!value) throw new Exception("FAIL: " + label);
}
