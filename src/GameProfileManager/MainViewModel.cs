using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using GameProfileManager.Core;
using GameProfileManager.Application;
using GameProfileManager.Infrastructure;
using GameProfileManager.Integrations;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GameProfileManager;

public sealed partial class MainViewModel : Observable, IDisposable
{
    private readonly SqliteStateStore _store;
    private readonly SyncService _sync;
    private readonly SyncOperationCoordinator _syncCoordinator;
    private readonly AppState _state;
    private DisplayInfo[] _displaySnapshot = [];
    private readonly DisplayChangeMonitor _displayMonitor = new();
    private bool _displayPollInFlight;
    private bool _rtssPollInFlight;
    private bool _disposed;
    private readonly Dictionary<Guid, RtssLimiterMode> _effectiveRtssModes = new();
    private readonly ObservableCollection<DisplayInfo> _displays = new();
    private DisplayInfo? _selectedDisplay;
    private string _displayStatus = "Проверка экранов...";
    private string _search = "";
    private string _statusFilter = "Все статусы";
    private string _notice = "Готово";
    private GameEntry? _selectedGame;
    private PerformanceProfile? _selectedProfile;
    private bool _busy;
    private bool _isApplyingAll;
    private string _syncProgressMessage = "";
    private double _syncProgressPercent;
    private readonly DispatcherTimer _rtssTimer = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _lastRtssProcessState;

