using System.ComponentModel;
using System.Runtime.InteropServices;
using GameProfileManager.Integrations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace GameProfileManager;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ShellTrayIcon _trayIcon;
    private readonly SdlControllerInput _controllerInput;
    private readonly InfoBarDismissTimer _statusDismissTimer;
    private readonly InfoBarDismissTimer _rtssDismissTimer;
    private bool _exitRequested;
    private bool _leaveApproved;
    private bool _closeGuardPending;
    private bool _navigationPending;
    private bool _syncingSelection;
    private bool _dialogActive;
    private bool _controllerMode;
    private bool _controllerConnected;
    private bool _playStationController;
    private readonly NavigationDiagnostics _navigationDiagnostics = new();
    private readonly Dictionary<(string Route, Guid? ItemId), RouteViewState> _pageStates = new();
    private NavigationProbe? _pendingNavigationProbe;
    private ControllerFilePicker? _activePicker;
    internal ControllerKeyboard? ActiveKeyboard { get; set; }
    private string _route = "overview";
    private string _controllerStatus = "Контроллер не подключён";

    public MainViewModel ViewModel => _viewModel;
    public string CurrentRoute => _route;
    public string ControllerStatus => _controllerStatus;
    public bool IsPlayStationController => _playStationController;
    internal NavigationProbe? CurrentNavigationProbe => _pendingNavigationProbe;
    internal event Action? ControllerStatusChanged;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _statusDismissTimer = new InfoBarDismissTimer(StatusBar, TimeSpan.FromSeconds(7));
        _rtssDismissTimer = new InfoBarDismissTimer(RtssBar, TimeSpan.FromSeconds(15));
        _viewModel = viewModel;
        ThemeManager.Apply(viewModel.Theme);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Title = "GPM — Game Profile Manager";
        AppWindow.SetIcon("Assets/GpmBranding/ICO/gpm_dark.ico");
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
        _viewModel.PropertyChanged += OnViewModelChanged;
        UpdateRtssBar();
        UpdateActivityBanner();

        _trayIcon = new ShellTrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this),
            Path.Combine(AppContext.BaseDirectory, "Assets", "GpmBranding", "ICO", "gpm_dark.ico"),
            RestoreWindow,
            () => _viewModel.VerifyCommand.Execute(null),
            () => _viewModel.ApplyCommand.Execute(null), ExitFromTray);

        _controllerInput = new SdlControllerInput();
        _controllerInput.ActionReceived += HandleControllerAction;
        _controllerInput.StatusChanged += UpdateControllerStatus;
        _controllerInput.Start();
        _viewModel.PickLocalFileAsync = PickLocalFileAsync;
        RootLayout.SizeChanged += (_, args) =>
            AppTitleFullName.Visibility = args.NewSize.Width < 640 ? Visibility.Collapsed : Visibility.Visible;
        RootLayout.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnRootKeyDown), true);
        RootLayout.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), true);
        Navigate("overview");
    }

    private void UpdateControllerStatus(string status, bool connected, bool playStation)
    {
        _controllerStatus = connected ? status : string.IsNullOrWhiteSpace(status) ? "Контроллер не подключён" : status;
        _controllerConnected = connected;
        _playStationController = connected && playStation;
        ControllerLegend.Text = _playStationController
            ? "✕ — выбрать     ○ — назад / закрыть список     Options — меню     L1/R1 — разделы     Крестовина — перемещение"
            : "A — выбрать     B — назад / закрыть список     Start — меню     LB/RB — разделы     D-pad — перемещение";
        ControllerLegend.Visibility = connected && _controllerMode ? Visibility.Visible : Visibility.Collapsed;
        ControllerStatusChanged?.Invoke();
    }

    public void MaximizeForApplicationStartup()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    public bool StartHiddenIfTrayAvailable()
    {
        if (!_trayIcon.IsAvailable) return false;
        AppWindow.Hide();
        _controllerInput.SetBackgroundMode(true);
        return true;
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
            if (_navigationPending) return;
            if (!await page.CanLeaveAsync()) return;
            _leaveApproved = true;
            Close();
        }
        catch (Exception ex) { _viewModel.Notice = "Ошибка закрытия: " + ex.Message; }
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
            _statusDismissTimer.Show(StatusBar.Severity == InfoBarSeverity.Error
                ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(7));
        }
        if (args.PropertyName == nameof(MainViewModel.Theme)) ThemeManager.Apply(_viewModel.Theme);
        if (args.PropertyName is nameof(MainViewModel.RtssWarningVisible) or nameof(MainViewModel.RtssWarning))
            UpdateRtssBar();
        if (args.PropertyName is nameof(MainViewModel.IsActivityVisible) or nameof(MainViewModel.SyncProgressMessage)
            or nameof(MainViewModel.SyncProgressPercent) or nameof(MainViewModel.SyncProgressTotal)
            or nameof(MainViewModel.IsApplyingAll))
            UpdateActivityBanner();
    }

    private void UpdateRtssBar()
    {
        RtssBar.Title = _viewModel.RtssStatus?.StartsWith("RTSS недоступен", StringComparison.Ordinal) == true
            ? "RTSS недоступен" : "RTSS закрыт";
        RtssBar.Message = _viewModel.RtssWarning;
        if (_viewModel.RtssWarningVisible) _rtssDismissTimer.Show();
        else _rtssDismissTimer.Hide();
        StartRtssButton.Visibility = RtssClient.ProcessIsRunning ? Visibility.Collapsed : Visibility.Visible;
        StartRtssButton.IsEnabled = _viewModel.StartRtssCommand.CanExecute(null);
    }

    private void UpdateActivityBanner()
    {
        bool visible = _viewModel.IsActivityVisible;
        ActivityBanner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ActivityText.Text = _viewModel.SyncProgressMessage;
        ActivityProgressBar.IsIndeterminate = _viewModel.SyncProgressTotal == 0;
        ActivityProgressBar.Value = _viewModel.SyncProgressPercent;
        StartRtssButton.IsEnabled = _viewModel.StartRtssCommand.CanExecute(null);
    }

    private void StartRtssButton_Click(object sender, RoutedEventArgs args) =>
        _viewModel.StartRtssCommand.Execute(null);

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection) return;
        if (args.IsSettingsSelected) Navigate("settings");
        else if (args.SelectedItemContainer?.Tag is string route) Navigate(route);
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

    private void PaneToggleButton_Click(object sender, RoutedEventArgs args) =>
        NavView.IsPaneOpen = !NavView.IsPaneOpen;

    public async void Navigate(string route, Guid? itemId = null)
    {
        try { await NavigateAsync(route, itemId); }
        catch (Exception ex) { _viewModel.Notice = "Ошибка навигации: " + ex.Message; RestoreNavigationSelection(); }
    }

    public async Task NavigateAsync(string route, Guid? itemId = null)
    {
        if (string.IsNullOrWhiteSpace(route)) return;
        if (_navigationPending || _closeGuardPending) { RestoreNavigationSelection(); return; }
        _navigationPending = true;
        try
        {
            if (ContentFrame.Content is RoutePage current && !await current.CanLeaveAsync())
            { RestoreNavigationSelection(); return; }
            if (ContentFrame.Content is RoutePage leaving) RememberPageState(leaving);
            _pendingNavigationProbe = _navigationDiagnostics.Begin("navigate");
            if (!ContentFrame.Navigate(typeof(RoutePage), new RouteRequest(this, route, itemId)))
                throw new InvalidOperationException("Не удалось открыть страницу.");
            RestoreNavigationSelection();
        }
        finally { _navigationPending = false; }
    }

    public async void GoBack()
    {
        try { await GoBackAsync(); }
        catch (Exception ex) { _viewModel.Notice = "Ошибка возврата: " + ex.Message; RestoreNavigationSelection(); }
    }

    public async Task GoBackAsync()
    {
        if (TryCloseOpenComboBox()) return;
        if (!IsInternalRoute(_route)) return;
        if (_navigationPending || _closeGuardPending) { RestoreNavigationSelection(); return; }
        _navigationPending = true;
        try
        {
            if (ContentFrame.Content is RoutePage current && !await current.CanLeaveAsync())
            { RestoreNavigationSelection(); return; }
            if (ContentFrame.Content is RoutePage leaving) RememberPageState(leaving);
            _pendingNavigationProbe = _navigationDiagnostics.Begin("back");
            if (ContentFrame.CanGoBack) ContentFrame.GoBack();
            else
            {
                string parent = _route switch
                {
                    "import" or "game" => "games",
                    "profile-new" or "profile-edit" => "profiles",
                    "settings-controller" or "settings-backup" => "settings",
                    _ when _route.StartsWith("game-", StringComparison.Ordinal) => "game",
                    _ => "overview"
                };
                if (!ContentFrame.Navigate(typeof(RoutePage), new RouteRequest(this, parent,
                    parent == "game" ? _viewModel.SelectedGame?.Id : null)))
                    throw new InvalidOperationException("Не удалось вернуться на страницу.");
            }
            RestoreNavigationSelection();
        }
        finally { _navigationPending = false; }
    }

    private void RestoreNavigationSelection()
    {
        _syncingSelection = true;
        try
        {
            NavView.SelectedItem = _route == "settings" || _route.StartsWith("settings-", StringComparison.Ordinal)
                ? NavView.SettingsItem
                : NavView.MenuItems.OfType<NavigationViewItem>()
                    .FirstOrDefault(item => Equals(item.Tag, _route));
        }
        finally { _syncingSelection = false; }
    }

    private void RememberPageState(RoutePage page)
    {
        if (page.Request is not { } request || !KeepsPageState(request.Route)) return;
        _pageStates[(request.Route, request.ItemId)] = page.CaptureViewState();
    }

    internal bool TryGetPageState(RouteRequest request, out RouteViewState state)
    {
        state = default;
        return KeepsPageState(request.Route) &&
            _pageStates.TryGetValue((request.Route, request.ItemId), out state);
    }

    private static bool KeepsPageState(string route) => route is not ("profile-new" or "profile-edit");

    internal void CompleteNavigation(NavigationProbe probe, RoutePage page, PageBuildSample build)
    {
        if (!ReferenceEquals(_pendingNavigationProbe, probe) || !ReferenceEquals(ContentFrame.Content, page)) return;
        _pendingNavigationProbe = null;
        _navigationDiagnostics.Complete(probe, page.Route, build);
    }

    internal async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogActive) return ContentDialogResult.None;
        _dialogActive = true;
        try { return await dialog.ShowAsync(); }
        finally { _dialogActive = false; }
    }

    private bool TryCloseOpenComboBox()
    {
        var combo = Descendants(ContentFrame).OfType<ComboBox>()
            .FirstOrDefault(item => item.IsDropDownOpen);
        if (combo is null) return false;
        combo.IsDropDownOpen = false;
        combo.Focus(FocusState.Programmatic);
        return true;
    }

    private static bool IsInternalRoute(string route) => route is not
        ("overview" or "games" or "profiles" or "monitors" or "diagnostics" or "settings");

    internal void SetCurrentRoute(string route)
    {
        _route = route;
        NavView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
        NavView.IsBackEnabled = IsInternalRoute(route);
    }

    private void OnRootKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
    {
        if (_controllerConnected && args.Key.ToString().StartsWith("Gamepad", StringComparison.Ordinal))
        { args.Handled = true; return; }
        bool altDown = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            & CoreVirtualKeyStates.Down) != 0;
        bool textFocused = FocusManager.GetFocusedElement(RootLayout.XamlRoot) is TextBox
            or PasswordBox or RichEditBox or AutoSuggestBox;
        if (HandleBackShortcut(args.Key, altDown, textFocused)) args.Handled = true;
    }

    private bool HandleBackShortcut(VirtualKey key, bool altDown, bool textFocused)
    {
        bool canGoBack = IsInternalRoute(_route) ||
            Descendants(ContentFrame).OfType<ComboBox>().Any(item => item.IsDropDownOpen);
        if (!canGoBack || !(key is VirtualKey.Escape or VirtualKey.GoBack
            || key == VirtualKey.Left && altDown || key == VirtualKey.Back && !textFocused)) return false;
        GoBack();
        return true;
    }

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _controllerMode = false;
        ControllerLegend.Visibility = Visibility.Collapsed;
        if (HandleMouseBack(args.GetCurrentPoint(RootLayout).Properties.IsXButton1Pressed)) args.Handled = true;
    }

    private bool HandleMouseBack(bool xButtonPressed)
    {
        if (!xButtonPressed || !IsInternalRoute(_route) &&
            !Descendants(ContentFrame).OfType<ComboBox>().Any(item => item.IsDropDownOpen)) return false;
        GoBack();
        return true;
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
        DispatchControllerAction(action);
    }

    private void DispatchControllerAction(ControllerAction action)
    {
        _controllerMode = true;
        ControllerLegend.Visibility = _controllerConnected ? Visibility.Visible : Visibility.Collapsed;
        if (ActiveKeyboard is { } keyboard) { keyboard.HandleController(action); return; }
        if (_activePicker is { } picker) { picker.HandleController(action); return; }
        if (_dialogActive) return;
        if (action == ControllerAction.Menu && TryCloseOpenComboBox()) return;
        switch (action)
        {
            case ControllerAction.Menu: NavView.IsPaneOpen = !NavView.IsPaneOpen; break;
            case ControllerAction.Up: MoveControllerFocus(FocusNavigationDirection.Up); break;
            case ControllerAction.Down: MoveControllerFocus(FocusNavigationDirection.Down); break;
            case ControllerAction.Left: MoveControllerFocus(FocusNavigationDirection.Left); break;
            case ControllerAction.Right: MoveControllerFocus(FocusNavigationDirection.Right); break;
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

    private void MoveControllerFocus(FocusNavigationDirection direction)
    {
        var openCombo = Descendants(ContentFrame).OfType<ComboBox>()
            .FirstOrDefault(item => item.IsDropDownOpen);
        if (openCombo is not null)
        {
            int change = direction switch
            {
                FocusNavigationDirection.Up or FocusNavigationDirection.Left => -1,
                FocusNavigationDirection.Down or FocusNavigationDirection.Right => 1,
                _ => 0
            };
            if (change != 0 && openCombo.Items.Count > 0)
                openCombo.SelectedIndex = Math.Clamp(openCombo.SelectedIndex + change, 0, openCombo.Items.Count - 1);
            return;
        }
        if (!RootLayout.IsLoaded) return;
        if (FocusManager.GetFocusedElement(RootLayout.XamlRoot) is null)
        {
            NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault()?.Focus(FocusState.Programmatic);
            return;
        }
        FocusManager.TryMoveFocus(direction, new FindNextElementOptions { SearchRoot = RootLayout });
    }

    private async Task EditTextFromControllerAsync(TextBox text)
    {
        string? value = await ControllerKeyboard.PromptAsync(this, "Ввод текста", text.Text);
        if (value is not null) text.Text = value;
        text.Focus(FocusState.Programmatic);
    }

    private void ActivateFocusedControl()
    {
        var openCombo = Descendants(ContentFrame).OfType<ComboBox>()
            .FirstOrDefault(item => item.IsDropDownOpen);
        if (openCombo is not null)
        {
            openCombo.IsDropDownOpen = false;
            openCombo.Focus(FocusState.Programmatic);
            return;
        }
        var focused = FocusManager.GetFocusedElement(RootLayout.XamlRoot);
        switch (focused)
        {
            case Button { IsEnabled: true } button: new ButtonAutomationPeer(button).Invoke(); break;
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
        if (!_controllerMode || !_controllerConnected)
        {
            var systemPicker = new FileOpenPicker(AppWindow.Id)
            {
                CommitButtonText = "Выбрать"
            };
            systemPicker.FileTypeFilter.Add(pattern == "*.exe" ? ".exe" : ".dll");
            var result = await systemPicker.PickSingleFileAsync();
            return result?.Path;
        }
        var picker = new ControllerFilePicker(this, title, pattern, initialDirectory);
        _activePicker = picker;
        try { return await picker.ShowAsync(); }
        finally { _activePicker = null; }
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
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
