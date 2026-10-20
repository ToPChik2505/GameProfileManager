using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using GameProfileManager;
using GameProfileManager.Core;
using GameProfileManager.Application;
using GameProfileManager.Infrastructure;
using GameProfileManager.Integrations;
using GameProfileManager.Presentation;

internal static class Program
{
[STAThread]
private static void Main(string[] args)
{
if (args.Length != 1) throw new ArgumentException("Output directory required");
string output = args[0];
Directory.CreateDirectory(output);
string stateDirectory = Path.Combine(output, "state");
if (Directory.Exists(stateDirectory))
    throw new IOException("The UI smoke output already contains state. Choose a new output directory.");
var store = new SqliteStateStore(stateDirectory);
var initial = store.Load();
initial.CheckAtStartup = false;
initial.RtssDirectory = Path.Combine(output, "missing-rtss");
initial.PresetNamesLocalized = false;
initial.Profiles[0].Name = "Cinematic";
initial.Profiles[1].Name = "Balanced";
initial.Profiles[2].Name = "Competitive";
initial.Games.Add(new GameEntry { Name = "DSX", SteamAppId = "1812620" });
initial.Games.Add(new GameEntry { Name = "Lossless Scaling", SteamAppId = "993090" });
string sampleExe = Path.Combine(output, "UiSample.exe");
File.WriteAllBytes(sampleExe, [0x4D, 0x5A]);
initial.Games.Add(new GameEntry { Name = "Тестовая игра", ExePath = sampleExe, SteamAppId = "1172620" });
store.Save(initial);

var listener = new BindingErrors();
PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
Exception? failure = null;
var app = new App();
app.InitializeComponent();
app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
var vm = new MainViewModel(store);
vm.AddProfileCommand.Execute(null);
var removedProfile = vm.SelectedProfile!;
vm.RemoveProfileCommand.Execute(null);
removedProfile.Name = "Удалённый профиль";
Check(!vm.ProfileOptions.Any(option => option.Id == removedProfile.Id),
    "removed profile no longer retains a view model event handler");
vm.SelectedProfile = vm.Profiles.First();
var window = new MainWindow(vm);
var frame = new DispatcherFrame();
var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
timer.Tick += (_, _) =>
{
    timer.Stop();
    try { CheckCaption(window, vm, output); CheckPages(window, vm, output); CheckControllerNavigation(window, vm); CheckControllerHint(window); CheckControllerKeyboard(window); CheckControllerMinimize(window, vm); CheckBusyWorkflow(output); }
    catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); }
    finally { window.Close(); app.Shutdown(); frame.Continue = false; }
};
timer.Start();
window.Show();
Dispatcher.PushFrame(frame);
if (failure is not null) throw failure;
if (!new SqliteStateStore(stateDirectory).Load().Profiles.Any(profile => profile.Description == "Сохранено при закрытии"))
    throw new Exception("FAIL pending edits are saved when the window closes");
if (listener.Messages.Count > 0)
    throw new Exception("WPF binding errors:" + Environment.NewLine + string.Join(Environment.NewLine, listener.Messages));
Console.WriteLine("PASS 6 pages, inspector, light/dark themes, navigation, protected presets, exact FPS and attention actions");
Console.WriteLine("Saved UI renders: " + output);
}