    public ObservableCollection<GameEntry> Games => _state.Games;
    public ObservableCollection<DisplayInfo> Displays => _displays;
    public ObservableCollection<ModeOption<string>> DefaultDisplayOptions { get; } = new();
    public ObservableCollection<ModeOption<string>> GameDisplayOptions { get; } = new();
    public ObservableCollection<ModeOption<int>> FpsFractions { get; } = new();
    public IReadOnlyList<ModeOption<string>> ThemeOptions { get; } =
    [
        new("Light", "Светлая"),
        new("Dark", "Тёмная")
    ];
    public IReadOnlyList<ModeOption<RtssLimiterMode>> RtssLimiterModes { get; } =
    [
        new(RtssLimiterMode.Async, ProfileLabels.Rtss(RtssLimiterMode.Async)),
        new(RtssLimiterMode.FrontEdgeSync, ProfileLabels.Rtss(RtssLimiterMode.FrontEdgeSync)),
        new(RtssLimiterMode.BackEdgeSync, ProfileLabels.Rtss(RtssLimiterMode.BackEdgeSync)),
        new(RtssLimiterMode.NvidiaReflex, ProfileLabels.Rtss(RtssLimiterMode.NvidiaReflex))
    ];
    public IReadOnlyList<ModeOption<int>> ProfileRtssLimiterModes { get; } =
    [
        new(-1, "Наследовать общий режим"),
        new((int)RtssLimiterMode.Async, ProfileLabels.Rtss(RtssLimiterMode.Async)),
        new((int)RtssLimiterMode.FrontEdgeSync, ProfileLabels.Rtss(RtssLimiterMode.FrontEdgeSync)),
        new((int)RtssLimiterMode.BackEdgeSync, ProfileLabels.Rtss(RtssLimiterMode.BackEdgeSync)),
        new((int)RtssLimiterMode.NvidiaReflex, ProfileLabels.Rtss(RtssLimiterMode.NvidiaReflex))
    ];
    public ObservableCollection<AttentionItem> AttentionItems { get; } = new();
    public bool IsUpdatingDisplayOptions { get; private set; }
    public bool IsUpdatingFpsOptions { get; private set; }
    public IReadOnlyList<ModeOption<FpsLimitMode>> FpsModes { get; } =
    [
        new(FpsLimitMode.Manual, "Ввести FPS вручную"),
        new(FpsLimitMode.DisplayFraction, "Доля частоты экрана")
    ];
    public ObservableCollection<PerformanceProfile> Profiles => _state.Profiles;
    public ObservableCollection<ProfileOption> ProfileOptions { get; } = new();
    public ICollectionView GamesView { get; }
    public IReadOnlyList<ModeOption<VrrMode>> VrrModes { get; } =
    [
        new(VrrMode.GSync, ProfileLabels.Vrr(VrrMode.GSync)),
        new(VrrMode.FixedRefresh, ProfileLabels.Vrr(VrrMode.FixedRefresh))
    ];
    public IReadOnlyList<ModeOption<VSyncMode>> VSyncModes { get; } =
    [
        new(VSyncMode.ApplicationControlled, ProfileLabels.VSync(VSyncMode.ApplicationControlled)),
        new(VSyncMode.On, ProfileLabels.VSync(VSyncMode.On)),
        new(VSyncMode.Off, ProfileLabels.VSync(VSyncMode.Off))
    ];
    public string Search { get => _search; set { if (Set(ref _search, value)) GamesView.Refresh(); } }
    public string StatusFilter { get => _statusFilter; set { if (Set(ref _statusFilter, value)) GamesView.Refresh(); } }
    public string[] StatusFilters { get; } = ["Все статусы", "Без профиля", "Нужен .exe", "Ожидает", "Применён", "Перезапустите", "Недоступно", "Ошибка"];
    public string Notice { get => _notice; set => Set(ref _notice, value); }
    public GameEntry? SelectedGame { get => _selectedGame; set { if (Set(ref _selectedGame, value)) Changed(nameof(SelectedGameHasBackup)); } }
    public bool SelectedGameHasBackup => !IsApplyingAll && SelectedGame is not null &&
        _state.Backups.Any(b => b.ExePath.Equals(SelectedGame.ExePath, StringComparison.OrdinalIgnoreCase));
    public PerformanceProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value)) return;
            Changed(nameof(SelectedProfileIsEditable));
            Changed(nameof(SelectedProfileHelp));
            Changed(nameof(SelectedProfileRtssLimiterChoice));
            Changed(nameof(SelectedProfileEffectiveRtssModeLabel));
            UpdateFpsFractions();
        }
    }
    public bool SelectedProfileIsEditable => !IsApplyingAll && SelectedProfile is { IsBuiltIn: false };
    public int SelectedProfileRtssLimiterChoice
    {
        get => SelectedProfile?.RtssLimiterMode is { } mode ? (int)mode : -1;
        set
        {
            if (SelectedProfile is not { IsBuiltIn: false } profile || !CanEdit ||
                value != -1 && !Enum.IsDefined((RtssLimiterMode)value)) return;
            profile.RtssLimiterMode = value == -1 ? null : (RtssLimiterMode)value;
        }
    }
    public string SelectedProfileEffectiveRtssModeLabel => SelectedProfile is not { } profile
        ? "Выберите профиль"
        : "Эффективный режим: " + ProfileLabels.Rtss(RtssLimiterModeResolver.Resolve(_state, profile)) +
          (profile.RtssLimiterMode is null ? " · общий" : " · задан в профиле");
    public bool IsApplyingAll
    {
        get => _isApplyingAll;
        private set
        {
            if (!Set(ref _isApplyingAll, value)) return;
            Changed(nameof(CanEdit));
            Changed(nameof(SelectedGameHasBackup));
            Changed(nameof(SelectedProfileIsEditable));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool CanEdit => !IsApplyingAll;
    public string SyncProgressMessage { get => _syncProgressMessage; private set => Set(ref _syncProgressMessage, value); }
    public double SyncProgressPercent { get => _syncProgressPercent; private set => Set(ref _syncProgressPercent, value); }
    public string SelectedProfileHelp => SelectedProfile is { IsBuiltIn: true }
        ? "Стандартный профиль защищён. Создайте его копию, чтобы изменить параметры."
        : "Параметры применятся ко всем играм, которым назначен этот профиль.";
    public DisplayInfo? SelectedDisplay { get => _selectedDisplay; set => Set(ref _selectedDisplay, value); }
    public string DisplayStatus { get => _displayStatus; private set => Set(ref _displayStatus, value); }
    public string DefaultDisplayId
    {
        get => _state.DefaultDisplayId;
        set
        {
            if (_state.DefaultDisplayId == value) return;
            _state.DefaultDisplayId = value ?? "";
            Changed(nameof(DefaultDisplayId));
            foreach (var game in Games.Where(g => string.IsNullOrWhiteSpace(g.DisplayId) &&
                         Profiles.Any(p => p.Id == g.ProfileId && p.FpsMode == FpsLimitMode.DisplayFraction)))
            { game.Status = ApplyStatus.Pending; game.Detail = "Экран по умолчанию изменён; примените профиль"; }
            RefreshDisplays(); UpdateFpsFractions(); UpdateDisplaySummary(); SaveOnly();
        }
    }
    public bool MinimizeToTray
    {
        get => _state.MinimizeToTray;
        set { if (_state.MinimizeToTray != value) { _state.MinimizeToTray = value; Changed(nameof(MinimizeToTray)); SaveOnly(); } }
    }
    public string Theme
    {
        get => _state.Theme;
        set
        {
            string theme = value == "Light" ? "Light" : "Dark";
            if (_state.Theme == theme) return;
            _state.Theme = theme;
            ThemeManager.Apply(theme);
            Changed(nameof(Theme));
            SaveOnly();
        }
    }
    public DisplayInfo? DefaultDisplay => DisplayFps.ChooseDisplay(_state, null, _displays.ToArray());
    public DisplayInfo? OverviewDisplay => _displays.FirstOrDefault(display => display.IsPrimary) ?? _displays.FirstOrDefault();
    public string OverviewDisplayHeading => _displays.Any(display => display.IsPrimary) ? "ОСНОВНОЙ ЭКРАН WINDOWS" : "ЭКРАН WINDOWS";
    public string OverviewDisplayTitle => OverviewDisplay?.Name ?? "Экран недоступен";
    public string OverviewDisplayRate => OverviewDisplay?.RefreshLabel ?? "— Гц";
    public string OverviewDisplayDescription
    {
        get
        {
            if (OverviewDisplay is not { } display) return "Подключите экран или проверьте параметры Windows.";
            string description = $"{display.ResolutionLabel}  ·  {display.Connection}";
            if (!string.IsNullOrWhiteSpace(_state.DefaultDisplayId) && DefaultDisplay is null)
                return description + "  ·  Экран для расчёта FPS отключён";
            return DefaultDisplay is { } fpsDisplay && fpsDisplay.Id != display.Id
                ? description + $"  ·  Доли FPS: {fpsDisplay.Name}"
                : description;
        }
    }
    public string SelectedProfileFpsDetail => SelectedProfile is null ? "Выберите профиль" :
        DisplayFps.Resolve(_state, null, SelectedProfile, _displays.ToArray()).Detail;
    public string RtssDirectory { get => _state.RtssDirectory; set { if (_state.RtssDirectory == value) return; _state.RtssDirectory = value; Changed(nameof(RtssDirectory)); ScheduleSave(); } }
    public RtssLimiterMode RtssLimiterMode
    {
        get => _state.RtssLimiterMode;
        set
        {
            if (_state.RtssLimiterMode == value || !Enum.IsDefined(value)) return;
            _state.RtssLimiterMode = value;
            Changed(nameof(RtssLimiterMode));
            var inherited = Profiles.Where(profile => profile.RtssLimiterMode is null)
                .Select(profile => profile.Id).ToHashSet();
            foreach (var profileId in inherited) _effectiveRtssModes[profileId] = value;
            foreach (var game in Games.Where(game => game.ProfileId is { } id && inherited.Contains(id)))
            { game.Status = ApplyStatus.Pending; game.Detail = "Режим лимитера RTSS изменён; примените профиль"; }
            Changed(nameof(SelectedProfileEffectiveRtssModeLabel));
            RefreshDisplays();
            SaveOnly(false);
        }
    }
    public bool CheckAtStartup { get => _state.CheckAtStartup; set { if (_state.CheckAtStartup == value) return; _state.CheckAtStartup = value; Changed(nameof(CheckAtStartup)); SaveOnly(false); } }
    public bool SilentAdminLaunch
    {
        get => ElevatedLaunchTask.IsInstalled;
        set
        {
            try
            {
                if (value) ElevatedLaunchTask.Install();
                else ElevatedLaunchTask.Uninstall();
                Notice = value ? "Запуск без повторного UAC настроен." : "Обычный запрос прав администратора восстановлен.";
            }
            catch (Exception ex)
            {
                _store.Log(ex);
                Notice = "Не удалось изменить способ запуска: " + ex.Message;
            }
            Changed(nameof(SilentAdminLaunch));
        }
    }
    public bool AutoStart
    {
        get
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("GameProfileManager") is not null; }
            catch (Exception ex) { _store.Log(ex); return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
                if (value) key.SetValue("GameProfileManager", '"' + Environment.ProcessPath! + '"');
                else key.DeleteValue("GameProfileManager", false);
            }
            catch (Exception ex) { _store.Log(ex); Notice = "Не удалось изменить автозапуск: " + ex.Message; }
            Changed(nameof(AutoStart));
        }
    }
    public string NvidiaStatus => _sync.NvidiaStatus ?? "Проверка...";
    public string RtssStatus => _sync.RtssStatus ?? "Проверка...";
    public bool RtssWarningVisible => !_sync.RtssReady;
    public string RtssWarning => _sync.RtssStatus + ". Ограничение FPS сейчас недоступно.";
    public string LastSync => _state.LastSync?.ToLocalTime().ToString("g") ?? "Никогда";
    public int ErrorCount => AttentionItems.Count;
    public int GameCount => Games.Count;
    public int ProfileCount => Profiles.Count;
    public string DataDirectory => _store.DirectoryPath;

    public ICommand AddProfileCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand RemoveProfileCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand RemoveGameCommand { get; }
    public ICommand ChooseExeCommand { get; }
    public Func<string, string, string?, string?>? PickLocalFile { get; set; }
    public ICommand ImportSteamCommand { get; }
    public ICommand ImportEpicCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ApplyGameCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand BrowseRtssCommand { get; }
    public ICommand StartRtssCommand { get; }

    public MainViewModel(SqliteStateStore? store = null, SyncService? sync = null)
    {
        _store = store ?? new SqliteStateStore();
        _saveTimer.Tick += (_, _) => SaveOnly(false);
        _sync = sync ?? new SyncService(_store, displayProvider: () => Volatile.Read(ref _displaySnapshot));
        _syncCoordinator = new SyncOperationCoordinator(_sync);
        try { _state = _store.Load(); }
        catch (Exception ex) { _store.Log(ex); _state = new AppState(); Notice = "Ошибка чтения SQLite: " + ex.Message; }
        _state.Theme = _state.Theme == "Light" ? "Light" : "Dark";
        ThemeManager.Apply(_state.Theme);
        if (!_state.PresetNamesLocalized)
        {
            foreach (var profile in _state.Profiles)
            {
                string? localized = profile.Name switch
                {
                    "Cinematic" => "Кинематографичный",
                    "Balanced" => "Сбалансированный",
                    "Competitive" => "Соревновательный",
                    _ => null
                };
                if (localized is not null) profile.Name = localized;
            }
            _state.PresetNamesLocalized = true;
            try { _store.Save(_state); }
            catch (Exception ex) { _store.Log(ex); Notice = "Ошибка сохранения названий профилей: " + ex.Message; }
        }
        if (ProfilePresets.ProtectLegacy(_state))
        {
            try { _store.Save(_state); }
            catch (Exception ex) { _store.Log(ex); Notice = "Ошибка сохранения стандартных профилей: " + ex.Message; }
        }
        int obsoleteUtilities = 0;
        foreach (var utility in Games.Where(g => g.SteamAppId is not null && SteamDiscovery.NonGameAppIds.Contains(g.SteamAppId) &&
                     g.ProfileId is null && !_state.Backups.Any(b => b.ManagedGameId == g.Id)).ToArray())
        {
            Games.Remove(utility);
            obsoleteUtilities++;
        }
        if (obsoleteUtilities > 0)
        {
            try { _store.Save(_state); Notice = $"Убрано служебных приложений Steam: {obsoleteUtilities}."; }
            catch (Exception ex) { _store.Log(ex); Notice = "Ошибка сохранения: " + ex.Message; }
        }
        GamesView = CollectionViewSource.GetDefaultView(Games);
        GamesView.Filter = item => item is GameEntry g &&
            (string.IsNullOrWhiteSpace(Search) || g.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || g.ExePath.Contains(Search, StringComparison.OrdinalIgnoreCase)) &&
            (StatusFilter == "Все статусы" || g.StatusLabel == StatusFilter);
        ProfileOptions.Add(new ProfileOption(null));
        foreach (var profile in Profiles) ProfileOptions.Add(new ProfileOption(profile));
        AddProfileCommand = new Command(() => { var p = new PerformanceProfile(); Profiles.Add(p); ProfileOptions.Add(new ProfileOption(p)); Hook(p); SelectedProfile = p; SaveOnly(); UpdateSummary(); }, () => !_busy);
        DuplicateProfileCommand = new Command(() =>
        {
            if (SelectedProfile is null) return;
            var copy = ProfilePresets.EditableCopy(SelectedProfile);
            Profiles.Add(copy); ProfileOptions.Add(new ProfileOption(copy)); Hook(copy);
            SelectedProfile = copy; SaveOnly(); UpdateSummary();
        }, () => !_busy);
        RemoveProfileCommand = new Command(() =>
        {
            if (SelectedProfile is null) return;
            if (SelectedProfile.IsBuiltIn) { Notice = "Стандартный профиль нельзя удалить."; return; }
            if (Games.Any(g => g.ProfileId == SelectedProfile.Id)) { Notice = "Сначала переназначьте игры этого профиля."; return; }
            _effectiveRtssModes.Remove(SelectedProfile.Id);
            ProfileOptions.Remove(ProfileOptions.Single(option => option.Id == SelectedProfile.Id));
            Profiles.Remove(SelectedProfile); SelectedProfile = null; SaveOnly(); UpdateSummary();
        }, () => !_busy);
        AddGameCommand = new Command(AddGame, () => !_busy);
        RemoveGameCommand = new Command(() => _ = RemoveSelectedGameAsync(), () => !_busy);
        ChooseExeCommand = new Command(() => _ = ChooseExeAsync(), () => !_busy);
        ImportSteamCommand = new Command(() => _ = ImportSteamAsync(), () => !_busy);
        ImportEpicCommand = new Command(() => _ = ImportEpicAsync(), () => !_busy);
        ApplyCommand = new Command(() => _ = ApplyAllAsync(), () => !_busy);
        ApplyGameCommand = new ParameterCommand(parameter =>
        {
            if (parameter is not GameEntry game) { Notice = "Выберите игру."; return; }
            _ = RunSyncAsync((service, state) => service.ApplyOne(state, state.Games.Single(item => item.Id == game.Id)),
                () => Notice = game.Name + ": " + game.Detail);
        }, () => !_busy);
        VerifyCommand = new Command(() => _ = RunSyncAsync((service, state) => service.CheckSaved(state),
            () => Notice = "Проверка сохранённых параметров завершена."), () => !_busy);
        RestoreCommand = new Command(() =>
        {
            if (SelectedGame is not { } game) { Notice = "Выберите игру."; return; }
            _ = RunSyncAsync((service, state) => service.Restore(state, state.Games.Single(item => item.Id == game.Id)),
                () => Notice = game.Status == ApplyStatus.Error ? game.Detail : "Исходные параметры восстановлены.");
        }, () => !_busy);
        BackupCommand = new Command(() => Run(() => Notice = "Резервная копия: " + _store.ExportBackup(_state)), () => !_busy);
        BrowseRtssCommand = new Command(() =>
        {
            string? path = SelectLocalFile("Выберите RTSSHooks64.dll", "RTSSHooks64.dll", RtssDirectory);
            if (path is not null) { RtssDirectory = Path.GetDirectoryName(path)!; SaveOnly(); _sync.CheckIntegrations(_state); UpdateSummary(); }
        }, () => !_busy);
        StartRtssCommand = new Command(() => Run(() =>
        {
            string directory = string.IsNullOrWhiteSpace(RtssDirectory) ? RtssClient.DetectDirectory() ?? "" : RtssDirectory;
            string exe = Path.Combine(directory, "RTSS.exe");
            if (!File.Exists(exe)) { Notice = "RTSS.exe не найден. Укажите папку RTSS в настройках."; return; }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = directory });
            Notice = "RTSS запускается. Состояние обновится автоматически.";
        }), () => !_busy);
        foreach (var p in Profiles) Hook(p);
        foreach (var g in Games) Hook(g);
        LoadDisplays();
        SelectedProfile = Profiles.FirstOrDefault();
        SelectedGame = Games.FirstOrDefault();
        RefreshDisplays();
        _sync.CheckIntegrations(_state);
        UpdateSummary();
        if (_state.CheckAtStartup)
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => _ = ApplyAllAsync(checkSavedFirst: true)),
                DispatcherPriority.Background);
        _lastRtssProcessState = RtssClient.ProcessIsRunning;
        _rtssTimer.Interval = TimeSpan.FromSeconds(2);
        _rtssTimer.Tick += (_, _) => { PollRtss(); PollDisplays(); };
        _rtssTimer.Start();
    }

    private void PollRtss()
    {
        if (_busy || _rtssPollInFlight || _disposed) return;
        _rtssPollInFlight = true;
        _ = PollRtssAsync();
    }

    private async Task PollRtssAsync()
    {
        try
        {
            bool running = await Task.Run(() => RtssClient.ProcessIsRunning);
            if (_busy || _disposed || running == _lastRtssProcessState) return;
            bool completed = await RunSyncAsync((service, state) => { service.CheckIntegrations(state); service.CheckSaved(state); },
                () => Notice = _sync.RtssReady
                    ? "RTSS снова запущен. Проверьте профили и при необходимости примените их."
                    : "RTSS закрыт. Лимит FPS сейчас не действует.");
            if (completed) _lastRtssProcessState = running;
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Ошибка проверки RTSS: " + ex.Message; }
        finally { _rtssPollInFlight = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _rtssTimer.Stop();
        if (_saveTimer.IsEnabled) SaveOnly(false);
    }

    private void ScheduleSave()
    {
        if (_busy) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }
    private void SaveOnly(bool announce = true, bool duringSync = false)
    {
        if (_busy && !duringSync) return;
        _saveTimer.Stop();
        try { _store.Save(_state); if (announce) Notice = "Конфигурация сохранена."; }
        catch (Exception ex) { _store.Log(ex); Notice = "Ошибка сохранения: " + ex.Message; }
        UpdateSummary();
    }

    /// <summary>
    /// Сохраняет конфигурацию перед внешней операцией и публикует её результат
    /// только после возврата на поток WPF. Блокировка команд действует до записи
    /// окончательного состояния и исключает конкурирующее сохранение SQLite.
    /// </summary>
    private async Task<bool> RunSyncAsync(Action<SyncService, AppState> operation, Action? completed = null)
    {
        if (_busy) return false;
        _busy = true;
        IsApplyingAll = true;
        try
        {
            SaveOnly(false, duringSync: true);
            var snapshot = AppStateSnapshot.Capture(_state);
            var error = await _syncCoordinator.ExecuteAsync(snapshot, operation);
            AppStateSnapshot.PublishResults(snapshot, _state);
            if (error is not null) throw error;
            completed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            Notice = "Операция прервана: " + ex.Message;
            return false;
        }
        finally
        {
            _busy = false;
            IsApplyingAll = false;
            SaveOnly(false);
            RefreshDisplays();
            UpdateSummary();
        }
    }

    private async Task RemoveSelectedGameAsync()
    {
        if (_busy || SelectedGame is not { } game) return;
        if (_state.Backups.Any(backup => backup.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase)))
        {
            bool completed = await RunSyncAsync((service, state) =>
                service.Restore(state, state.Games.Single(item => item.Id == game.Id)));
            if (!completed || _state.Backups.Any(backup => backup.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase)))
            { Notice = game.Detail; return; }
        }
        Games.Remove(game);
        if (SelectedGame == game) SelectedGame = null;
        SaveOnly();
        UpdateSummary();
    }
    private void Run(Action action)
    {
        if (_busy) return;
        _busy = true;
        try { action(); }
        catch (Exception ex) { _store.Log(ex); Notice = ex.Message; }
        finally { _busy = false; UpdateSummary(); }
    }
    private void UpdateSummary()
    {
        RebuildAttentionItems();
        Changed(nameof(GameCount)); Changed(nameof(ProfileCount)); Changed(nameof(ErrorCount));
        Changed(nameof(LastSync)); Changed(nameof(NvidiaStatus)); Changed(nameof(RtssStatus));
        Changed(nameof(RtssWarningVisible)); Changed(nameof(RtssWarning));
        Changed(nameof(SelectedGameHasBackup));
    }


}

public sealed record AttentionItem(string Category, string Title, string Detail, string Advice,
    int PageIndex, string ActionLabel, GameEntry? Game);

public sealed class Command(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
}

public sealed class ParameterCommand(Action<object?> execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
}

public sealed class ModeOption<T>(T value, string label) : Observable
{
    private string _label = label;
    public T Value { get; } = value;
    public string Label { get => _label; set => Set(ref _label, value); }
}
