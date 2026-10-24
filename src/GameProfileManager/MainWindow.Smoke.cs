using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.System;

namespace GameProfileManager;

public sealed partial class MainWindow
{
    internal async Task RunSmokeAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (ControllerAction action in new[] { ControllerAction.Up, ControllerAction.Down,
            ControllerAction.Left, ControllerAction.Right, ControllerAction.PreviousPage,
            ControllerAction.NextPage })
            DispatchControllerAction(action);
        Navigate("overview");
        await WaitForRouteAsync("overview");
        var controllerOpenGames = Descendants(ContentFrame).OfType<Button>()
            .First(button => button.Content is "Открыть мои игры");
        controllerOpenGames.Focus(FocusState.Programmatic);
        DispatchControllerAction(ControllerAction.Accept);
        await WaitForRouteAsync("games");
        bool controllerFocusAndAcceptWorks = FocusManager.GetFocusedElement(RootLayout.XamlRoot) is not null
            && CurrentRoute == "games";
        if (!controllerFocusAndAcceptWorks)
            throw new InvalidOperationException("Controller focus or button activation failed.");
        string screenshotDirectory = Path.Combine(outputDirectory, "screenshots");
        Directory.CreateDirectory(screenshotDirectory);
        var routes = new[] { "overview", "games", "profiles", "monitors", "diagnostics", "settings",
            "settings-controller", "settings-backup", "import", "profile-new", "game", "game-fps",
            "game-nvidia", "game-rtss", "game-graphics" };
        Guid? gameId = _viewModel.Games.FirstOrDefault()?.Id;
        var observed = new List<string>();
        double[] widePairs = [];
        AppWindow.Resize(SmokePixels(1440, 900));
        foreach (string theme in new[] { "Dark", "Light" })
        {
            _viewModel.Theme = theme;
            RootLayout.Background = new SolidColorBrush(theme == "Dark"
                ? new Windows.UI.Color { A = 255, R = 32, G = 32, B = 32 }
                : new Windows.UI.Color { A = 255, R = 250, G = 250, B = 250 });
            StatusBar.IsOpen = false;
            foreach (string route in routes)
            {
                Navigate(route, route.StartsWith("game", StringComparison.Ordinal) ? gameId : null);
                await WaitForRouteAsync(route);
                if (ContentFrame.Content is not RoutePage || CurrentRoute != route)
                    throw new InvalidOperationException("Route failed: " + route);
                if (theme == "Dark") observed.Add(route);
                if (theme == "Dark" && route == "overview")
                    widePairs = Descendants(ContentFrame).OfType<Grid>()
                        .Where(grid => grid.Name == "ResponsivePair").Select(grid => grid.ActualWidth).ToArray();
                await CaptureAsync(Path.Combine(screenshotDirectory, theme + "-" + route + ".png"));
            }
        }
        bool graphicsUnavailable = Descendants(ContentFrame).OfType<TextBlock>()
            .Any(text => text.Text?.Contains("недоступ", StringComparison.OrdinalIgnoreCase) == true);
        if (!graphicsUnavailable) throw new InvalidOperationException("Graphics availability message is missing.");
        bool iconLoaded = _viewModel.GameCards.FirstOrDefault()?.Icon is not null;
        if (!iconLoaded) throw new InvalidOperationException("Game EXE icon did not load in WinUI.");
        bool shellChromeReady = AppTitleBar.LeftHeader is StackPanel && NavView.OpenPaneLength == 248
            && RtssBar.IsOpen && StartRtssButton.Content is "Запустить RTSS";
        if (!shellChromeReady) throw new InvalidOperationException("WinUI shell chrome or RTSS action is missing.");
        bool gameColumnsLabeled = _viewModel.GameCards.FirstOrDefault()?.FpsListDisplay.Contains("FPS") == true;
        if (!gameColumnsLabeled) throw new InvalidOperationException("Game FPS column lacks a unit.");
        Navigate("settings");
        await WaitForRouteAsync("settings");
        UpdateControllerStatus("DualSense", true, true);
        bool dualSenseLayoutWorks = IsPlayStationController && ControllerLegend.Text.Contains("✕ — выбрать")
            && ControllerLegend.Text.Contains("○ — назад");
        bool paneWasOpen = NavView.IsPaneOpen;
        DispatchControllerAction(ControllerAction.Menu);
        bool controllerMenuWorks = NavView.IsPaneOpen != paneWasOpen;
        DispatchControllerAction(ControllerAction.Menu);
        controllerMenuWorks &= NavView.IsPaneOpen == paneWasOpen;
        var settingsCombo = Descendants(ContentFrame).OfType<ComboBox>().First();
        settingsCombo.Focus(FocusState.Programmatic);
        settingsCombo.IsDropDownOpen = true;
        DispatchControllerAction(ControllerAction.Back);
        bool controllerDropdownCloses = !settingsCombo.IsDropDownOpen && CurrentRoute == "settings";
        if (!dualSenseLayoutWorks || !controllerDropdownCloses || !controllerMenuWorks)
            throw new InvalidOperationException("DualSense labels or controller dropdown return failed.");
        UpdateControllerStatus("", false, false);
        bool rootBackDisabled = NavView.IsBackButtonVisible == NavigationViewBackButtonVisible.Visible
            && !NavView.IsBackEnabled && !HandleBackShortcut(VirtualKey.GoBack, false, false)
            && !HandleMouseBack(true);
        Navigate("settings-controller");
        await WaitForRouteAsync("settings-controller");
        bool controllerBackEnabled = NavView.IsBackButtonVisible == NavigationViewBackButtonVisible.Visible
            && NavView.IsBackEnabled;
        NavView_BackRequested(NavView, null!);
        await WaitForRouteAsync("settings");
        bool controllerReturnWorks = rootBackDisabled && controllerBackEnabled
            && CurrentRoute == "settings" && !NavView.IsBackEnabled;
        if (!controllerReturnWorks) throw new InvalidOperationException("Controller settings back navigation failed.");
        Navigate("games");
        await WaitForRouteAsync("games");
        Navigate("import");
        await WaitForRouteAsync("import");
        bool importBackVisible = NavView.IsBackButtonVisible == NavigationViewBackButtonVisible.Visible
            && NavView.IsBackEnabled && !Descendants(ContentFrame).OfType<Button>()
                .Any(button => button.Content is "Вернуться к играм");
        bool keyboardBackHandled = HandleBackShortcut(VirtualKey.GoBack, false, false);
        await WaitForRouteAsync("games");
        Navigate("import");
        await WaitForRouteAsync("import");
        bool mouseBackHandled = HandleMouseBack(true);
        await WaitForRouteAsync("games");
        Navigate("import");
        await WaitForRouteAsync("import");
        NavView_BackRequested(NavView, null!);
        await WaitForRouteAsync("games");
        bool importReturnWorks = importBackVisible && keyboardBackHandled && mouseBackHandled
            && CurrentRoute == "games" && NavView.IsBackButtonVisible == NavigationViewBackButtonVisible.Visible
            && !NavView.IsBackEnabled;
        if (!importReturnWorks) throw new InvalidOperationException("Import return navigation failed.");
        var actualSizes = new List<string>();
        bool firstSize = true;
        bool narrowSizeAccepted = true;
        foreach (var size in new[] { (1440, 900), (1280, 720), (1024, 768), (800, 600), (640, 720), (420, 680) })
        {
            AppWindow.Resize(SmokePixels(size.Item1, size.Item2));
            if (firstSize)
            {
                Navigate("overview");
                await WaitForRouteAsync("overview");
                firstSize = false;
            }
            else await Task.Delay(650);
            actualSizes.Add($"{AppWindow.Size.Width}x{AppWindow.Size.Height}");
            if (size.Item1 <= 640)
                narrowSizeAccepted &= AppWindow.Size.Width <= SmokePixels(size.Item1 + 5, size.Item2).Width;
            await CaptureAsync(Path.Combine(screenshotDirectory, $"Light-overview-{size.Item1}x{size.Item2}.png"));
        }
        foreach (var size in new[] { (960, 560), (640, 720), (420, 680) })
        {
            AppWindow.Resize(SmokePixels(size.Item1, size.Item2));
            foreach (string route in new[] { "games", "game", "profile-new" })
            {
                Navigate(route, route == "game" ? gameId : null);
                await WaitForRouteAsync(route);
                await CaptureAsync(Path.Combine(screenshotDirectory,
                    $"Light-{route}-{size.Item1}x{size.Item2}.png"));
            }
        }
        if (!narrowSizeAccepted) throw new InvalidOperationException("Compact window width was clamped above requested Snap size.");
        var gameForActions = _viewModel.Games.First();
        Navigate("game", gameForActions.Id);
        await WaitForRouteAsync("game");
        var monitorToggle = Descendants(ContentFrame).OfType<ToggleSwitch>().First(toggle =>
            toggle.Header is "Профиль по монитору");
        var assignmentPanel = Descendants(ContentFrame).OfType<StackPanel>().First(panel =>
            panel.Name == "DisplayAssignments");
        bool monitorInitiallyHidden = assignmentPanel.Visibility == Visibility.Collapsed;
        monitorToggle.IsOn = true;
        bool monitorAppearsImmediately = assignmentPanel.Visibility == Visibility.Visible;
        await CaptureAsync(Path.Combine(screenshotDirectory, "Light-game-monitor-toggle.png"));
        monitorToggle.IsOn = false;
        string alternativeExe = Path.Combine(outputDirectory, "UiAlternative.exe");
        File.Copy(gameForActions.ExePath, alternativeExe);
        _viewModel.PickLocalFileAsync = (_, _, _) => Task.FromResult<string?>(alternativeExe);
        _viewModel.ChooseExeCommand.Execute(null);
        await Task.Delay(100);
        bool exeSelectionWorks = gameForActions.ExePath == alternativeExe &&
            Descendants(ContentFrame).OfType<TextBlock>().Any(text => text.Text == alternativeExe);
        _viewModel.RestoreCommand.Execute(null);
        await Task.Delay(100);
        var smokeStore = new GameProfileManager.Infrastructure.SqliteStateStore(Path.Combine(outputDirectory, "state"));
        var persistedGame = smokeStore.Load().Games.Single(item => item.Id == gameForActions.Id);
        bool restoreClearsProfile = gameForActions.ProfileId is null && !gameForActions.AutoSwitchProfile
            && gameForActions.DisplayProfileAssignments.Count == 0 &&
            _viewModel.GameCards.First().ProfileDisplay == "Без профиля" &&
            Descendants(ContentFrame).OfType<ComboBox>().First().SelectedItem is ComboBoxItem
                { Tag: Guid selectedProfileId } && selectedProfileId == Guid.Empty &&
            persistedGame.ProfileId is null && !persistedGame.AutoSwitchProfile;
        if (!monitorInitiallyHidden || !monitorAppearsImmediately || !exeSelectionWorks || !restoreClearsProfile)
            throw new InvalidOperationException("Game assignment, EXE selection or restore UI failed.");
        Navigate("profile-new");
        await WaitForRouteAsync("profile-new");
        var draftName = Descendants(ContentFrame).OfType<TextBox>().First();
        draftName.Text = "UI Smoke профиль";
        bool draftIsolated = !smokeStore.Load().Profiles.Any(profile => profile.Name == draftName.Text);
        var saveButton = Descendants(ContentFrame).OfType<Button>()
            .First(button => button.Content is string label && label == "Сохранить профиль");
        ((Action)saveButton.Tag)();
        await WaitForRouteAsync("profiles");
        var savedProfile = _viewModel.Profiles.FirstOrDefault(profile => profile.Name == "UI Smoke профиль")
            ?? throw new InvalidOperationException("Editor did not save requested name. Text=" + draftName.Text +
                "; notice=" + _viewModel.Notice + "; profiles=" +
                string.Join(", ", _viewModel.Profiles.Select(profile => profile.Name)));
        await CaptureAsync(Path.Combine(screenshotDirectory, "Light-profiles-custom.png"));
        bool editorSaved = smokeStore.Load().Profiles.Any(profile => profile.Id == savedProfile.Id);
        Navigate("profile-edit", savedProfile.Id);
        await WaitForRouteAsync("profile-edit");
        Descendants(ContentFrame).OfType<TextBox>().First().Text = "Несохранённый черновик";
        bool discardPreserved = smokeStore.Load().Profiles.Any(profile =>
            profile.Id == savedProfile.Id && profile.Name == "UI Smoke профиль");
        bool loaderVisible = false;
        await _viewModel.RunUserActionAsync("Проверка индикатора…", async () =>
        {
            await Task.Delay(700);
            loaderVisible = _viewModel.IsActivityVisible && ActivityBanner.Visibility == Visibility.Visible
                && ActivityRing.IsActive && ActivityText.Text == "Проверка индикатора…";
            await CaptureAsync(Path.Combine(screenshotDirectory, "Light-activity-loader.png"));
        });
        bool loaderShownAndHidden = loaderVisible && !_viewModel.IsActivityVisible
            && ActivityBanner.Visibility == Visibility.Collapsed && !ActivityRing.IsActive;
        if (!loaderShownAndHidden) throw new InvalidOperationException("Shared activity loader did not open and close.");
        if (!draftIsolated || !editorSaved || !discardPreserved)
            throw new InvalidOperationException("Profile editor draft/persistence assertion failed.");
        if (SmokeAudit.WriteAttempts != 0)
            throw new InvalidOperationException("Navigation attempted external writes: " + SmokeAudit.WriteAttempts);
        File.WriteAllText(Path.Combine(outputDirectory, "smoke-result.json"),
            JsonSerializer.Serialize(new { routes = observed, graphicsUnavailable, iconLoaded,
                shellChromeReady, gameColumnsLabeled, loaderShownAndHidden, importReturnWorks, controllerReturnWorks,
                controllerFocusAndAcceptWorks, dualSenseLayoutWorks, controllerDropdownCloses, controllerMenuWorks,
                monitorAppearsImmediately, exeSelectionWorks, restoreClearsProfile,
                externalWriteAttempts = SmokeAudit.WriteAttempts, screenshots = Directory.GetFiles(screenshotDirectory).Length,
                actualSizes, narrowSizeAccepted, widePairs, draftIsolated, editorSaved, discardPreserved,
                navigationSamples = _navigationDiagnostics.Snapshot(),
                responsiveWidth = Descendants(ContentFrame).OfType<Grid>()
                    .Where(grid => grid.Name == "ResponsivePair").Select(grid => grid.ActualWidth).ToArray() },
                new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        _leaveApproved = true;
        Close();
    }

    private SizeInt32 SmokePixels(int width, int height)
    {
        double scale = ShellTrayIcon.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        return new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    private async Task WaitForRouteAsync(string route)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (ContentFrame.Content is RoutePage page && page.Route == route && page.IsLoaded)
            { await Task.Delay(650); return; }
            await Task.Delay(25);
        }
        throw new TimeoutException("WinUI did not render route: " + route);
    }

    private async Task CaptureAsync(string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(RootLayout);
        var pixels = await bitmap.GetPixelsAsync();
        CryptographicBuffer.CopyToByteArray(pixels, out byte[] bytes);
        using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }
}