static void CheckPages(MainWindow window, MainViewModel vm, string output)
{
    var tabs = window.Content as TabControl ?? throw new Exception("TabControl missing");
    Check(tabs.Items.Count == 6, "six navigation pages");
    Check(window.Background is SolidColorBrush { Color: { R: < 50, G: < 50, B: < 50 } }, "dark window background");
    Check(vm.Profiles.Select(p => p.Name).SequenceEqual(["Кинематографичный", "Сбалансированный", "Соревновательный"]), "existing starter profiles renamed to Russian");
    Check(new SqliteStateStore(Path.Combine(output, "state")).Load().Profiles.All(p => p.Name is not ("Cinematic" or "Balanced" or "Competitive")), "renamed starter profiles persisted");

    tabs.SelectedIndex = 0;
    window.UpdateLayout();
    Capture(window, Path.Combine(output, "01-overview.png"));
    void CheckOverviewCard(string name, int page)
    {
        tabs.SelectedIndex = 0;
        window.UpdateLayout();
        var card = Find<Button>(window).Single(button => button.Name == name);
        card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, card));
        Check(tabs.SelectedIndex == page, $"{name} opens page {page}");
    }
    CheckOverviewCard("OverviewScreenCard", 1);
    CheckOverviewCard("OverviewGamesCard", 2);
    CheckOverviewCard("OverviewProfilesCard", 3);
    CheckOverviewCard("OverviewAttentionCard", 4);
    CheckOverviewCard("OverviewIntegrationsCard", 5);
    CheckOverviewCard("OverviewHowCard", 2);

    tabs.SelectedIndex = 1;
    window.UpdateLayout();
    Check(Find<ListBox>(window).Any(), "display list found");
    var defaultDisplayMenu = Find<ComboBox>(window).FirstOrDefault(c => c.Name == "DefaultDisplayMenu");
    Check(defaultDisplayMenu is not null, "default display selector found");
    OpenAndClose(defaultDisplayMenu!, Path.Combine(output, "06a-display-menu.png"));
    Check(vm.Displays.Count > 0, "active Windows displays discovered");
    defaultDisplayMenu!.SelectedValue = vm.Displays[0].Id;
    Pump();
    Check(vm.DefaultDisplayId == vm.Displays[0].Id, "default display selection persists in view model");
    if (vm.Displays.FirstOrDefault(display => display.Name == "MSI G273Q") is { } near120)
    {
        vm.SelectedDisplay = near120;
        window.UpdateLayout();
        Capture(window, Path.Combine(output, "02a-nominal-refresh.png"));
        var frequency = Find<TextBlock>(window).Single(block => block.Text == "120 Гц");
        var precisionNote = Find<TextBlock>(window).Single(block => block.Text.StartsWith("Сигнал: 119,998 Гц"));
        Check(Math.Abs(frequency.TranslatePoint(new Point(), window).X -
                       precisionNote.TranslatePoint(new Point(), window).X) < 1,
            "refresh precision note aligns with the refresh value");
        double originalWidth = window.Width, originalHeight = window.Height;
        window.Width = 1100;
        window.Height = 690;
        window.UpdateLayout();
        Capture(window, Path.Combine(output, "02b-nominal-refresh-narrow.png"));
        window.Width = originalWidth;
        window.Height = originalHeight;
        window.UpdateLayout();
        Check(near120.RefreshLabel == "120 Гц" && near120.RefreshPrecisionNote.Contains("119,998 Гц") &&
              DisplayFps.ExactLimit(near120, 3) == 40 &&
              vm.FpsFractions.Any(option => option.Value == 3 && option.Label.Contains("40 FPS")),
              "119.998 Hz mode displays nominal 120 Hz and offers 1/3 = 40 FPS");
    }
    var primaryDisplay = vm.Displays.First(display => display.IsPrimary);
    Check(vm.OverviewDisplay == primaryDisplay && vm.OverviewDisplayTitle == primaryDisplay.Name &&
          vm.OverviewDisplayHeading == "ОСНОВНОЙ ЭКРАН WINDOWS",
          "overview always identifies the Windows primary monitor");
    tabs.SelectedIndex = 0;
    window.UpdateLayout();
    var screenCard = Find<Button>(window).Single(button => button.Name == "OverviewScreenCard");
    screenCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, screenCard));
    Check(tabs.SelectedIndex == 1 && vm.SelectedDisplay == primaryDisplay,
          "overview screen card opens the primary monitor even when FPS uses another monitor");
    typeof(MainViewModel).GetMethod("UpdateDisplayOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
    Pump();
    Check(vm.DefaultDisplayId == vm.Displays[0].Id, "display option refresh keeps the default selection");
    defaultDisplayMenu.SelectedValue = "";
    Pump();
    Capture(window, Path.Combine(output, "02-screen.png"));
    Console.WriteLine($"Windows displays found: {vm.Displays.Count}; {vm.DisplayStatus}");

    tabs.SelectedIndex = 2;
    window.UpdateLayout();
    Check(vm.Games.Count == 1, "previously imported Steam utilities removed on startup");
    Check(Find<Border>(window).Single(border => border.Name == "GameActionsFooter").CornerRadius is
        { BottomLeft: 14, BottomRight: 14 }, "game inspector footer follows the card's rounded bottom corners");
    Capture(window, Path.Combine(output, "02-games.png"));
    window.UpdateLayout();
    Check(vm.Games.Count > 0, "sample game present");
    Check(vm.Games.All(g => g.SteamAppId is not ("1812620" or "993090" or "228980")), "Steam utilities excluded");
    WaitForIcons(vm);
    Check(vm.GameCards.All(game => game.Icon is not null), "Steam artwork or local icon available for every imported game");
    Check(!Find<Button>(window).Any(button => Equals(button.Content, "Сохранить")), "manual save button removed");
    vm.Games[0].Name = "Игра с автосохранением";
    Thread.Sleep(450);
    Pump();
    Check(new SqliteStateStore(Path.Combine(output, "state")).Load().Games[0].Name == "Игра с автосохранением",
        "game name is saved automatically");
    Check(Find<Image>(window).Any(image => image.Clip is RectangleGeometry { RadiusX: 9, RadiusY: 9 }),
        "game list artwork is clipped to rounded corners");
    Check(Find<Image>(window).Any(image => image.Clip is RectangleGeometry { RadiusX: 14, RadiusY: 14 }),
        "inspector artwork is clipped to rounded corners");
    var list = Find<ListBox>(window).Single(box => box.Name == "GamesList");
    Check(list.Items.Count == vm.GamesView.Cast<object>().Count(), "inspector game list bound to imported games");
    var applyButton = Find<Button>(window).FirstOrDefault(button => Equals(button.Content, "Применить к игре"));
    Check(applyButton?.CommandParameter is GameEntryViewModel entry && entry.Model == vm.Games[0], "per-game apply button is bound to selected game");
    var filter = Find<ComboBox>(window).FirstOrDefault(c => c.ItemsSource is string[]);
    Check(filter is not null, "status filter found");
    OpenAndClose(filter!, Path.Combine(output, "06-status-menu.png"));
    Check(vm.StatusFilters.SequenceEqual(["Все статусы", "Без профиля", "Нужен .exe", "Ожидает", "Применён", "Перезапустите", "Недоступно", "Ошибка"]), "all status filter labels are Russian");
    filter!.SelectedItem = "Применён";
    Pump();
    Check(vm.StatusFilter == "Применён" && !vm.GamesView.Cast<object>().Any(), "status filter updates the game list");
    var inspector = Find<ContentControl>(window).Single(control => control.Name == "GameInspector");
    Check(vm.SelectedGame is null && !inspector.IsVisible &&
          Find<TextBlock>(window).Single(block => block.Text == "Выберите игру").IsVisible,
        "empty filtered list shows a clean inspector empty state without the game template underneath");
    vm.Search = "нет такой игры";
    Pump();
    Check(!vm.GamesView.Cast<object>().Any() && !inspector.IsVisible,
        "search with no matches keeps the inspector empty without layered content");
    vm.Search = "";
    filter.SelectedItem = "Все статусы";
    Pump();
    list.SelectedItem = vm.GameCards[0];
    Pump();
    Check(vm.SelectedGame == vm.Games[0], "game selection binding");
    var gameDisplayMenu = Find<ComboBox>(window).FirstOrDefault(c => c.Name == "GameDisplayMenu");
    Check(gameDisplayMenu is not null, "per-game display selector found");
    OpenAndClose(gameDisplayMenu!, Path.Combine(output, "06b-game-display-menu.png"));
    gameDisplayMenu!.SelectedValue = vm.Displays[0].Id;
    Pump();
    Check(vm.SelectedGame?.DisplayId == vm.Displays[0].Id, "per-game display selection updates the game");
    typeof(MainViewModel).GetMethod("UpdateDisplayOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
    Pump();
    Check(vm.SelectedGame?.DisplayId == vm.Displays[0].Id, "display option refresh keeps the game override");
    gameDisplayMenu.SelectedValue = "";
    Pump();
    var gameProfile = Find<ComboBox>(window).FirstOrDefault(c => c.Name == "GameProfileMenu");
    if (gameProfile is not null)
    {
        var boundGame = gameProfile.DataContext as GameEntryViewModel ?? throw new Exception("Game profile menu has no game context");
        OpenAndClose(gameProfile, Path.Combine(output, "06b-profile-menu.png"));
        gameProfile.SelectedIndex = 1;
        Pump();
        Check(boundGame.Model.ProfileId == vm.Profiles[0].Id, "game profile assignment");
        Capture(window, Path.Combine(output, "03a-game-profile-assigned.png"));
        gameProfile.SelectedIndex = 0;
        Pump();
        Check(boundGame.Model.ProfileId is null, "empty profile selection clears assignment");
    }
    Capture(window, Path.Combine(output, "03-games-imported.png"));
    var epicButton = Find<Button>(window).FirstOrDefault(button => Equals(button.Content, "Импорт EGS"));
    Check(epicButton?.Command == vm.ImportEpicCommand, "EGS import button is bound");
    if (EpicDiscovery.FindInstalled().Count > 0)
    {
        epicButton!.Command.Execute(null);
        WaitForImport(vm);
        var epicGame = vm.Games.Single(g => g.EpicAppName is not null);
        epicGame.ProfileId = vm.Profiles[0].Id;
        int count = vm.Games.Count;
        epicButton.Command.Execute(null);
        WaitForImport(vm);
        Check(vm.Games.Count == count && epicGame.ProfileId == vm.Profiles[0].Id,
            "repeated EGS import preserves game assignment without duplicates");
        Check(new SqliteStateStore(Path.Combine(output, "state")).Load().Games.Any(g => g.EpicAppName == epicGame.EpicAppName),
            "EGS import persists in SQLite");
        Pump();
        window.UpdateLayout();
        Console.WriteLine($"EGS import: {epicGame.Name}, games={vm.Games.Count}");
        Capture(window, Path.Combine(output, "03b-epic-imported.png"));
    }

    tabs.SelectedIndex = 3;
    window.UpdateLayout();
    Check(Find<ListBox>(window).Any(), "profile list found");
    var fpsModeMenu = Find<ComboBox>(window).Single(c => c.Name == "FpsModeMenu");
    var fractionMenu = Find<ComboBox>(window).Single(c => c.Name == "FractionMenu");
    var vrrMenu = Find<ComboBox>(window).Single(c => c.Name == "VrrMenu");
    var vSyncMenu = Find<ComboBox>(window).Single(c => c.Name == "VSyncMenu");
    var profileRtssMenu = Find<ComboBox>(window).Single(c => c.Name == "ProfileRtssLimiterMenu");
    Check(vm.SelectedProfile is { IsBuiltIn: true } && !fpsModeMenu.IsEnabled &&
          !profileRtssMenu.IsEnabled && (int)profileRtssMenu.SelectedValue == -1,
        "starter profile editor is disabled");
    var deleteProfile = Find<Button>(window).Single(button => Equals(button.Content, "Удалить выбранный"));
    Check(!deleteProfile.IsEnabled, "starter profile delete button is disabled");
    int beforeCopy = vm.Profiles.Count;
    vm.RemoveProfileCommand.Execute(null);
    Check(vm.Profiles.Count == beforeCopy, "starter profile cannot be deleted through its command");
    vm.DuplicateProfileCommand.Execute(null);
    Pump();
    Check(vm.Profiles.Count == beforeCopy + 1 && vm.SelectedProfile is { IsBuiltIn: false } &&
          fpsModeMenu.IsEnabled && profileRtssMenu.IsEnabled && deleteProfile.IsEnabled,
        "editable copy is created from a starter profile");
    Check(profileRtssMenu.Items.Cast<ModeOption<int>>().Select(option => option.Value)
        .SequenceEqual([-1, 0, 1, 2, 3]) &&
          ((ModeOption<int>)profileRtssMenu.Items[0]).Label == "Наследовать общий режим",
        "profile editor has inheritance and four explicit RTSS modes");
    OpenAndClose(profileRtssMenu, Path.Combine(output, "06i-profile-rtss-limiter-menu.png"));
    profileRtssMenu.SelectedValue = (int)RtssLimiterMode.BackEdgeSync;
    Pump();
    Check(vm.SelectedProfile?.RtssLimiterMode == RtssLimiterMode.BackEdgeSync &&
          vm.SelectedProfileEffectiveRtssModeLabel.Contains("Задний фронт") &&
          vm.SelectedProfileEffectiveRtssModeLabel.Contains("задан в профиле"),
        "profile RTSS selection and effective mode are displayed");
    OpenAndClose(fpsModeMenu, Path.Combine(output, "06c-fps-mode-menu.png"));
    fpsModeMenu.SelectedValue = FpsLimitMode.DisplayFraction;
    Pump();
    var expectedDivisors = Enumerable.Range(1, 12)
        .Where(divisor => vm.DefaultDisplay is { } display && DisplayFps.ExactLimit(display, divisor) is not null)
        .ToArray();
    Check(vm.FpsFractions.Select(option => option.Value).SequenceEqual(expectedDivisors) &&
          vm.FpsFractions.All(option => !option.Label.Contains('≈')),
          "fraction menu contains only exact integer divisions");
    if (expectedDivisors.Length > 0)
    {
        int divisor = expectedDivisors[Math.Min(2, expectedDivisors.Length - 1)];
        fractionMenu.SelectedValue = divisor;
        Pump();
        typeof(MainViewModel).GetMethod("UpdateFpsFractions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        Pump();
        Check(vm.SelectedProfile?.FpsDivisor == divisor, "fraction menu refresh keeps the selected divisor");
        var selectedOption = fractionMenu.SelectedItem as ModeOption<int>;
        int? previousFps = vm.DefaultDisplay is { } previousDisplay
            ? DisplayFps.ExactLimit(previousDisplay, divisor) : null;
        var alternateDisplay = vm.Displays.FirstOrDefault(display => display.Id != vm.DefaultDisplay?.Id &&
            DisplayFps.ExactLimit(display, divisor) is int fps && fps != previousFps);
        if (alternateDisplay is not null)
        {
            vm.DefaultDisplayId = alternateDisplay.Id;
            Pump();
            int newFps = DisplayFps.ExactLimit(alternateDisplay, divisor)!.Value;
            Check(ReferenceEquals(fractionMenu.SelectedItem, selectedOption) &&
                  selectedOption?.Label == $"1/{divisor} — {newFps} FPS" &&
                  vm.SelectedProfile?.FpsDivisor == divisor,
                  "changing the FPS display keeps the selected fraction and updates its visible FPS");
            window.UpdateLayout();
            Capture(window, Path.Combine(output, "04a-fraction-after-screen-change.png"));
            vm.DefaultDisplayId = "";
            Pump();
            Check(ReferenceEquals(fractionMenu.SelectedItem, selectedOption) &&
                  selectedOption?.Label == $"1/{divisor} — {previousFps} FPS",
                  "switching back restores the option label without clearing selection");
        }
        OpenAndClose(fractionMenu, Path.Combine(output, "06d-fraction-menu.png"));
    }
    Check(vm.SelectedProfile?.FpsMode == FpsLimitMode.DisplayFraction, "exact fraction mode is available");
    fpsModeMenu.SelectedValue = FpsLimitMode.Manual;
    OpenAndClose(vrrMenu, Path.Combine(output, "06e-vrr-menu.png"));
    OpenAndClose(vSyncMenu, Path.Combine(output, "06f-vsync-menu.png"));
    Check(vrrMenu.Items.Cast<ModeOption<VrrMode>>().Select(o => o.Label).SequenceEqual(["G-SYNC", "Фиксированная частота"]), "VRR menu is localized");
    Check(vSyncMenu.Items.Cast<ModeOption<VSyncMode>>().Select(o => o.Label).SequenceEqual(["Управляется игрой", "Включена", "Выключена"]), "V-Sync menu is localized");
    vrrMenu.SelectedValue = VrrMode.GSync;
    Pump();
    Check(vm.SelectedProfile?.Vrr == VrrMode.GSync, "VRR menu updates selected profile");
    vrrMenu.SelectedValue = VrrMode.FixedRefresh;
    vSyncMenu.SelectedValue = VSyncMode.Off;
    Pump();
    Check(vm.SelectedProfile?.VSync == VSyncMode.Off, "V-Sync menu updates selected profile");
    vSyncMenu.SelectedValue = VSyncMode.On;
    var editedProfile = vm.SelectedProfile!;
    editedProfile.Name = "Профиль с автосохранением";
    editedProfile.Description = "Описание сохранено автоматически";
    editedProfile.FpsLimit = 77;
    Thread.Sleep(450);
    Pump();
    var savedProfile = new SqliteStateStore(Path.Combine(output, "state")).Load().Profiles.Single(p => p.Id == editedProfile.Id);
    Check(savedProfile.Name == editedProfile.Name && savedProfile.Description == editedProfile.Description &&
           savedProfile.FpsLimit == 77 && savedProfile.VSync == VSyncMode.On &&
           savedProfile.RtssLimiterMode == RtssLimiterMode.BackEdgeSync,
        "profile text, FPS and mode are saved automatically");
    Capture(window, Path.Combine(output, "04-profiles.png"));

    tabs.SelectedIndex = 4;
    window.UpdateLayout();
    vm.Games[0].ProfileId = vm.SelectedProfile!.Id;
    Pump();
    Check(vm.AttentionItems.Any(item => item.Game == vm.Games[0]) && vm.ErrorCount == vm.AttentionItems.Count,
        "attention page includes an assigned game that still needs applying");
    Capture(window, Path.Combine(output, "05-attention.png"));
    var gameIssue = Find<Button>(window).FirstOrDefault(button => button.DataContext is AttentionItem { Game: not null });
    Check(gameIssue is not null, "attention item has an action");
    gameIssue!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, gameIssue));
    Check(tabs.SelectedIndex == 2 && vm.SelectedGame == vm.Games[0], "attention action opens the affected game");

    tabs.SelectedIndex = 5;
    window.UpdateLayout();
    Check(Find<CheckBox>(window).Count() == 4, "startup, quiet elevation and tray settings found");
    Check(!vm.SilentAdminLaunch, "quiet elevation remains disabled outside Program Files");
    vm.CheckAtStartup = true;
    Check(Find<CheckBox>(window).First().IsChecked == true, "startup checkbox binding");
    Check(new SqliteStateStore(Path.Combine(output, "state")).Load().CheckAtStartup, "startup option saved automatically");
    vm.CheckAtStartup = false;
    vm.RtssDirectory = Path.Combine(output, "another-missing-rtss");
    Thread.Sleep(450);
    Pump();
    Check(new SqliteStateStore(Path.Combine(output, "state")).Load().RtssDirectory == vm.RtssDirectory,
        "RTSS path saved automatically");
    var limiterMenu = Find<ComboBox>(window).Single(c => c.Name == "RtssLimiterMenu");
    Check(limiterMenu.SelectedValue is RtssLimiterMode.FrontEdgeSync, "Front Edge Sync selected by default");
    OpenAndClose(limiterMenu, Path.Combine(output, "06h-rtss-limiter-menu.png"));
    Check(limiterMenu.Items.Cast<ModeOption<RtssLimiterMode>>().Select(option => option.Value)
        .SequenceEqual([RtssLimiterMode.Async, RtssLimiterMode.FrontEdgeSync,
            RtssLimiterMode.BackEdgeSync, RtssLimiterMode.NvidiaReflex]), "all RTSS limiter choices appear");
    Check(limiterMenu.Items.Cast<ModeOption<RtssLimiterMode>>().Take(3).All(option =>
        option.Label.Any(character => character is >= 'А' and <= 'я')),
        "RTSS limiter choices include Russian labels");
    var inheritedProfile = vm.SelectedProfile!;
    vm.DuplicateProfileCommand.Execute(null);
    var explicitProfile = vm.SelectedProfile!;
    explicitProfile.RtssLimiterMode = RtssLimiterMode.BackEdgeSync;
    vm.SelectedProfile = inheritedProfile;
    string explicitExe = Path.Combine(output, "ExplicitModeSample.exe");
    File.WriteAllBytes(explicitExe, [0x4D, 0x5A]);
    var explicitGame = new GameEntry { Name = "Игра с явным режимом", ExePath = explicitExe,
        ProfileId = explicitProfile.Id, Status = ApplyStatus.Applied };
    vm.Games.Add(explicitGame);
    vm.Games[0].Status = ApplyStatus.Applied;
    vm.Games[0].Detail = "Режим профиля уже применён";
    limiterMenu.SelectedValue = RtssLimiterMode.BackEdgeSync;
    Pump();
    Check(vm.RtssLimiterMode == RtssLimiterMode.BackEdgeSync &&
          new SqliteStateStore(Path.Combine(output, "state")).Load().RtssLimiterMode == RtssLimiterMode.BackEdgeSync,
        "RTSS limiter selection is saved automatically");
    Check(vm.Games[0].Status == ApplyStatus.Applied && explicitGame.Status == ApplyStatus.Applied &&
          vm.Games[0].RtssLimiterDisplay.Contains("Задний фронт"),
        "changing the global RTSS mode leaves an explicit profile untouched");
    vm.SelectedProfileRtssLimiterChoice = -1;
    Pump();
    Check(vm.SelectedProfile?.RtssLimiterMode is null && vm.Games[0].Status == ApplyStatus.Applied &&
          vm.SelectedProfileEffectiveRtssModeLabel.Contains("общий"),
        "switching to inheritance with the same effective value does not mark the game pending");
    limiterMenu.SelectedValue = RtssLimiterMode.Async;
    Pump();
    Check(vm.Games[0].Status == ApplyStatus.Pending && explicitGame.Status == ApplyStatus.Applied &&
          vm.Games[0].Detail.Contains("Режим лимитера") &&
          vm.Games[0].RtssLimiterDisplay.Contains("Асинхронный"),
        "changing the global RTSS mode marks only inheriting games pending");
    vm.Games[0].Status = ApplyStatus.Applied;
    vm.SelectedProfileRtssLimiterChoice = (int)RtssLimiterMode.NvidiaReflex;
    Pump();
    Check(vm.Games[0].Status == ApplyStatus.Pending && explicitGame.Status == ApplyStatus.Applied,
        "changing an individual RTSS mode marks only games assigned to that profile pending");
    vm.SelectedProfileRtssLimiterChoice = -1;
    vm.Games.Remove(explicitGame);
    vm.SelectedProfile = explicitProfile;
    vm.RemoveProfileCommand.Execute(null);
    vm.SelectedProfile = inheritedProfile;
    Capture(window, Path.Combine(output, "06-settings.png"));
    var themeMenu = Find<ComboBox>(window).Single(c => c.Name == "ThemeMenu");
    OpenAndClose(themeMenu, Path.Combine(output, "06g-theme-menu.png"));
    var originalTheme = themeMenu.SelectedItem;
    var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
    {
        RoutedEvent = UIElement.PreviewMouseWheelEvent,
        Source = themeMenu
    };
    themeMenu.RaiseEvent(wheel);
    Pump();
    Check(wheel.Handled && ReferenceEquals(themeMenu.SelectedItem, originalTheme) && vm.Theme == "Dark",
        "mouse wheel cannot change a closed selector");
    themeMenu.SelectedValue = "Light";
    Pump();
    Check(vm.Theme == "Light" && new SqliteStateStore(Path.Combine(output, "state")).Load().Theme == "Light", "light theme saved");
    Check(window.Background is SolidColorBrush { Color: { R: > 200, G: > 200, B: > 200 } }, "light theme applied to window");
    for (int index = 0; index < 6; index++)
    {
        tabs.SelectedIndex = index;
        Pump();
        window.UpdateLayout();
        Check(Find<FrameworkElement>(window).All(element => element.ToolTip is null),
            "page contains no tooltips");
        Capture(window, Path.Combine(output, $"light-{index + 1:00}.png"));
    }
    vm.Theme = "Dark";
    Pump();
    Check(window.Background is SolidColorBrush { Color: { R: < 50, G: < 50, B: < 50 } }, "dark theme restored");

    window.Width = 830;
    window.Height = 720;
    tabs.SelectedIndex = 2;
    Pump();
    window.UpdateLayout();
    Check(window.ContentColumns == 1, "half-screen layout uses compact game cards");
    Capture(window, Path.Combine(output, "07-half-games.png"));
    tabs.SelectedIndex = 3;
    Pump();
    window.UpdateLayout();
    Capture(window, Path.Combine(output, "07-half-profiles.png"));

    window.Width = 800;
    window.Height = 680;
    Check(window.MinWidth == 800 && window.MinHeight == 680, "window stops resizing before the layout becomes unusable");
    string[] pages = ["07-min-overview.png", "08-min-screen.png", "09-min-games.png", "10-min-profiles.png", "11-min-attention.png", "12-min-settings.png"];
    for (int index = 0; index < pages.Length; index++)
    {
        tabs.SelectedIndex = index;
        Pump();
        window.UpdateLayout();
        Capture(window, Path.Combine(output, pages[index]));
    }
    vm.MinimizeToTray = true;
    window.Close();
    Pump();
    Check(!window.IsVisible, "closing with tray setting hides the window");
    window.Show();
    window.WindowState = WindowState.Normal;
    vm.MinimizeToTray = false;
    vm.Profiles.Last().Description = "Сохранено при закрытии";
}

