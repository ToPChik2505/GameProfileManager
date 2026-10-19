using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace GameProfileManager;

internal sealed class ControllerTextInputWindow : Window, IControllerSurface
{
    private readonly bool _numeric;
    private readonly TextBox _input;
    private readonly TextBlock _error;
    private readonly UniformGrid _keyGrid;
    private readonly List<Button> _keys = new();
    private bool _russian = true;
    private int _selected;

    public string ResultText => _input.Text;

    public ControllerTextInputWindow(string title, string value, bool numeric = false)
    {
        _numeric = numeric;
        Title = title;
        Width = numeric ? 570 : 790;
        Height = numeric ? 355 : 625;
        MinWidth = Width;
        MinHeight = Height;
        MaxWidth = Width;
        MaxHeight = Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");

        var chrome = new Border { Padding = new Thickness(24), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1) };
        chrome.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        chrome.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");
        Content = chrome;
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chrome.Child = layout;

        var heading = new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 17) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        layout.Children.Add(heading);

        _input = new TextBox { Text = value, FontSize = 18, MinHeight = 46, Margin = new Thickness(0, 0, 0, 9) };
        _input.CaretIndex = _input.Text.Length;
        Grid.SetRow(_input, 1);
        layout.Children.Add(_input);

        var status = new StackPanel { Margin = new Thickness(0, 0, 0, 13) };
        Grid.SetRow(status, 2);
        layout.Children.Add(status);
        var hint = new TextBlock
        {
            Text = numeric
                ? "Крестовина — выбор   A / ✕ — нажать   X / □ — удалить   Start / Options — готово   B / ○ — отмена"
                : "Крестовина — выбор   A / ✕ — нажать   X / □ — удалить   Y / △ — RU/EN   Start / Options — готово   B / ○ — отмена",
            FontSize = 11, TextWrapping = TextWrapping.Wrap
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        status.Children.Add(hint);
        _error = new TextBlock { FontSize = 12, Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed };
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        status.Children.Add(_error);

        _keyGrid = new UniformGrid { Columns = numeric ? 5 : 10 };
        Grid.SetRow(_keyGrid, 3);
        layout.Children.Add(_keyGrid);
        BuildKeys();
        Loaded += (_, _) =>
        {
            FocusKey(0);
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    private void BuildKeys()
    {
        string[] characters = _numeric
            ? ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"]
            : [.."1234567890".Select(character => character.ToString()),
               ..(_russian ? "йцукенгшщзхъфывапролджэячсмитьбю" : "qwertyuiopasdfghjklzxcvbnm")
                 .Select(character => character.ToString()),
               "-", "_", ".", ",", ":", "\\", "/"];
        string[] commands = _numeric
            ? ["⌫", "Очистить", "Готово", "Отмена"]
            : ["Пробел", "⌫", "Очистить", "RU/EN", "←", "→", "Готово", "Отмена"];
        _keyGrid.Children.Clear();
        _keys.Clear();
        foreach (string label in characters.Concat(commands))
        {
            var button = new Button
            {
                Content = label, Tag = label, FontSize = label.Length > 1 ? 12 : 17,
                MinHeight = _numeric ? 49 : 45, Margin = new Thickness(3)
            };
            button.Click += (_, _) => PerformKey(label);
            _keys.Add(button);
            _keyGrid.Children.Add(button);
        }
        _selected = Math.Clamp(_selected, 0, _keys.Count - 1);
    }

    private void PerformKey(string key)
    {
        _error.Visibility = Visibility.Collapsed;
        switch (key)
        {
            case "Готово":
                if (_numeric && (!int.TryParse(_input.Text, out int fps) || fps is < 1 or > 1000))
                {
                    _error.Text = "Лимит FPS должен быть от 1 до 1000.";
                    _error.Visibility = Visibility.Visible;
                    return;
                }
                DialogResult = true;
                return;
            case "Отмена": DialogResult = false; return;
            case "⌫":
                if (_input.SelectionLength > 0) ReplaceSelection("");
                else if (_input.CaretIndex > 0)
                {
                    int index = _input.CaretIndex;
                    _input.Text = _input.Text.Remove(index - 1, 1);
                    _input.CaretIndex = index - 1;
                }
                return;
            case "Очистить": _input.Text = ""; _input.CaretIndex = 0; return;
            case "RU/EN": _russian = !_russian; BuildKeys(); FocusKey(_selected); return;
            case "Пробел": ReplaceSelection(" "); return;
            case "←": _input.CaretIndex = Math.Max(0, _input.CaretIndex - 1); return;
            case "→": _input.CaretIndex = Math.Min(_input.Text.Length, _input.CaretIndex + 1); return;
            default: ReplaceSelection(key); return;
        }
    }

    private void ReplaceSelection(string value)
    {
        int index = _input.SelectionStart;
        _input.Text = _input.Text.Remove(index, _input.SelectionLength).Insert(index, value);
        _input.CaretIndex = index + value.Length;
    }

    private void FocusKey(int index)
    {
        if (_keys.Count == 0) return;
        _keys[_selected].ClearValue(Button.BackgroundProperty);
        _keys[_selected].ClearValue(Button.BorderBrushProperty);
        _keys[_selected].ClearValue(Button.BorderThicknessProperty);
        _selected = Math.Clamp(index, 0, _keys.Count - 1);
        _keys[_selected].SetResourceReference(Button.BackgroundProperty, "SelectedBrush");
        _keys[_selected].SetResourceReference(Button.BorderBrushProperty, "AccentBrush");
        _keys[_selected].BorderThickness = new Thickness(2);
        _keys[_selected].Focus();
        Keyboard.Focus(_keys[_selected]);
    }

    public void HandleControllerAction(ControllerAction action)
    {
        switch (action)
        {
            case ControllerAction.Left: FocusKey(_selected - 1); break;
            case ControllerAction.Right: FocusKey(_selected + 1); break;
            case ControllerAction.Up: FocusKey(_selected - _keyGrid.Columns); break;
            case ControllerAction.Down: FocusKey(_selected + _keyGrid.Columns); break;
            case ControllerAction.Accept: PerformKey((string)_keys[_selected].Tag); break;
            case ControllerAction.Secondary: PerformKey("⌫"); break;
            case ControllerAction.Tertiary: if (!_numeric) PerformKey("RU/EN"); break;
            case ControllerAction.PreviousPage: PerformKey("←"); break;
            case ControllerAction.NextPage: PerformKey("→"); break;
            case ControllerAction.PageUp: PerformKey("Очистить"); break;
            case ControllerAction.PageDown: PerformKey("Пробел"); break;
            case ControllerAction.Menu: PerformKey("Готово"); break;
            case ControllerAction.Back: DialogResult = false; break;
        }
    }
}
