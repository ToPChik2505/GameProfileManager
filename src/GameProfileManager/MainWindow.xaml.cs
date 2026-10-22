using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.System;

namespace GameProfileManager;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ShellTrayIcon _trayIcon;
    private readonly SdlControllerInput _controllerInput;
    private bool _exitRequested;
    private bool _leaveApproved;
    private bool _closeGuardPending;
    private bool _controllerMode;
    private bool _controllerConnected;
    private ControllerFilePicker? _activePicker;
    internal ControllerKeyboard? ActiveKeyboard { get; set; }
    private string _route = "overview";
    private string _controllerStatus = "Контроллер не подключён";

    public MainViewModel ViewModel => _viewModel;
    public string CurrentRoute => _route;
    public string ControllerStatus => _controllerStatus;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        ThemeManager.Apply(viewModel.Theme);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/app.ico");
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
        _viewModel.PropertyChanged += OnViewModelChanged;

        _trayIcon = new ShellTrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this),
            Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"),
            RestoreWindow,
            () => _viewModel.VerifyCommand.Execute(null),
            () => _viewModel.ApplyCommand.Execute(null), ExitFromTray);

        _controllerInput = new SdlControllerInput();
        _controllerInput.ActionReceived += HandleControllerAction;
        _controllerInput.StatusChanged += (status, connected, _) =>
        {
            _controllerStatus = connected ? status : string.IsNullOrWhiteSpace(status) ? "Контроллер не подключён" : status;
            _controllerConnected = connected;
            ControllerLegend.Visibility = connected && _controllerMode ? Visibility.Visible : Visibility.Collapsed;
        };
        _controllerInput.Start();
        _viewModel.PickLocalFileAsync = PickLocalFileAsync;
        RootLayout.KeyDown += OnRootKeyDown;
        RootLayout.PointerPressed += (_, _) =>
        {
            _controllerMode = false;
            ControllerLegend.Visibility = Visibility.Collapsed;
        };
        Navigate("overview");
    }

    public void MaximizeForApplicationStartup()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    public void RestoreFromSecondLaunch() => RestoreWindow();

    private void RestoreWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Restore();
        Activate();
        _controllerInput.SetBackgroundMode(false);
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        RestoreWindow();
        Close();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_exitRequested && _viewModel.MinimizeToTray && _trayIcon.IsAvailable)
        {
            args.Cancel = true;
            AppWindow.Hide();
            _controllerInput.SetBackgroundMode(true);
            return;
        }
        if (!_leaveApproved && ContentFrame.Content is RoutePage page)
        {
            args.Cancel = true;
            if (!_closeGuardPending) _ = ConfirmCloseAsync(page);
        }
    }

    private async Task ConfirmCloseAsync(RoutePage page)
    {
        _closeGuardPending = true;
        try
        {
            if (!await page.CanLeaveAsync()) return;
            _leaveApproved = true;
            Close();
        }
        finally { _closeGuardPending = false; }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _controllerInput.Dispose();
        _viewModel.Dispose();
        _trayIcon.Dispose();
        ((App)Microsoft.UI.Xaml.Application.Current).ReleaseInstance();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Notice))
        {
            StatusBar.Message = _viewModel.Notice;
            StatusBar.Severity = _viewModel.Notice.StartsWith("Ошибка", StringComparison.OrdinalIgnoreCase)
                ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
            StatusBar.IsOpen = true;
        }
        if (args.PropertyName == nameof(MainViewModel.Theme)) ThemeManager.Apply(_viewModel.Theme);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) Navigate("settings");
        else if (args.SelectedItemContainer?.Tag is string route) Navigate(route);
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

    public async void Navigate(string route, Guid? itemId = null)
    {
        if (string.IsNullOrWhiteSpace(route)) return;
        if (ContentFrame.Content is RoutePage current && !await current.CanLeaveAsync()) return;
        _route = route;
        ContentFrame.Navigate(typeof(RoutePage), new RouteRequest(this, route, itemId));
    }

    public async void GoBack()
    {
        if (ContentFrame.Content is RoutePage current && !await current.CanLeaveAsync()) return;
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
        else
        {
            _route = "overview";
            ContentFrame.Navigate(typeof(RoutePage), new RouteRequest(this, "overview", null));
        }
    }

    internal void SetCurrentRoute(string route) => _route = route;

    private void OnRootKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (_controllerConnected && args.Key.ToString().StartsWith("Gamepad", StringComparison.Ordinal))
        { args.Handled = true; return; }
        if (args.Key == VirtualKey.Escape) { GoBack(); args.Handled = true; }
    }

    private void HandleControllerAction(ControllerAction action)
    {
        if (action == ControllerAction.ToggleWindow)
        {
            if (!AppWindow.IsVisible) RestoreWindow();
            else if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
            return;
        }
        if (GetForegroundWindow() != WinRT.Interop.WindowNative.GetWindowHandle(this)) return;
        _controllerMode = true;
        ControllerLegend.Visibility = _controllerConnected ? Visibility.Visible : Visibility.Collapsed;
        if (ActiveKeyboard is { } keyboard) { keyboard.HandleController(action); return; }
        if (_activePicker is { } picker) { picker.HandleController(action); return; }
        switch (action)
        {
            case ControllerAction.Up: FocusManager.TryMoveFocus(FocusNavigationDirection.Up); break;
            case ControllerAction.Down: FocusManager.TryMoveFocus(FocusNavigationDirection.Down); break;
            case ControllerAction.Left: FocusManager.TryMoveFocus(FocusNavigationDirection.Left); break;
            case ControllerAction.Right: FocusManager.TryMoveFocus(FocusNavigationDirection.Right); break;
            case ControllerAction.Back: GoBack(); break;
            case ControllerAction.PreviousPage: CycleTopLevel(-1); break;
            case ControllerAction.NextPage: CycleTopLevel(1); break;
            case ControllerAction.PageUp: ScrollFocusedArea(-1); break;
            case ControllerAction.PageDown: ScrollFocusedArea(1); break;
            case ControllerAction.Secondary:
                if (FocusManager.GetFocusedElement(RootLayout.XamlRoot) is TextBox text)
                    _ = EditTextFromControllerAsync(text);
                break;
            case ControllerAction.Tertiary:
                if (AppWindow.Presenter is OverlappedPresenter minimize) minimize.Minimize();
                break;
            case ControllerAction.Accept:
                ActivateFocusedControl();
                break;
        }
    }

    private async Task EditTextFromControllerAsync(TextBox text)
    {
        string? value = await ControllerKeyboard.PromptAsync(this, "Ввод текста", text.Text);
        if (value is not null) text.Text = value;
        text.Focus(FocusState.Programmatic);
    }

    private void ActivateFocusedControl()
    {
        var focused = FocusManager.GetFocusedElement(RootLayout.XamlRoot);
        switch (focused)
        {
            case Button { Tag: Action invoke }: invoke(); break;
            case NavigationViewItem item: NavView.SelectedItem = item; break;
            case ComboBox combo: combo.IsDropDownOpen = !combo.IsDropDownOpen; break;
            case ToggleSwitch toggle: toggle.IsOn = !toggle.IsOn; break;
            case TextBox text: _ = EditTextFromControllerAsync(text); break;
            case ListViewItem listItem when listItem.Content is GameProfileManager.Presentation.GameEntryViewModel card:
                _viewModel.SelectedGameCard = card; Navigate("game", card.Id); break;
            case ListViewItem listItem when listItem.Content is GameProfileManager.Core.PerformanceProfile profile:
                _viewModel.SelectedProfile = profile; Navigate("profile-edit", profile.Id); break;
        }
    }

    private void ScrollFocusedArea(int direction)
    {
        var element = FocusManager.GetFocusedElement(RootLayout.XamlRoot) as DependencyObject;
        while (element is not null && element is not ScrollViewer) element = VisualTreeHelper.GetParent(element);
        if (element is not ScrollViewer scroll)
            scroll = Descendants(ContentFrame).OfType<ScrollViewer>().FirstOrDefault()!;
        if (scroll is not null) scroll.ChangeView(null, scroll.VerticalOffset + direction * 320, null);
    }

    private void CycleTopLevel(int direction)
    {
        var items = NavView.MenuItems.OfType<NavigationViewItem>().ToArray();
        int index = Array.FindIndex(items, item => Equals(item.Tag, _route));
        int next = (index + direction + items.Length) % items.Length;
        NavView.SelectedItem = items[next];
    }

    private async Task<string?> PickLocalFileAsync(string title, string pattern, string? initialDirectory)
    {
        var picker = new ControllerFilePicker(this, title, pattern, initialDirectory);
        _activePicker = picker;
        try { return await picker.ShowAsync(); }
        finally { _activePicker = null; }
    }

    public async Task CheckUpdatesAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameProfileManager/1.0");
            using var response = await client.GetAsync("https://api.github.com/repos/Vladislav2505/GameProfileManager/releases/latest");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string tag = json.RootElement.GetProperty("tag_name").GetString()
                ?? throw new FormatException("У опубликованной версии нет номера.");
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                throw new FormatException("У опубликованной версии неверный номер.");
            var current = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
            if (latest <= current)
            {
                _viewModel.Notice = $"Установлена актуальная версия {current.ToString(3)}.";
                return;
            }
            _viewModel.Notice = $"Доступна версия {latest}.";
            if (await PageFactory.ConfirmUpdate(this, latest.ToString()))
                Process.Start(new ProcessStartInfo("https://github.com/Vladislav2505/GameProfileManager/releases/tag/" +
                    Uri.EscapeDataString(tag)) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException or Win32Exception)
        {
            _viewModel.Notice = "Ошибка проверки обновлений. Проверьте подключение к GitHub и повторите попытку.";
        }
    }

    internal async Task RunSmokeAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
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
        var actualSizes = new List<string>();
        bool firstSize = true;
        foreach (var size in new[] { (1280, 720), (1024, 768), (960, 560) })
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
            await CaptureAsync(Path.Combine(screenshotDirectory, $"Light-overview-{size.Item1}x{size.Item2}.png"));
        }
        Navigate("profile-new");
        await WaitForRouteAsync("profile-new");
        var draftName = Descendants(ContentFrame).OfType<TextBox>().First();
        draftName.Text = "UI Smoke профиль";
        var smokeStore = new GameProfileManager.Infrastructure.SqliteStateStore(Path.Combine(outputDirectory, "state"));
        bool draftIsolated = !smokeStore.Load().Profiles.Any(profile => profile.Name == draftName.Text);
        var saveButton = Descendants(ContentFrame).OfType<Button>()
            .First(button => button.Content is string label && label == "Сохранить профиль");
        ((Action)saveButton.Tag)();
        await WaitForRouteAsync("profiles");
        var savedProfile = _viewModel.Profiles.FirstOrDefault(profile => profile.Name == "UI Smoke профиль")
            ?? throw new InvalidOperationException("Editor did not save requested name. Text=" + draftName.Text +
                "; notice=" + _viewModel.Notice + "; profiles=" +
                string.Join(", ", _viewModel.Profiles.Select(profile => profile.Name)));
        bool editorSaved = smokeStore.Load().Profiles.Any(profile => profile.Id == savedProfile.Id);
        Navigate("profile-edit", savedProfile.Id);
        await WaitForRouteAsync("profile-edit");
        Descendants(ContentFrame).OfType<TextBox>().First().Text = "Несохранённый черновик";
        bool discardPreserved = smokeStore.Load().Profiles.Any(profile =>
            profile.Id == savedProfile.Id && profile.Name == "UI Smoke профиль");
        if (!draftIsolated || !editorSaved || !discardPreserved)
            throw new InvalidOperationException("Profile editor draft/persistence assertion failed.");
        if (SmokeAudit.WriteAttempts != 0)
            throw new InvalidOperationException("Navigation attempted external writes: " + SmokeAudit.WriteAttempts);
        File.WriteAllText(Path.Combine(outputDirectory, "smoke-result.json"),
            JsonSerializer.Serialize(new { routes = observed, graphicsUnavailable,
                externalWriteAttempts = SmokeAudit.WriteAttempts, screenshots = Directory.GetFiles(screenshotDirectory).Length,
                actualSizes, widePairs, draftIsolated, editorSaved, discardPreserved,
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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}

public sealed record RouteRequest(MainWindow Owner, string Route, Guid? ItemId);
