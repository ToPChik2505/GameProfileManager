using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GameProfileManager;

/// <summary>WinUI file browser shared by mouse, keyboard and SDL controller input.</summary>
internal sealed class ControllerFilePicker
{
    private sealed record Entry(string Path, bool IsDirectory, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly MainWindow _owner;
    private readonly string _pattern;
    private readonly ContentDialog _dialog;
    private readonly ListView _list = new() { MinHeight = 240, MaxHeight = 430, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock _location = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _error = new() { IsOpen = false, Severity = InfoBarSeverity.Warning, IsClosable = false };
    private string? _directory;
    private string? _result;

    public ControllerFilePicker(MainWindow owner, string title, string pattern, string? initialDirectory)
    {
        _owner = owner;
        _pattern = pattern;
        _directory = Directory.Exists(initialDirectory) ? initialDirectory : null;
        _dialog = new ContentDialog
        {
            XamlRoot = owner.Content.XamlRoot,
            Title = title,
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close
        };
        var layout = new StackPanel { Spacing = 10, MinWidth = 300 };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(Button("Диски", () => _ = LoadAsync(null)));
        toolbar.Children.Add(Button("Вверх", () => _ = LoadAsync(Directory.GetParent(_directory ?? "")?.FullName)));
        layout.Children.Add(toolbar);
        layout.Children.Add(_location);
        var pathInput = new TextBox { PlaceholderText = "Путь к файлу или папке" };
        layout.Children.Add(pathInput);
        layout.Children.Add(Button("Перейти по пути", async () =>
        {
            string path = pathInput.Text.Trim();
            if (Directory.Exists(path)) await LoadAsync(path);
            else if (File.Exists(path) && Matches(path)) { _result = path; _dialog.Hide(); }
            else ShowError("Файл или папка не найдены.");
        }));
        layout.Children.Add(_error);
        layout.Children.Add(_list);
        _list.DoubleTapped += (_, _) => OpenSelected();
        layout.Children.Add(Button("Открыть выбранное", OpenSelected));
        _dialog.Content = new ScrollViewer { Content = layout, MaxHeight = 560 };
    }

    public async Task<string?> ShowAsync()
    {
        await LoadAsync(_directory);
        await _dialog.ShowAsync();
        return _result;
    }

    public void HandleController(ControllerAction action)
    {
        switch (action)
        {
            case ControllerAction.Up:
            case ControllerAction.Down:
                if (_list.Items.Count > 0)
                    _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + (action == ControllerAction.Down ? 1 : -1), 0, _list.Items.Count - 1);
                break;
            case ControllerAction.Accept: OpenSelected(); break;
            case ControllerAction.Back: _dialog.Hide(); break;
            case ControllerAction.Secondary: _ = LoadAsync(null); break;
        }
    }

    private async Task LoadAsync(string? directory)
    {
        try
        {
            Entry[] entries = await Task.Run(() => directory is null
                ? DriveInfo.GetDrives().Where(drive => drive.IsReady)
                    .Select(drive => new Entry(drive.RootDirectory.FullName, true, drive.Name)).ToArray()
                : Directory.EnumerateDirectories(directory)
                    .Select(path => new Entry(path, true, "📁 " + Path.GetFileName(path)))
                    .Concat(Directory.EnumerateFiles(directory, _pattern)
                        .Select(path => new Entry(path, false, Path.GetFileName(path))))
                    .OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Label).ToArray());
            _directory = directory;
            _location.Text = directory ?? "Диски";
            _list.ItemsSource = entries;
            _list.SelectedIndex = entries.Length > 0 ? 0 : -1;
            _error.IsOpen = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { ShowError("Не удалось открыть папку: " + ex.Message); }
    }

    private void OpenSelected()
    {
        if (_list.SelectedItem is not Entry entry) return;
        if (entry.IsDirectory) { _ = LoadAsync(entry.Path); return; }
        if (!Matches(entry.Path)) { ShowError("Выберите подходящий файл."); return; }
        _result = entry.Path;
        _dialog.Hide();
    }

    private bool Matches(string path) => _pattern == "*.exe"
        ? Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        : Path.GetFileName(path).Equals("RTSSHooks64.dll", StringComparison.OrdinalIgnoreCase);

    private void ShowError(string message) { _error.Message = message; _error.IsOpen = true; }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, MinHeight = 40 };
        button.Click += (_, _) => action();
        return button;
    }
}
