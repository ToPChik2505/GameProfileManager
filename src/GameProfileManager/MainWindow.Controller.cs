using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TextBox = System.Windows.Controls.TextBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GameProfileManager;

public partial class MainWindow
{
    private readonly SdlControllerInput _controllerInput = new();
    private ComboBox? _controllerCombo;
    private int _controllerComboOriginalIndex;
    private bool _controllerMode;
    private bool _controllerConnected;
    private bool _controllerIsPlayStation;

    private void InitializeController()
    {
        _controllerInput.StatusChanged += (name, connected, playStation) =>
        {
            _controllerConnected = connected;
            _controllerIsPlayStation = playStation;
            if (!connected) _controllerMode = false;
            UpdateControllerHint();
        };
        _controllerInput.ActionReceived += action =>
        {
            // Длительное сочетание возвращает скрытое окно, даже если активно другое приложение.
            if (action == ControllerAction.ToggleWindow)
            {
                if (!IsVisible || WindowState == WindowState.Minimized) RestoreWindow();
                else if (IsActive) MinimizeFromController();
                SetControllerMode(true);
                return;
            }
            // Остальные действия работают только при активном окне приложения.
            var windows = System.Windows.Application.Current.Windows.Cast<Window>().ToArray();
            if (!IsForegroundProcess() && !windows.Any(window => window.IsActive)) return;
            Window? dialog = windows.LastOrDefault(window => window is IControllerSurface && window.IsVisible);
            if (dialog is IControllerSurface surface)
            {
                SetControllerMode(true);
                surface.HandleControllerAction(action);
                return;
            }
            if (!IsActive) return;
            SetControllerMode(true);
            HandleControllerAction(action);
        };
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        PreviewMouseDown += (_, _) => SetControllerMode(false);
        PreviewMouseWheel += (_, _) => SetControllerMode(false);
        PreviewKeyDown += (_, _) => SetControllerMode(false);
        Loaded += (_, _) => _controllerInput.Start();
        ViewModel.PickLocalFile = PickLocalFile;
    }

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (!_controllerMode) return;
        var input = e.StagingItem.Input;
        if (input.RoutedEvent == Keyboard.PreviewKeyDownEvent ||
            input is MouseButtonEventArgs { RoutedEvent: var mouseEvent } &&
            mouseEvent.Name.StartsWith("PreviewMouse", StringComparison.Ordinal) ||
            input.RoutedEvent == Mouse.PreviewMouseWheelEvent)
            SetControllerMode(false);
    }

    private void SetControllerMode(bool controllerMode)
    {
        if (_controllerMode == controllerMode) return;
        _controllerMode = controllerMode;
        UpdateControllerHint();
    }

    private void UpdateControllerHint()
    {
        if (RootTabs.Template.FindName("ControllerHint", RootTabs) is not TextBlock hint) return;
        hint.Visibility = _controllerMode && _controllerConnected ? Visibility.Visible : Visibility.Collapsed;
        hint.Text = !_controllerConnected ? "" : _controllerIsPlayStation
            ? "✕ Выбрать   ○ Назад   L1/R1 Разделы   △ Свернуть   Options+Create Открыть"
            : "A Выбрать   B Назад   LB/RB Разделы   Y Свернуть   Start+View Открыть";
    }

    private void DisposeController()
    {
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        ViewModel.PickLocalFile = null;
        _controllerInput.Dispose();
    }

    private static bool IsForegroundProcess()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out uint processId);
        return processId == (uint)Environment.ProcessId;
    }

    private void MinimizeFromController()
    {
        CloseControllerCombo();
        WindowState = WindowState.Minimized;
    }

    private string? PickLocalFile(string title, string extension, string? initialDirectory)
    {
        if (!_controllerMode)
        {
            var native = new OpenFileDialog
            {
                Title = title,
                Filter = extension == "*.exe" ? "Приложения (*.exe)|*.exe" : "RTSSHooks64.dll|RTSSHooks64.dll",
                InitialDirectory = initialDirectory ?? ""
            };
            return native.ShowDialog(this) == true ? native.FileName : null;
        }
        var picker = new ControllerFilePickerWindow(title, extension, initialDirectory) { Owner = this };
        return picker.ShowDialog() == true ? picker.SelectedFile : null;
    }

    internal void HandleControllerAction(ControllerAction action)
    {
        if (_controllerCombo is { IsDropDownOpen: true } combo)
        {
            if (action is ControllerAction.Up or ControllerAction.Down)
            {
                if (combo.Items.Count == 0) return;
                int next = combo.SelectedIndex + (action == ControllerAction.Down ? 1 : -1);
                combo.SelectedIndex = Math.Clamp(next, 0, combo.Items.Count - 1);
                return;
            }
            if (action == ControllerAction.Accept)
            {
                combo.IsDropDownOpen = false;
                combo.Focus();
                _controllerCombo = null;
                return;
            }
            if (action == ControllerAction.Back)
            {
                combo.SelectedIndex = _controllerComboOriginalIndex;
                combo.IsDropDownOpen = false;
                combo.Focus();
                _controllerCombo = null;
                return;
            }
        }

        switch (action)
        {
            case ControllerAction.PreviousPage:
            case ControllerAction.NextPage:
                CloseControllerCombo();
                RootTabs.SelectedIndex = (RootTabs.SelectedIndex + (action == ControllerAction.NextPage ? 1 : RootTabs.Items.Count - 1)) % RootTabs.Items.Count;
                Dispatcher.BeginInvoke(new Action(FocusPreferredPageControl), DispatcherPriority.Loaded);
                break;
            case ControllerAction.Up:
            case ControllerAction.Down:
                MoveVertical(action == ControllerAction.Down ? 1 : -1);
                break;
            case ControllerAction.Left:
                if (RootTabs.SelectedIndex is 2 or 3 && !IsFocusInPageList()) FocusPageList();
                else MoveFocusStop(-1);
                break;
            case ControllerAction.Right:
                MoveFocusStop(1);
                break;
            case ControllerAction.PageUp:
            case ControllerAction.PageDown:
                ScrollFocusedArea(action == ControllerAction.PageDown ? 1 : -1);
                break;
            case ControllerAction.Accept:
                ActivateFocusedControl();
                break;
            case ControllerAction.Back:
                if (RootTabs.SelectedIndex is 2 or 3 && !IsFocusInPageList()) FocusPageList();
                else if (RootTabs.SelectedIndex != 0)
                {
                    RootTabs.SelectedIndex = 0;
                    Dispatcher.BeginInvoke(new Action(FocusPreferredPageControl), DispatcherPriority.Loaded);
                }
                else FocusPreferredPageControl();
                break;
            case ControllerAction.Secondary:
                if (RootTabs.SelectedIndex == 2) FocusControl(GamesSearchBox);
                else if (Keyboard.FocusedElement is TextBox textBox) OpenControllerKeyboard(textBox);
                break;
            case ControllerAction.Tertiary:
                MinimizeFromController();
                break;
            case ControllerAction.ToggleWindow:
                if (!IsVisible || WindowState == WindowState.Minimized) RestoreWindow();
                else MinimizeFromController();
                break;
            case ControllerAction.Menu:
                if (RootTabs.Template.FindName("ApplyHeaderButton", RootTabs) is Button apply)
                    FocusControl(apply);
                break;
        }
    }

    private void CloseControllerCombo()
    {
        if (_controllerCombo is { } combo) combo.IsDropDownOpen = false;
        _controllerCombo = null;
    }

    private List<FrameworkElement> FocusStops()
    {
        var result = new List<FrameworkElement>();
        if (RootTabs.Template.FindName("VerifyHeaderButton", RootTabs) is Button { IsVisible: true, IsEnabled: true } verify)
            result.Add(verify);
        if (RootTabs.Template.FindName("ApplyHeaderButton", RootTabs) is Button { IsVisible: true, IsEnabled: true } apply)
            result.Add(apply);
        foreach (string name in new[] { "MinimizeCaptionButton", "MaximizeCaptionButton", "CloseCaptionButton" })
            if (RootTabs.Template.FindName(name, RootTabs) is Button { IsVisible: true, IsEnabled: true } captionButton)
                result.Add(captionButton);
        if (RootTabs.Template.FindName("StartRtssButton", RootTabs) is Button { IsVisible: true, IsEnabled: true } rtss)
            result.Add(rtss);
        if (RootTabs.Template.FindName("PageContent", RootTabs) is not ContentPresenter page) return result;
        CollectFocusStops(page, result);
        return result;
    }

    private void CollectFocusStops(DependencyObject parent, List<FrameworkElement> result)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.IsVisible && element.IsEnabled)
            {
                bool stop = element is Button or CheckBox or ComboBox or TextBox or ListBox ||
                    ReferenceEquals(element, OverviewHeroCard);
                if (stop && element.Focusable)
                {
                    result.Add(element);
                    if (!ReferenceEquals(element, OverviewHeroCard)) continue;
                }
            }
            CollectFocusStops(child, result);
        }
    }

    private FrameworkElement? CurrentFocusStop(List<FrameworkElement> stops)
    {
        for (DependencyObject? current = Keyboard.FocusedElement as DependencyObject; current is not null;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is FrameworkElement element && stops.Contains(element)) return element;
        return null;
    }

    private void MoveVertical(int direction)
    {
        var stops = FocusStops();
        if (CurrentFocusStop(stops) is ListBox list && list.Items.Count > 0)
        {
            int next = list.SelectedIndex + direction;
            if (next >= 0 && next < list.Items.Count)
            {
                list.SelectedIndex = next;
                list.ScrollIntoView(list.SelectedItem);
                FocusControl(list);
                return;
            }
        }
        MoveFocusStop(direction);
    }

    private void MoveFocusStop(int direction)
    {
        var stops = FocusStops();
        if (stops.Count == 0) return;
        var current = CurrentFocusStop(stops);
        int index = current is null ? (direction > 0 ? -1 : stops.Count) : stops.IndexOf(current);
        FocusControl(stops[Math.Clamp(index + direction, 0, stops.Count - 1)]);
    }

    private void FocusPreferredPageControl()
    {
        FrameworkElement? preferred = RootTabs.SelectedIndex switch
        {
            0 => OverviewHeroCard,
            1 => DisplaysList,
            2 => GamesList,
            3 => ProfilesList,
            _ => FocusStops().FirstOrDefault()
        };
        if (preferred is { IsVisible: true, IsEnabled: true }) FocusControl(preferred);
        else if (FocusStops().FirstOrDefault() is { } first) FocusControl(first);
    }

    private void FocusPageList()
    {
        FrameworkElement? list = RootTabs.SelectedIndex == 2 ? GamesList :
            RootTabs.SelectedIndex == 3 ? ProfilesList : null;
        if (list is not null) FocusControl(list);
    }

    private bool IsFocusInPageList()
    {
        var current = Keyboard.FocusedElement as DependencyObject;
        return current is not null && (FindParent<ListBox>(current) is { } list &&
            (ReferenceEquals(list, GamesList) || ReferenceEquals(list, ProfilesList)) ||
            ReferenceEquals(current, GamesList) || ReferenceEquals(current, ProfilesList));
    }

    private static void FocusControl(FrameworkElement element)
    {
        if (element is ListBox list)
        {
            if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
            if (list.SelectedItem is not null)
            {
                list.ScrollIntoView(list.SelectedItem);
                list.UpdateLayout();
                if (list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item)
                {
                    item.Focus();
                    Keyboard.Focus(item);
                    item.BringIntoView();
                    return;
                }
            }
        }
        element.Focus();
        Keyboard.Focus(element);
        element.BringIntoView();
    }

    private void ScrollFocusedArea(int direction)
    {
        DependencyObject? focus = Keyboard.FocusedElement as DependencyObject;
        ScrollViewer? scroller = focus is null ? null : FindParent<ScrollViewer>(focus);
        if (scroller is null && RootTabs.Template.FindName("PageContent", RootTabs) is ContentPresenter page)
            scroller = FindDescendant<ScrollViewer>(page);
        if (scroller is not null)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + direction * Math.Max(150, scroller.ViewportHeight * 0.75));
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void ActivateFocusedControl()
    {
        var stops = FocusStops();
        var focused = CurrentFocusStop(stops);
        if (focused is null) { FocusPreferredPageControl(); return; }
        switch (focused)
        {
            case ComboBox combo:
                _controllerComboOriginalIndex = combo.SelectedIndex;
                _controllerCombo = combo;
                combo.IsDropDownOpen = true;
                break;
            case TextBox textBox:
                OpenControllerKeyboard(textBox);
                break;
            case CheckBox checkBox:
                checkBox.IsChecked = checkBox.IsChecked != true;
                break;
            case ListBox:
                MoveFocusStop(1);
                break;
            case Button button:
                ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
                break;
            default:
                if (ReferenceEquals(focused, OverviewHeroCard))
                {
                    RootTabs.SelectedIndex = 2;
                    Dispatcher.BeginInvoke(new Action(FocusPreferredPageControl), DispatcherPriority.Loaded);
                }
                break;
        }
    }

    private void OpenControllerKeyboard(TextBox textBox)
    {
        string title = textBox.Tag as string ?? "Введите текст";
        var editor = new ControllerTextInputWindow(title, textBox.Text, ReferenceEquals(textBox, ManualFpsInput))
        { Owner = this };
        if (editor.ShowDialog() == true) textBox.Text = editor.ResultText;
        FocusControl(textBox);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
