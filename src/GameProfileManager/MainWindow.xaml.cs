using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GameProfileManager.Presentation;
using GameProfileManager.Core;
using Forms = System.Windows.Forms;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GameProfileManager;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty ContentColumnsProperty = DependencyProperty.Register(
        nameof(ContentColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(2));
    public static readonly DependencyProperty OverviewStatColumnsProperty = DependencyProperty.Register(
        nameof(OverviewStatColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(3));
    public int ContentColumns { get => (int)GetValue(ContentColumnsProperty); private set => SetValue(ContentColumnsProperty, value); }
    public int OverviewStatColumns { get => (int)GetValue(OverviewStatColumnsProperty); private set => SetValue(OverviewStatColumnsProperty, value); }

    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Forms.ToolStripItem _verifyTrayItem;
    private readonly Forms.ToolStripItem _applyTrayItem;
    private readonly Forms.ToolStripItem _exitTrayItem;
    private HwndSource? _windowSource;
    private bool _exitRequested;
    private bool _trayHintShown;
    private bool _updatingWindowMinimum;
    private WindowState _restoreWindowState = WindowState.Normal;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(MainViewModel? viewModel)
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateResponsiveLayout();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
        DataContext = viewModel ?? new MainViewModel();
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Открыть", null, (_, _) => Dispatcher.BeginInvoke(RestoreWindow));
        _verifyTrayItem = _trayMenu.Items.Add("Проверить настройки", null, (_, _) => Dispatcher.BeginInvoke(() => ViewModel.VerifyCommand.Execute(null)));
        _applyTrayItem = _trayMenu.Items.Add("Применить ко всем", null, (_, _) => Dispatcher.BeginInvoke(() => ViewModel.ApplyCommand.Execute(null)));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _exitTrayItem = _trayMenu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ??
            (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = string.Empty,
            ContextMenuStrip = _trayMenu,
            Visible = ViewModel.MinimizeToTray
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreWindow);
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
        InitializeController();
        IsVisibleChanged += (_, _) => _controllerInput.SetBackgroundMode(!IsVisible);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void UpdateResponsiveLayout()
    {
        UpdateWindowMinimumSizeForMonitor();
        if (ActualWidth <= 0) return;
        bool narrow = ActualWidth < 900;
        bool medium = ActualWidth < 1150;
        ContentColumns = narrow ? 1 : 2;
        OverviewStatColumns = narrow ? 1 : 3;
        UpdateGameCardLayouts();

        GameListColumn.Width = new GridLength(narrow ? 170 : medium ? 260 : 360);
        GameGapColumn.Width = new GridLength(narrow ? 10 : 18);
        ProfileListColumn.Width = new GridLength(narrow ? 170 : medium ? 230 : 310);
        ProfileGapColumn.Width = new GridLength(narrow ? 10 : 16);
        DisplayListColumn.Width = new GridLength(narrow ? 170 : medium ? 210 : 280);
        DisplayGapColumn.Width = new GridLength(narrow ? 10 : 16);

        OverviewHeroArt.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        OverviewHeroArtColumn.Width = new GridLength(narrow ? 0 : 190);
        OverviewStatsGrid.Margin = narrow ? new Thickness(0, 0, 0, 8) : new Thickness(0, 0, -15, 19);
        OverviewGamesCard.Margin = OverviewProfilesCard.Margin = OverviewAttentionCard.Margin =
            narrow ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 15, 0);
        OverviewIntegrationsCard.Margin = narrow ? new Thickness(0, 0, 0, 12) : new Thickness(0);
        OverviewHowCard.Margin = narrow ? new Thickness(0) : new Thickness(12, 0, 0, 0);

        if (RootTabs.Template.FindName("PageContent", RootTabs) is ContentPresenter page)
            page.Margin = narrow ? new Thickness(18, 18, 18, 14) : new Thickness(30, 25, 30, 22);
    }

    private void GameCardsGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid grid) UpdateGameCardLayout(grid);
    }

    private void GameCardsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid) UpdateGameCardLayout(grid);
    }

    private void UpdateGameCardLayouts()
    {
        if (FindVisualChild<Grid>(GameInspector, "GameCardsGrid") is { } grid)
            UpdateGameCardLayout(grid);
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match && match.Name == name) return match;
            if (FindVisualChild<T>(child, name) is { } descendant) return descendant;
        }
        return null;
    }

    private void UpdateGameCardLayout(Grid grid)
    {
        bool singleColumn = ContentColumns == 1;
        GridLength secondColumnWidth = singleColumn
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        if (grid.ColumnDefinitions[1].Width != secondColumnWidth)
            grid.ColumnDefinitions[1].Width = secondColumnWidth;

        var cards = grid.Children.OfType<Border>().ToArray();
        if (cards.Length != 4) return;
        Place(cards[0], 0, 0);
        Place(cards[1], singleColumn ? 1 : 0, singleColumn ? 0 : 1);
        Place(cards[2], singleColumn ? 2 : 1, 0);
        Place(cards[3], singleColumn ? 3 : 1, singleColumn ? 0 : 1);

        void Place(Border card, int row, int column)
        {
            Grid.SetRow(card, row);
            Grid.SetColumn(card, column);
            card.VerticalAlignment = singleColumn ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        }
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.MinimizeToTray))
            _trayIcon.Visible = ViewModel.MinimizeToTray;
        if (e.PropertyName == nameof(MainViewModel.Theme))
            UpdateTitleBarTheme();
        if (e.PropertyName == nameof(MainViewModel.IsApplyingAll))
            _verifyTrayItem.Enabled = _applyTrayItem.Enabled = _exitTrayItem.Enabled = !ViewModel.IsApplyingAll;
        if (e.PropertyName == nameof(MainViewModel.Notice) && !IsVisible && ViewModel.MinimizeToTray &&
            (ViewModel.Notice.Contains("RTSS закрыт", StringComparison.OrdinalIgnoreCase) ||
             ViewModel.Notice.Contains("проверьте статусы", StringComparison.OrdinalIgnoreCase)))
        {
            _trayIcon.BalloonTipTitle = "Game Profile Manager";
            _trayIcon.BalloonTipText = ViewModel.Notice;
            _trayIcon.ShowBalloonTip(5000);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ViewModel.IsApplyingAll && (_exitRequested || !ViewModel.MinimizeToTray))
        {
            e.Cancel = true;
            _exitRequested = false;
            ViewModel.Notice = "Применение ещё идёт. Дождитесь завершения обработки игр.";
            return;
        }
        if (!_exitRequested && !Dispatcher.HasShutdownStarted && ViewModel.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.BalloonTipTitle = "Game Profile Manager";
                _trayIcon.BalloonTipText = "Приложение работает в трее. Двойной щелчок вернёт окно.";
                _trayIcon.ShowBalloonTip(4000);
            }
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState != WindowState.Minimized)
            _restoreWindowState = WindowState;
        if (WindowState == WindowState.Minimized && ViewModel.MinimizeToTray) Hide();
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = _restoreWindowState;
        Activate();
    }

    internal void MaximizeForApplicationStartup()
    {
        _restoreWindowState = WindowState.Maximized;
        WindowState = WindowState.Maximized;
    }

    internal void RestoreFromSecondLaunch()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) RestoreWindow();
        if (!Activate())
        {
            Topmost = true;
            Topmost = false;
            Activate();
        }
    }

    private void MinimizeCaption_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeCaption_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseCaption_Click(object sender, RoutedEventArgs e) => Close();

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);
        UpdateTitleBarTheme();
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmGetMinMaxInfo = 0x0024;
        const int wmDpiChanged = 0x02E0;
        if (message == wmDpiChanged)
        {
            // WPF processes WM_DPICHANGED and updates its device transform. Refresh
            // responsive thresholds and size limits after that update, without consuming the message.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(UpdateResponsiveLayout));
            return IntPtr.Zero;
        }
        if (message != wmGetMinMaxInfo || lParam == IntPtr.Zero) return IntPtr.Zero;

        IntPtr monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var bounds = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        bounds.MaxPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
        bounds.MaxPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
        // MINMAXINFO uses physical pixels. Bound both maximize and interactive resize
        // to this monitor's work area; the nearest monitor can have another DPI.
        int maxWidth = Math.Max(1, monitorInfo.Work.Right - monitorInfo.Work.Left - 1);
        int maxHeight = Math.Max(1, monitorInfo.Work.Bottom - monitorInfo.Work.Top - 1);
        bounds.MaxSize.X = maxWidth;
        bounds.MaxSize.Y = maxHeight;
        bounds.MaxTrackSize.X = maxWidth;
        bounds.MaxTrackSize.Y = maxHeight;

        // WPF minimums are in DIPs. Convert using the HWND's current monitor DPI,
        // then clamp them so a high scale factor cannot force the window off-screen.
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1d : dpi / 96d;
        int minWidth = (int)Math.Ceiling(MinWidth * scale);
        int minHeight = (int)Math.Ceiling(MinHeight * scale);
        bounds.MinTrackSize.X = Math.Min(maxWidth, Math.Max(bounds.MinTrackSize.X, minWidth));
        bounds.MinTrackSize.Y = Math.Min(maxHeight, Math.Max(bounds.MinTrackSize.Y, minHeight));
        Marshal.StructureToPtr(bounds, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private void UpdateWindowMinimumSizeForMonitor()
    {
        if (_updatingWindowMinimum) return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        IntPtr monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        if (monitor == IntPtr.Zero) return;
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return;

        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1d : dpi / 96d;
        double availableWidth = Math.Max(1, monitorInfo.Work.Right - monitorInfo.Work.Left - 1) / scale;
        double availableHeight = Math.Max(1, monitorInfo.Work.Bottom - monitorInfo.Work.Top - 1) / scale;
        double minWidth = Math.Min(800, availableWidth);
        double minHeight = Math.Min(680, availableHeight);
        if (Math.Abs(MinWidth - minWidth) < 0.01 && Math.Abs(MinHeight - minHeight) < 0.01) return;

        _updatingWindowMinimum = true;
        try
        {
            MinWidth = minWidth;
            MinHeight = minHeight;
        }
        finally { _updatingWindowMinimum = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void UpdateTitleBarTheme()
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        var dark = ViewModel.Theme == "Dark" ? 1 : 0;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        var roundedCorners = 2; // DWMWCP_ROUND в Windows 11.
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref roundedCorners, sizeof(int));
    }

    private void RootTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != RootTabs || !SystemParameters.ClientAreaAnimation) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (RootTabs.Template.FindName("PageContent", RootTabs) is not ContentPresenter page) return;
            page.RenderTransform = new TranslateTransform(0, 8);
            page.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase() });
            ((TranslateTransform)page.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = new QuadraticEase() });
        });
    }

    private void RootTabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindParent<ComboBox>(e.OriginalSource as DependencyObject) is not { IsDropDownOpen: false } combo)
            return;

        // Закрытый список сохраняет выбор; прокручивается окружающая страница.
        e.Handled = true;
        if (FindParent<ScrollViewer>(VisualTreeHelper.GetParent(combo)) is { } scroller)
        {
            scroller.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = scroller
            });
        }
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        for (var node = child; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is T result) return result;
        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        DisposeController();
        ViewModel.PropertyChanged -= ViewModelPropertyChanged;
        _trayIcon.Visible = false;
        _trayIcon.Icon?.Dispose();
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        ViewModel.Dispose();
        base.OnClosed(e);
    }

    private void OpenScreen_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SelectedDisplay = vm.OverviewDisplay;
        RootTabs.SelectedIndex = 1;
    }
    private void OpenGames_Click(object sender, RoutedEventArgs e) => RootTabs.SelectedIndex = 2;
    private void OpenProfiles_Click(object sender, RoutedEventArgs e) => RootTabs.SelectedIndex = 3;
    private void OpenAttention_Click(object sender, RoutedEventArgs e) => RootTabs.SelectedIndex = 4;
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => RootTabs.SelectedIndex = 5;

    private void HeroCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (CameFromButton(e.OriginalSource, sender)) return;
        RootTabs.SelectedIndex = 2;
        e.Handled = true;
    }

    private void HeroCard_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || CameFromButton(e.OriginalSource, sender)) return;
        RootTabs.SelectedIndex = 2;
        e.Handled = true;
    }

    private static bool CameFromButton(object? source, object container)
    {
        for (DependencyObject? node = source as DependencyObject; node is not null && node != container;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is ButtonBase) return true;
        return false;
    }

    private void AttentionAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AttentionItem item }) return;
        if (item.Game is not null) ViewModel.SelectedGame = item.Game;
        RootTabs.SelectedIndex = item.PageIndex;
    }

    private void DefaultDisplay_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsUpdatingDisplayOptions &&
            sender is ComboBox { SelectedItem: ModeOption<string> option })
            vm.DefaultDisplayId = option.Value;
    }

    private void GameDisplay_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsUpdatingDisplayOptions &&
            sender is ComboBox { DataContext: GameEntryViewModel game, SelectedItem: ModeOption<string> option })
            game.Model.DisplayId = option.Value;
    }

    private void GameDisplayProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsUpdatingDisplayOptions &&
            sender is ComboBox { DataContext: GameEntryViewModel game, SelectedItem: ProfileOption option })
            vm.SetDisplayProfileAssignment(game.Model, game.Model.DisplayProfileTargetId,
                option.Id == Guid.Empty ? null : option.Id);
    }

    private void GameProfileAssignmentDisplay_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsUpdatingDisplayOptions &&
            sender is ComboBox { DataContext: GameEntryViewModel game, SelectedItem: ModeOption<string> option })
            game.DisplayProfileTargetId = option.Value;
    }

    private void Fraction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsUpdatingFpsOptions &&
            sender is ComboBox { SelectedItem: ModeOption<int> option } && vm.SelectedProfile is { } profile)
            profile.FpsDivisor = option.Value;
    }

    private void GameProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: GameEntryViewModel game, SelectedItem: ProfileOption option })
            game.Model.ProfileId = option.Id == Guid.Empty ? null : option.Id;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
