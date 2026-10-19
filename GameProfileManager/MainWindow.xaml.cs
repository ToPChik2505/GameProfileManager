using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GameProfileManager.Core;
using Forms = System.Windows.Forms;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace GameProfileManager;

public partial class MainWindow : Window
{
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ContextMenuStrip _trayMenu;
    private bool _exitRequested;
    private bool _trayHintShown;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(MainViewModel? viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? new MainViewModel();
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Открыть", null, (_, _) => Dispatcher.BeginInvoke(RestoreWindow));
        _trayMenu.Items.Add("Проверить настройки", null, (_, _) => Dispatcher.BeginInvoke(() => ViewModel.VerifyCommand.Execute(null)));
        _trayMenu.Items.Add("Применить ко всем", null, (_, _) => Dispatcher.BeginInvoke(() => ViewModel.ApplyCommand.Execute(null)));
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ??
            (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Game Profile Manager",
            ContextMenuStrip = _trayMenu,
            Visible = ViewModel.MinimizeToTray
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreWindow);
        ViewModel.PropertyChanged += ViewModelPropertyChanged;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.MinimizeToTray))
            _trayIcon.Visible = ViewModel.MinimizeToTray;
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
        if (WindowState == WindowState.Minimized && ViewModel.MinimizeToTray) Hide();
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dark = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
    }

    protected override void OnClosed(EventArgs e)
    {
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
            sender is ComboBox { SelectedItem: ModeOption<string> option } && vm.SelectedGame is { } game)
            game.DisplayId = option.Value;
    }

    private void Fraction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && !vm.IsUpdatingFpsOptions &&
            sender is ComboBox { SelectedItem: ModeOption<int> option } && vm.SelectedProfile is { } profile)
            profile.FpsDivisor = option.Value;
    }

    private void GameProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: GameEntry game, SelectedItem: ProfileOption option })
            game.ProfileId = option.Id == Guid.Empty ? null : option.Id;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
