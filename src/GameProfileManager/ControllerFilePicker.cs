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
    private readonly bool _multiple;
    private readonly ContentDialog _dialog;
    private readonly ListView _list = new() { MinHeight = 240, MaxHeight = 430, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock _location = new() { TextWrapping = TextWrapping.Wrap };
    private readonly InfoBar _error = new() { IsOpen = false, Severity = InfoBarSeverity.Warning, IsClosable = true };
    private readonly Button _upButton;
    private CancellationTokenSource? _loadCancellation;
    private int _loadGeneration;
    private bool _closed;
    private string? _directory;
    private string? _result;
    private readonly List<string> _results = [];

    public ControllerFilePicker(MainWindow owner, string title, string pattern, string? initialDirectory, bool multiple = false)
    {
        _owner = owner;
        _pattern = pattern;
        _multiple = multiple;
        _list.SelectionMode = multiple ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Single;
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
        _upButton = Button("Вверх", GoUp);
        _upButton.IsEnabled = _directory is not null;
        toolbar.Children.Add(_upButton);
        layout.Children.Add(toolbar);
        layout.Children.Add(_location);
        var pathInput = new TextBox { PlaceholderText = "Путь к файлу или папке" };
        layout.Children.Add(pathInput);
        layout.Children.Add(Button("Перейти по пути", async () =>
        {
            string path = pathInput.Text.Trim();
            if (Directory.Exists(path)) await LoadAsync(path);
            else if (File.Exists(path) && Matches(path))
            {
                if (_multiple)
                {
                    if (!_results.Contains(path, StringComparer.OrdinalIgnoreCase)) _results.Add(path);
                    ShowError($"Добавлено файлов: {_results.Count}. Нажмите «Готово», чтобы завершить.");
                }
                else { _result = path; _dialog.Hide(); }
            }
            else ShowError("Файл или папка не найдены.");
        }));
        layout.Children.Add(_error);
        layout.Children.Add(_list);
        _list.DoubleTapped += (_, _) =>
        {
            if (!_multiple) OpenSelected();
            else if (_list.SelectedItem is Entry { IsDirectory: true } directory) _ = LoadAsync(directory.Path);
            else AddSelectedFiles();
        };
        if (multiple)
        {
            layout.Children.Add(Button("Добавить выбранные файлы", AddSelectedFiles));
            layout.Children.Add(Button("Готово", () => _dialog.Hide()));
        }
        else layout.Children.Add(Button("Открыть выбранное", OpenSelected));
        _dialog.Content = new ScrollViewer { Content = layout, MaxHeight = 560 };
        _dialog.Closed += (_, _) =>
        {
            _closed = true;
            _loadCancellation?.Cancel();
        };
    }

    public async Task<string?> ShowAsync()
    {
        await LoadAsync(_directory);
        await _owner.ShowDialogAsync(_dialog);
        return _result;
    }

    public async Task<IReadOnlyList<string>> ShowManyAsync()
    {
        await LoadAsync(_directory);
        await _owner.ShowDialogAsync(_dialog);
        return _results.ToArray();
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
            case ControllerAction.Accept:
                if (_multiple) AddSelectedFiles(); else OpenSelected();
                break;
            case ControllerAction.Back: _dialog.Hide(); break;
            case ControllerAction.Secondary: _ = LoadAsync(null); break;
        }
    }

    private async Task LoadAsync(string? directory)
    {
        if (_closed) return;
        int generation = ++_loadGeneration;
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        try
        {
            Entry[] entries = await Task.Run(() => directory is null
                ? DriveInfo.GetDrives().Where(drive => drive.IsReady)
                    .Select(drive => new Entry(drive.RootDirectory.FullName, true, drive.Name)).ToArray()
                : Directory.EnumerateDirectories(directory)
                    .Select(path => new Entry(path, true, "📁 " + Path.GetFileName(path)))
                    .Concat(Directory.EnumerateFiles(directory, _pattern)
                        .Select(path => new Entry(path, false, Path.GetFileName(path))))
                    .OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Label).ToArray(), cancellation.Token);
            if (_closed || generation != _loadGeneration || cancellation.IsCancellationRequested) return;
            _directory = directory;
            _location.Text = directory ?? "Диски";
            _upButton.IsEnabled = directory is not null;
            _list.ItemsSource = entries;
            _list.SelectedIndex = entries.Length > 0 ? 0 : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_closed && generation == _loadGeneration)
                ShowError("Не удалось открыть папку: " + ex.Message);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void GoUp()
    {
        if (_directory is null) return;
        try
        {
            if (!Directory.Exists(_directory))
            {
                ShowError("Текущая папка больше не доступна.");
                return;
            }
            _ = LoadAsync(Directory.GetParent(_directory)?.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { ShowError("Не удалось открыть родительскую папку: " + ex.Message); }
    }

    private void OpenSelected()
    {
        if (_list.SelectedItem is not Entry entry) return;
        if (entry.IsDirectory) { _ = LoadAsync(entry.Path); return; }
        if (!Matches(entry.Path)) { ShowError("Выберите подходящий файл."); return; }
        _result = entry.Path;
        _dialog.Hide();
    }

    private void AddSelectedFiles()
    {
        foreach (var entry in _list.SelectedItems.OfType<Entry>())
        {
            if (entry.IsDirectory) continue;
            if (!Matches(entry.Path)) { ShowError("Выберите поддерживаемые файлы конфигурации."); return; }
            if (!_results.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)) _results.Add(entry.Path);
        }
        if (_results.Count > 0) ShowError($"Добавлено файлов: {_results.Count}. Выберите ещё или нажмите «Готово».");
    }

    private bool Matches(string path) => _pattern switch
    {
        "*.exe" => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase),
        "RTSSHooks64.dll" => Path.GetFileName(path).Equals("RTSSHooks64.dll", StringComparison.OrdinalIgnoreCase),
        "*.zip" => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase),
        "*" or "*.*" => true,
        _ => Path.GetExtension(path).Equals(_pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase)
    };

    private void ShowError(string message) { _error.Message = message; _error.IsOpen = true; }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, MinHeight = 40 };
        button.Click += (_, _) => action();
        return button;
    }
}