static void CheckCaption(MainWindow window, MainViewModel vm, string output)
{
    Check(window.WindowStyle == WindowStyle.None && WindowChrome.GetWindowChrome(window) is { CaptionHeight: 70 },
        "custom title bar has drag and resize chrome");
    long nativeStyle = GetWindowLongPtr(new WindowInteropHelper(window).Handle, -16).ToInt64();
    const long nativeWindowControls = 0x00080000 | 0x00040000 | 0x00020000 | 0x00010000;
    Check((nativeStyle & nativeWindowControls) == nativeWindowControls,
        "native resize, minimize and maximize styles remain available");
    Check((nativeStyle & 0x00C00000) == 0, "native title bar is absent");
    var buttons = Find<Button>(window).Where(button => !string.IsNullOrEmpty(button.Name))
        .ToDictionary(button => button.Name);
    foreach (string name in new[] { "MinimizeCaptionButton", "MaximizeCaptionButton", "CloseCaptionButton" })
        Check(buttons.ContainsKey(name) && WindowChrome.GetIsHitTestVisibleInChrome(buttons[name]),
            $"{name} is clickable inside window chrome");
    vm.MinimizeToTray = false;
    buttons["MaximizeCaptionButton"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump();
    Check(window.WindowState == WindowState.Maximized, "custom maximize works");
    Capture(window, Path.Combine(output, "00-maximized-title.png"));
    CheckMaximizedWorkArea(window);
    buttons["MaximizeCaptionButton"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump();
    Check(window.WindowState == WindowState.Normal, "custom restore works");
    buttons["MinimizeCaptionButton"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    Pump();
    Check(window.WindowState == WindowState.Minimized, "custom minimize works");
    window.WindowState = WindowState.Normal;
    window.Activate();
    Pump();
    Console.WriteLine("PASS custom caption controls and window chrome");
}

static void CheckMaximizedWorkArea(MainWindow window)
{
    IntPtr hwnd = new WindowInteropHelper(window).Handle;
    IntPtr monitor = MonitorFromWindow(hwnd, 2);
    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
    Check(monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info), "monitor work area available");
    Check(DwmGetWindowAttribute(hwnd, 9, out var visibleRect, Marshal.SizeOf<NativeRect>()) == 0,
        "maximized visible frame bounds available");
    const int frameTolerance = 2;
    Check(Math.Abs(visibleRect.Left - info.Work.Left) <= frameTolerance &&
          Math.Abs(visibleRect.Top - info.Work.Top) <= frameTolerance &&
          Math.Abs(visibleRect.Right - info.Work.Right) <= frameTolerance &&
          Math.Abs(visibleRect.Bottom - info.Work.Bottom) <= frameTolerance,
          "maximized visible frame fills this monitor's work area");
    IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<MinMaxInfo>());
    try
    {
        Marshal.StructureToPtr(new MinMaxInfo(), buffer, false);
        SendMessage(hwnd, 0x0024, IntPtr.Zero, buffer);
        var limits = Marshal.PtrToStructure<MinMaxInfo>(buffer);
        Check(limits.MaxPosition.X == info.Work.Left - info.Monitor.Left &&
              limits.MaxPosition.Y == info.Work.Top - info.Monitor.Top &&
              limits.MaxSize.X == info.Work.Right - info.Work.Left - 1 &&
              limits.MaxSize.Y == info.Work.Bottom - info.Work.Top - 1,
              "maximize bounds match this monitor's usable work area");
        var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        Check(limits.MinTrackSize.X >= (int)Math.Ceiling(window.MinWidth * scale.M11) &&
              limits.MinTrackSize.Y >= (int)Math.Ceiling(window.MinHeight * scale.M22),
              "native resize limits honor the WPF minimum size");
    }
    finally { Marshal.FreeHGlobal(buffer); }
}

static void CheckControllerHint(MainWindow window)
{
    var tabs = (TabControl)window.Content;
    var hint = (TextBlock)tabs.Template.FindName("ControllerHint", tabs);
    Check(hint.Visibility == Visibility.Collapsed, "controller hint hidden before gamepad input");
    typeof(MainWindow).GetField("_controllerConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(window, true);
    typeof(MainWindow).GetMethod("UpdateControllerHint", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(window, null);
    Check(hint.Visibility == Visibility.Collapsed, "connecting a controller alone does not show hints");
    typeof(MainWindow).GetMethod("SetControllerMode", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(window, [true]);
    Check(hint.Visibility == Visibility.Visible, "gamepad input shows controller hint");

    window.Activate();
    window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
    { RoutedEvent = Mouse.PreviewMouseDownEvent });
    Check(hint.Visibility == Visibility.Collapsed, "mouse input hides controller hint");
    typeof(MainWindow).GetMethod("SetControllerMode", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(window, [true]);
    PostMessage(new WindowInteropHelper(window).Handle, 0x0100, (IntPtr)0x87, IntPtr.Zero); // F24
    PostMessage(new WindowInteropHelper(window).Handle, 0x0101, (IntPtr)0x87, IntPtr.Zero);
    Pump();
    Check(hint.Visibility == Visibility.Collapsed, "keyboard input hides controller hint");
    typeof(MainWindow).GetField("_controllerConnected", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(window, false);
    Console.WriteLine("PASS controller hint follows active input");
}

static void CheckBusyWorkflow(string output)
{
    string directory = Path.Combine(output, "busy-state");
    var store = new SqliteStateStore(directory);
    var state = store.Load();
    state.CheckAtStartup = false;
    for (int index = 0; index < 3; index++)
    {
        string exe = Path.Combine(output, $"BusySample{index}.exe");
        File.WriteAllBytes(exe, [0x4D, 0x5A]);
        state.Games.Add(new GameEntry { Name = $"Тестовая игра {index + 1}", ExePath = exe, ProfileId = state.Profiles[0].Id });
    }
    store.Save(state);
    var slowRtss = new SlowRtss();
    var sync = new SyncService(store, _ => slowRtss, () => new SmokeNvidia(), () => []);
    var vm = new MainViewModel(store, sync);
    var window = new MainWindow(vm);
    window.Show();
    Pump();

    int crossThreadChanges = 0;
    foreach (var game in vm.Games)
        game.PropertyChanged += (_, _) =>
        {
            if (!window.Dispatcher.CheckAccess()) Interlocked.Increment(ref crossThreadChanges);
        };

    vm.ApplyCommand.Execute(null);
    Pump();
    Check(vm.IsApplyingAll && vm.SyncProgressMessage.Contains("Игра"), "apply all runs asynchronously with a clear game message");
    Check(!vm.IsActivityVisible, "activity indicator stays hidden during its anti-flicker delay");
    var tabs = window.Content as TabControl ?? throw new Exception("busy window tabs missing");
    tabs.SelectedIndex = 2;
    Pump();
    Check(tabs.SelectedIndex == 2, "navigation remains responsive while applying all games");
    Check(!Find<Button>(window).Single(button => Equals(button.Content, "Применить ко всем")).IsEnabled &&
          !Find<Button>(window).Single(button => Equals(button.Content, "Проверить")).IsEnabled &&
          !vm.AddProfileCommand.CanExecute(null) && !vm.ApplyGameCommand.CanExecute(vm.Games[0]),
        "integration and editing commands are disabled during apply all");
    Check(Find<TextBlock>(window).Any(block => block.Text.Contains("Изменение настроек временно недоступно")),
        "loader explains why editing is disabled");
    tabs.SelectedIndex = 5;
    Pump();
    Check(!Find<ComboBox>(window).Single(menu => menu.Name == "ThemeMenu").IsEnabled &&
          !Find<ComboBox>(window).Single(menu => menu.Name == "RtssLimiterMenu").IsEnabled,
        "settings stay visible but cannot change during apply all");
    PumpFor(400);
    Check(vm.IsActivityVisible, "long-running operation reveals the compact activity status after the delay");
    Capture(window, Path.Combine(output, "13-busy-apply.png"));
    tabs.SelectedIndex = 2;
    Pump();
    window.Close();
    Check(window.IsVisible && vm.IsApplyingAll, "closing cannot interrupt active application");

    var watch = Stopwatch.StartNew();
    while (vm.IsApplyingAll && watch.Elapsed < TimeSpan.FromSeconds(15))
    {
        Thread.Sleep(25);
        Pump();
    }
    Check(!vm.IsApplyingAll && vm.SyncProgressPercent == 100 && vm.Games.All(game => game.Status == ApplyStatus.Applied),
        "all games finish and progress reaches 100 percent");
    Check(crossThreadChanges == 0, "bound game notifications stay on the WPF dispatcher during background apply");
    Pump();
    Console.WriteLine($"Busy completion: notice={vm.Notice}, command={vm.ApplyCommand.CanExecute(null)}, button={Find<Button>(window).Single(button => Equals(button.Content, "Применить ко всем")).IsEnabled}");
    Check(Find<Button>(window).Single(button => Equals(button.Content, "Применить ко всем")).IsEnabled &&
          vm.Notice.Contains("Обработано игр: 3"), "commands return and completion is explained");
    window.Close();
    Console.WriteLine("PASS responsive apply all, progress, disabled writes and safe window close");

    var startupState = store.Load();
    startupState.CheckAtStartup = true;
    startupState.MinimizeToTray = true;
    store.Save(startupState);
    var startupVm = new MainViewModel(store,
        new SyncService(store, _ => new SlowRtss(), () => new SmokeNvidia(), () => []));
    var startupWindow = new MainWindow(startupVm);
    startupWindow.Show();
    Pump();
    Check(startupWindow.IsVisible && startupVm.IsApplyingAll,
        "startup verification opens the window before applying in background");
    startupWindow.Close();
    Check(!startupWindow.IsVisible && startupVm.IsApplyingAll,
        "closing to tray keeps background application running");
    watch.Restart();
    while (startupVm.IsApplyingAll && watch.Elapsed < TimeSpan.FromSeconds(15))
    {
        Thread.Sleep(25);
        Pump();
    }
    Check(!startupVm.IsApplyingAll && startupVm.Games.All(game => game.Status == ApplyStatus.Applied),
        "startup verification finishes without blocking the window");
    startupVm.MinimizeToTray = false;
    startupWindow.Close();
    Console.WriteLine("PASS background verification and reapply on startup");
}

static void CheckControllerNavigation(MainWindow window, MainViewModel vm)
{
    var tabs = (TabControl)window.Content;
    var handle = typeof(MainWindow).GetMethod("HandleControllerAction", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new Exception("Controller route missing");
    void Send(ControllerAction action)
    {
        handle.Invoke(window, [action]);
        Pump();
    }
    tabs.SelectedIndex = 0;
    window.UpdateLayout();
    Send(ControllerAction.NextPage);
    Check(tabs.SelectedIndex == 1, "controller shoulder opens screen page");
    Send(ControllerAction.NextPage);
    Check(tabs.SelectedIndex == 2, "controller shoulder opens game page");
    Send(ControllerAction.NextPage);
    Check(tabs.SelectedIndex == 3, "controller shoulder opens profile page");
    vm.SelectedProfile = vm.Profiles[0];
    var profiles = Find<ListBox>(window).First(box => box.Name == "ProfilesList");
    profiles.Focus();
    Send(ControllerAction.Down);
    Check(vm.SelectedProfile == vm.Profiles[1], "controller down selects next profile");
    Send(ControllerAction.Menu);
    Check(Keyboard.FocusedElement is Button { Name: "ApplyHeaderButton" },
        "controller menu reaches global apply button");
    vm.SelectedProfile = vm.Profiles.Last();
    Pump();
    var profileRtss = Find<ComboBox>(window).Single(box => box.Name == "ProfileRtssLimiterMenu");
    profileRtss.Focus();
    Send(ControllerAction.Accept);
    Check(profileRtss.IsDropDownOpen, "controller opens the profile RTSS selector");
    Send(ControllerAction.Back);
    Check(!profileRtss.IsDropDownOpen, "controller closes the profile RTSS selector");
    tabs.SelectedIndex = 5;
    Pump();
    var theme = Find<ComboBox>(window).First(box => box.Name == "ThemeMenu");
    theme.Focus();
    Send(ControllerAction.Accept);
    Check(theme.IsDropDownOpen, "controller accept opens selector");
    Send(ControllerAction.Back);
    Check(!theme.IsDropDownOpen, "controller back closes selector");
    Console.WriteLine("PASS controller WPF navigation, focus and dropdown actions");
}

static void CheckControllerKeyboard(MainWindow window)
{
    var tabs = (TabControl)window.Content;
    tabs.SelectedIndex = 2;
    Pump();
    var search = Find<TextBox>(window).First(box => box.Name == "GamesSearchBox");
    search.Text = "";
    search.Focus();
    var handle = typeof(MainWindow).GetMethod("HandleControllerAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var inputField = typeof(MainWindow).GetField("_controllerInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var input = inputField.GetValue(window)!;
    var received = input.GetType().GetField("ActionReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var enumeration = input.GetType().GetField("_nextEnumeration", BindingFlags.Instance | BindingFlags.NonPublic)!;
    long beforeEnumeration = (long)enumeration.GetValue(input)!;
    bool fired = false;
    string? entered = null;
    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
    timer.Tick += (_, _) =>
    {
        timer.Stop();
        var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "ControllerTextInputWindow");
        Console.WriteLine($"Keyboard dialog: open={dialog is not null}, active={dialog?.IsActive}, mainActive={window.IsActive}, inputPolls={((long)enumeration.GetValue(input)! > beforeEnumeration)}");
        if (dialog is null) return;
        ((Delegate)received.GetValue(input)!).DynamicInvoke(ControllerAction.Accept);
        entered = Find<TextBox>(dialog).Single().Text;
        ((Delegate)received.GetValue(input)!).DynamicInvoke(ControllerAction.Back);
        fired = true;
    };
    timer.Start();
    handle.Invoke(window, [ControllerAction.Accept]);
    Check(fired, "controller input reaches modal keyboard");
    Check(entered == "1", "controller accept types selected key");
    Console.WriteLine("PASS controller modal keyboard input");
}

static void CheckControllerMinimize(MainWindow window, MainViewModel vm)
{
    var handle = typeof(MainWindow).GetMethod("HandleControllerAction", BindingFlags.Instance | BindingFlags.NonPublic)!;
    vm.MinimizeToTray = true;
    handle.Invoke(window, [ControllerAction.Tertiary]);
    Pump();
    Check(!window.IsVisible && window.WindowState == WindowState.Minimized, "controller Y minimizes to tray");
    handle.Invoke(window, [ControllerAction.ToggleWindow]);
    Pump();
    Check(window.IsVisible && window.WindowState == WindowState.Normal, "controller chord restores window");
    vm.MinimizeToTray = false;
    Console.WriteLine("PASS controller minimize to tray and restore");
}

static void OpenAndClose(ComboBox menu, string? snapshot = null)
{
    menu.IsDropDownOpen = true;
    menu.UpdateLayout();
    Pump();
    var popup = menu.Template.FindName("PART_Popup", menu) as Popup;
    Console.WriteLine($"Dropdown: IsDropDownOpen={menu.IsDropDownOpen}, Popup={popup?.IsOpen}, Items={menu.Items.Count}");
    Check(popup?.IsOpen == true, "dropdown opens");
    Check(menu.Items.Count > 0, "dropdown has items");
    if (snapshot is not null && popup?.Child is FrameworkElement visual) CaptureVisual(visual, snapshot);
    menu.IsDropDownOpen = false;
}

static void Pump()
{
    var frame = new DispatcherFrame();
    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
    Dispatcher.PushFrame(frame);
}

static void PumpFor(int milliseconds)
{
    var frame = new DispatcherFrame();
    var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
    delay.Tick += (_, _) => { delay.Stop(); frame.Continue = false; };
    delay.Start();
    Dispatcher.PushFrame(frame);
}

static void WaitForImport(MainViewModel vm)
{
    var watch = Stopwatch.StartNew();
    while (vm.IsApplyingAll && watch.Elapsed < TimeSpan.FromSeconds(15))
    {
        Thread.Sleep(20);
        Pump();
    }
    Check(!vm.IsApplyingAll, "background import finishes before UI assertions");
}

static void WaitForIcons(MainViewModel vm)
{
    var watch = Stopwatch.StartNew();
    while (vm.GameCards.Any(game => game.Icon is null) && watch.Elapsed < TimeSpan.FromSeconds(15))
    {
        Thread.Sleep(20);
        Pump();
    }
    Check(vm.GameCards.All(game => game.Icon is not null), "background icon loading finishes before UI assertions");
}

static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
{
    for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
    {
        var child = VisualTreeHelper.GetChild(root, i);
        if (child is T match) yield return match;
        foreach (var descendant in Find<T>(child)) yield return descendant;
    }
}

static void Capture(Window window, string path)
{
    var frame = new DispatcherFrame();
    var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
    delay.Tick += (_, _) => { delay.Stop(); frame.Continue = false; };
    delay.Start();
    Dispatcher.PushFrame(frame);
    CaptureVisual(window, path);
}

static void CaptureVisual(FrameworkElement visual, string path)
{
    int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
    int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL " + message);
}

[StructLayout(LayoutKind.Sequential)]
private struct NativePoint { public int X; public int Y; }
[StructLayout(LayoutKind.Sequential)]
private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
[StructLayout(LayoutKind.Sequential)]
private struct MinMaxInfo
{
    public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
}
[StructLayout(LayoutKind.Sequential)]
private struct MonitorInfo
{
    public int Size;
    public NativeRect Monitor, Work;
    public uint Flags;
}
[DllImport("user32.dll")]
private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
[DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
[DllImport("dwmapi.dll")]
private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect rectangle, int size);
[DllImport("user32.dll")]
private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

sealed class BindingErrors : TraceListener
{
    public List<string> Messages { get; } = new();
    public override void Write(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message.Trim());
    }
    public override void WriteLine(string? message) => Write(message);
}

sealed class SlowRtss : IRtssSettings
{
    public string InstallDirectory => "Тестовый RTSS";
    public bool IsRunning => true;
    public bool ProfileExists(string exeName) => false;
    public int? ReadFps(string exeName) => 120;
    public int? ReadLimiterMode(string exeName) => 1;
    public void ProbeWrite(string exeName, int? targetFps) { }
    public void WriteSettings(string exeName, int fps, RtssLimiterMode mode) => Thread.Sleep(700);
    public void Restore(string exeName, bool existed, int? previousFps, int? previousLimiterMode) { }
    public void Dispose() { }
}
sealed class SmokeNvidia : INvidiaSettings
{
    public NvidiaSettingsSnapshot Read(string exePath)
        => default;
    public bool Matches(NvidiaSettingsSnapshot current, PerformanceProfile desired) => false;
    public void ProbeWrite() { }
    public void Apply(GameEntry game, PerformanceProfile desired) { }
    public void Restore(string exePath, SettingBackup backup) { }
    public void Dispose() { }
}
