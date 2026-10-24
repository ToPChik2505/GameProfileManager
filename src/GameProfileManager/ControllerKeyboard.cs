using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GameProfileManager;

internal sealed class ControllerKeyboard
{
    private readonly ContentDialog _dialog;
    private readonly TextBox _text;
    private readonly Grid _keys = new() { ColumnSpacing = 4, RowSpacing = 4 };
    private readonly List<Button> _buttons = new();
    private bool _russian = true;
    private bool _dialogOpened;
    private int _selection;
    private string? _result;

    private ControllerKeyboard(MainWindow owner, string title, string value)
    {
        _dialog = new ContentDialog
        {
            XamlRoot = owner.Content.XamlRoot, Title = title,
            CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Close
        };
        _text = new TextBox { Text = value, MinHeight = 40 };
        var content = new StackPanel { Spacing = 10, MinWidth = 300 };
        content.Children.Add(_text);
        content.Children.Add(new TextBlock { Text = owner.IsPlayStationController
            ? "Крестовина — выбор · ✕ — нажать · □ — удалить · △ — RU/EN · Options — готово · ○ — отмена"
            : "D-pad — выбор · A — нажать · X — удалить · Y — RU/EN · Start — готово · B — отмена" });
        content.Children.Add(_keys);
        _dialog.Content = new ScrollViewer { Content = content, MaxHeight = 510 };
        _dialog.Opened += (_, _) =>
        {
            _dialogOpened = true;
            // ContentDialog assigns its initial focus as it finishes opening. Defer
            // our focus request so that it wins over that final focus pass.
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusSelectedKey);
        };
        BuildKeys();
    }

    public static async Task<string?> PromptAsync(MainWindow owner, string title, string value)
    {
        var keyboard = new ControllerKeyboard(owner, title, value);
        owner.ActiveKeyboard = keyboard;
        try { await owner.ShowDialogAsync(keyboard._dialog); return keyboard._result; }
        finally { owner.ActiveKeyboard = null; }
    }

    private void BuildKeys()
    {
        _keys.Children.Clear();
        _keys.RowDefinitions.Clear();
        _keys.ColumnDefinitions.Clear();
        _buttons.Clear();
        for (int col = 0; col < 10; col++) _keys.ColumnDefinitions.Add(new ColumnDefinition());
        var labels = "1234567890".Select(character => character.ToString())
            .Concat((_russian ? "йцукенгшщзхъфывапролджэячсмитьбю" : "qwertyuiopasdfghjklzxcvbnm")
                .Select(character => character.ToString()))
            .Concat(["Пробел", "⌫", "Очистить", "RU/EN", "-", "_", ".", "\\", "Готово", "Отмена"])
            .ToArray();
        for (int row = 0; row < (labels.Length + 9) / 10; row++)
            _keys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int index = 0; index < labels.Length; index++)
        {
            string label = labels[index];
            var button = new Button { Content = label, MinHeight = 42, MinWidth = 32, Padding = new Thickness(4) };
            button.Click += (_, _) =>
            {
                int selected = _buttons.IndexOf(button);
                if (selected >= 0)
                {
                    _selection = selected;
                    UpdateSelectionVisuals();
                }
                Press(label);
            };
            Grid.SetRow(button, index / 10);
            Grid.SetColumn(button, index % 10);
            _keys.Children.Add(button);
            _buttons.Add(button);
        }
        _selection = Math.Clamp(_selection, 0, _buttons.Count - 1);
        UpdateSelectionVisuals();
        if (_dialogOpened) FocusSelectedKey();
    }

    private void FocusSelectedKey()
    {
        if ((uint)_selection >= (uint)_buttons.Count) return;
        var button = _buttons[_selection];
        button.Focus(FocusState.Programmatic);
        button.StartBringIntoView();
    }

    private void UpdateSelectionVisuals()
    {
        Brush? selectedBrush = GetThemeBrush("AccentFillColorDefaultBrush");
        Brush? normalBrush = GetThemeBrush("ControlStrokeColorDefaultBrush");
        Brush? selectedBackground = GetThemeBrush("ControlFillColorSecondaryBrush");
        Brush? normalBackground = GetThemeBrush("ControlFillColorDefaultBrush");

        for (int index = 0; index < _buttons.Count; index++)
        {
            bool selected = index == _selection;
            _buttons[index].BorderBrush = selected ? selectedBrush : normalBrush;
            _buttons[index].BorderThickness = new Thickness(selected ? 2 : 1);
            _buttons[index].Background = selected ? selectedBackground : normalBackground;
        }
    }

    private static Brush? GetThemeBrush(string key) =>
        Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out object value) ? value as Brush : null;

    private void Press(string key)
    {
        switch (key)
        {
            case "Готово": _result = _text.Text; _dialog.Hide(); break;
            case "Отмена": _dialog.Hide(); break;
            case "⌫": if (_text.Text.Length > 0) _text.Text = _text.Text[..^1]; break;
            case "Очистить": _text.Text = ""; break;
            case "RU/EN": _russian = !_russian; BuildKeys(); break;
            case "Пробел": _text.Text += " "; break;
            default: _text.Text += key; break;
        }
    }

    public void HandleController(ControllerAction action)
    {
        if (_buttons.Count == 0) return;
        int row = _selection / 10;
        int column = _selection % 10;
        switch (action)
        {
            case ControllerAction.Up: row = Math.Max(0, row - 1); break;
            case ControllerAction.Down: row = Math.Min((_buttons.Count - 1) / 10, row + 1); break;
            case ControllerAction.Left: column = Math.Max(0, column - 1); break;
            case ControllerAction.Right: column = Math.Min(9, column + 1); break;
            case ControllerAction.Accept: Press((string)_buttons[_selection].Content); return;
            case ControllerAction.Secondary: Press("⌫"); return;
            case ControllerAction.Tertiary: Press("RU/EN"); return;
            case ControllerAction.Menu: Press("Готово"); return;
            case ControllerAction.Back: _dialog.Hide(); return;
        }
        _selection = Math.Min(row * 10 + column, _buttons.Count - 1);
        UpdateSelectionVisuals();
        FocusSelectedKey();
    }
}
