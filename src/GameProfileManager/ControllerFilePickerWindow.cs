using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;

namespace GameProfileManager;

internal sealed class ControllerFilePickerWindow : Window, IControllerSurface
{
    private readonly string _filePattern;
    private readonly TextBlock _path;
    private readonly TextBlock _message;
    private readonly ListBox _entries;
    private string? _directory;

    public string? SelectedFile { get; private set; }

    public ControllerFilePickerWindow(string title, string filePattern, string? initialDirectory)
    {
        _filePattern = filePattern;
        Title = title;
        Width = 900;
        Height = 675;
        MinWidth = 750;
        MinHeight = 540;
        WindowStyle = WindowStyle.None;
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
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chrome.Child = layout;

        var heading = new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 15) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        layout.Children.Add(heading);

        var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 13) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(toolbar, 1);
        layout.Children.Add(toolbar);
        _path = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14, Margin = new Thickness(0, 0, 12, 0) };
        _path.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        toolbar.Children.Add(_path);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(tools, 1);
        toolbar.Children.Add(tools);
        var drives = new Button { Content = "Диски", Margin = new Thickness(0, 0, 7, 0) };
        drives.Click += (_, _) => LoadDirectory(null);
        tools.Children.Add(drives);
        var up = new Button { Content = "Вверх", Margin = new Thickness(0, 0, 7, 0) };
        up.Click += (_, _) => GoUp();
        tools.Children.Add(up);
        var enterPath = new Button { Content = "Ввести путь" };
        enterPath.Click += (_, _) => EnterPath();
        tools.Children.Add(enterPath);

        _entries = new ListBox
        {
            DisplayMemberPath = nameof(FileEntry.DisplayName),
            BorderThickness = new Thickness(0),
            FontSize = 16,
            Padding = new Thickness(3)
        };
        _entries.SetResourceReference(ListBox.BackgroundProperty, "RaisedBrush");
        _entries.SetResourceReference(ListBox.ForegroundProperty, "TextBrush");
        _entries.MouseDoubleClick += (_, _) => OpenSelected();
        Grid.SetRow(_entries, 2);
        layout.Children.Add(_entries);

        _message = new TextBlock { Margin = new Thickness(0, 12, 0, 10), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(_message, 3);
        layout.Children.Add(_message);

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(footer, 4);
        layout.Children.Add(footer);
        var hint = new TextBlock
        {
            Text = "A / ✕ — открыть или выбрать   B / ○ — вверх   X / □ — ввести путь   Y / △ — диски   LT/RT — листать",
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        footer.Children.Add(hint);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        var cancel = new Button { Content = "Отмена", Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        var choose = new Button { Content = "Выбрать", Padding = new Thickness(22, 9, 22, 9) };
        choose.SetResourceReference(Button.BackgroundProperty, "AccentBrush");
        choose.Click += (_, _) => OpenSelected();
        buttons.Children.Add(choose);

        LoadDirectory(Directory.Exists(initialDirectory) ? initialDirectory : null);
        Loaded += (_, _) =>
        {
            _entries.Focus();
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        };
        PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: DialogResult = false; e.Handled = true; break;
                case Key.Back: GoUp(); e.Handled = true; break;
                case Key.Enter: OpenSelected(); e.Handled = true; break;
            }
        };
    }

    private void LoadDirectory(string? directory)
    {
        try
        {
            FileEntry[] entries;
            if (directory is null)
            {
                entries = DriveInfo.GetDrives()
                    .Where(drive => drive.IsReady)
                    .Select(drive => new FileEntry(drive.RootDirectory.FullName, true, "▸  " + drive.Name + "  " + drive.VolumeLabel))
                    .ToArray();
            }
            else
            {
                entries = Directory.GetDirectories(directory)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                    .Select(path => new FileEntry(path, true, "▸  " + Path.GetFileName(path)))
                    .Concat(Directory.GetFiles(directory, _filePattern)
                        .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                        .Select(path => new FileEntry(path, false, "•  " + Path.GetFileName(path))))
                    .ToArray();
            }
            _directory = directory;
            _path.Text = directory ?? "Диски этого компьютера";
            _entries.ItemsSource = entries;
            _entries.SelectedIndex = entries.Length == 0 ? -1 : 0;
            _message.Text = entries.Length == 0 ? "В этой папке нет подходящих файлов." :
                $"Папок и файлов: {entries.Length}. Показываются файлы {_filePattern}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _message.Text = "Не удалось открыть папку: " + ex.Message;
        }
    }

    private void GoUp()
    {
        if (_directory is null) { DialogResult = false; return; }
        LoadDirectory(Directory.GetParent(_directory)?.FullName);
    }

    private void OpenSelected()
    {
        if (_entries.SelectedItem is not FileEntry entry) return;
        if (entry.IsDirectory) { LoadDirectory(entry.Path); return; }
        if (!File.Exists(entry.Path)) { _message.Text = "Файл больше не существует. Обновите папку."; return; }
        SelectedFile = entry.Path;
        DialogResult = true;
    }

    private void EnterPath()
    {
        var keyboard = new ControllerTextInputWindow("Путь к папке или файлу", _directory ?? "") { Owner = this };
        if (keyboard.ShowDialog() != true) return;
        string path = Environment.ExpandEnvironmentVariables(keyboard.ResultText.Trim().Trim('"'));
        if (Directory.Exists(path)) { LoadDirectory(path); return; }
        if (File.Exists(path) && (Path.GetFileName(path).Equals(_filePattern, StringComparison.OrdinalIgnoreCase) ||
            _filePattern.StartsWith("*.", StringComparison.Ordinal) &&
            Path.GetExtension(path).Equals(_filePattern[1..], StringComparison.OrdinalIgnoreCase)))
        {
            SelectedFile = path;
            DialogResult = true;
            return;
        }
        _message.Text = "Папка или подходящий файл не найдены: " + path;
    }

    private void MoveSelection(int amount)
    {
        if (_entries.Items.Count == 0) return;
        _entries.SelectedIndex = Math.Clamp(_entries.SelectedIndex + amount, 0, _entries.Items.Count - 1);
        _entries.ScrollIntoView(_entries.SelectedItem);
        _entries.Focus();
    }

    public void HandleControllerAction(ControllerAction action)
    {
        switch (action)
        {
            case ControllerAction.Up: MoveSelection(-1); break;
            case ControllerAction.Down: MoveSelection(1); break;
            case ControllerAction.PageUp: MoveSelection(-8); break;
            case ControllerAction.PageDown: MoveSelection(8); break;
            case ControllerAction.Accept:
            case ControllerAction.Menu: OpenSelected(); break;
            case ControllerAction.Back: GoUp(); break;
            case ControllerAction.Secondary: EnterPath(); break;
            case ControllerAction.Tertiary: LoadDirectory(null); break;
            case ControllerAction.PreviousPage: GoUp(); break;
            case ControllerAction.NextPage: OpenSelected(); break;
        }
    }

    private sealed record FileEntry(string Path, bool IsDirectory, string DisplayName);
}
