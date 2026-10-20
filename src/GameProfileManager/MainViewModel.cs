using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using GameProfileManager.Core;
using GameProfileManager.Infrastructure;
using GameProfileManager.Integrations;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GameProfileManager;

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly SqliteStateStore _store;
    private readonly SyncService _sync;
    private readonly AppState _state;
    private readonly Dictionary<Guid, RtssLimiterMode> _effectiveRtssModes = new();
    private readonly ObservableCollection<DisplayInfo> _displays = new();
    private DisplayInfo? _selectedDisplay;
    private string _displayStatus = "Проверка экранов...";
    private string _displayFingerprint = "";
    private string _candidateFingerprint = "";
    private int _candidateObservations;
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
        _sync = sync ?? new SyncService(_store, displayProvider: () => _displays.ToArray());
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
        RemoveGameCommand = new Command(() =>
        {
            if (SelectedGame is null) return;
            if (_state.Backups.Any(b => b.ExePath.Equals(SelectedGame.ExePath, StringComparison.OrdinalIgnoreCase)))
            {
                Run(() => _sync.Restore(_state, SelectedGame));
                if (SelectedGame.Status == ApplyStatus.Error) { Notice = SelectedGame.Detail; return; }
            }
            Games.Remove(SelectedGame); SelectedGame = null; SaveOnly(); UpdateSummary();
        }, () => !_busy);
        ChooseExeCommand = new Command(ChooseExe, () => !_busy);
        ImportSteamCommand = new Command(ImportSteam, () => !_busy);
        ImportEpicCommand = new Command(ImportEpic, () => !_busy);
        ApplyCommand = new Command(() => _ = ApplyAllAsync(), () => !_busy);
        ApplyGameCommand = new ParameterCommand(parameter => Run(() =>
        {
            if (parameter is not GameEntry game) { Notice = "Выберите игру."; return; }
            SaveOnly();
            _sync.ApplyOne(_state, game);
            RefreshDisplays();
            Notice = game.Name + ": " + game.Detail;
        }), () => !_busy);
        VerifyCommand = new Command(() => Run(() => { _sync.CheckSaved(_state); RefreshDisplays(); SaveOnly(); Notice = "Проверка сохранённых параметров завершена."; }), () => !_busy);
        RestoreCommand = new Command(() => Run(() =>
        {
            if (SelectedGame is null) { Notice = "Выберите игру."; return; }
            _sync.Restore(_state, SelectedGame); RefreshDisplays(); Notice = SelectedGame.Status == ApplyStatus.Error ? SelectedGame.Detail : "Исходные параметры восстановлены.";
        }), () => !_busy);
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

    private async Task ApplyAllAsync(bool checkSavedFirst = false)
    {
        if (_busy) return;
        _busy = true;
        IsApplyingAll = true;
        SyncProgressPercent = 0;
        SyncProgressMessage = checkSavedFirst ? "Проверяем сохранённые параметры игр…" : "Подготавливаем применение…";
        Notice = checkSavedFirst ? "Проверяем настройки при запуске." : "Применяем профили. Окно остаётся доступным.";
        try
        {
            SaveOnly(false);
            if (checkSavedFirst)
            {
                await Task.Run(() => _sync.CheckSaved(_state));
                RefreshDisplays();
                bool needsApply = Games.Any(game => game.Status != ApplyStatus.Applied && game.ProfileId is not null &&
                    !string.IsNullOrWhiteSpace(game.ExePath)) ||
                    Games.Any(game => game.ProfileId is null && _state.Backups.Any(backup => backup.ManagedGameId == game.Id));
                if (!needsApply)
                {
                    SaveOnly(false);
                    Notice = "Проверка завершена: сохранённые параметры совпадают.";
                    return;
                }
            }

            var games = Games.ToArray();
            if (games.Length == 0) { Notice = "В библиотеке пока нет игр."; return; }
            for (int index = 0; index < games.Length; index++)
            {
                var game = games[index];
                SyncProgressMessage = $"Игра {index + 1} из {games.Length}: {game.Name}";
                await Task.Run(() => _sync.ApplyOne(_state, game));
                SyncProgressPercent = 100d * (index + 1) / games.Length;
                UpdateSummary();
            }
            int issues = games.Count(game => game.Status is ApplyStatus.Error or ApplyStatus.Unsupported);
            Notice = issues == 0
                ? $"Применение завершено. Обработано игр: {games.Length}. Проверьте статусы."
                : $"Применение завершено. Обработано игр: {games.Length}; с проблемами: {issues}. Проверьте статусы в разделе «Требуют внимания».";
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            Notice = "Применение прервано: " + ex.Message;
        }
        finally
        {
            _busy = false;
            IsApplyingAll = false;
            RefreshDisplays();
            UpdateSummary();
        }
    }

    private void PollRtss()
    {
        if (_busy) return;
        try
        {
            bool running = RtssClient.ProcessIsRunning;
            if (running == _lastRtssProcessState) return;
            _lastRtssProcessState = running;
            _sync.CheckIntegrations(_state);
            _sync.CheckSaved(_state);
            RefreshDisplays();
            Notice = _sync.RtssReady
                ? "RTSS снова запущен. Проверьте профили и при необходимости примените их."
                : "RTSS закрыт. Лимит FPS сейчас не действует.";
            _store.Save(_state);
            UpdateSummary();
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Ошибка проверки RTSS: " + ex.Message; }
    }

    private void LoadDisplays()
    {
        try
        {
            var found = WindowsDisplayDiscovery.Read();
            _displays.Clear();
            foreach (var display in found) _displays.Add(display);
            _displayFingerprint = Fingerprint(found);
            UpdateDisplayOptions();
            DisplayStatus = found.Count == 0 ? "Windows не обнаружила активные экраны" :
                found.Any(d => d.DynamicRefresh) ? "Обнаружена динамическая частота: автоматический лимит для неё недоступен" :
                "Параметры экранов обновляются автоматически";
        }
        catch (Exception ex) { _store.Log(ex); DisplayStatus = "Не удалось прочитать экраны: " + ex.Message; }
        UpdateDisplaySummary();
    }

    private void PollDisplays()
    {
        if (_busy) return;
        try
        {
            var found = WindowsDisplayDiscovery.Read();
            string fingerprint = Fingerprint(found);
            if (fingerprint == _displayFingerprint) { _candidateFingerprint = ""; _candidateObservations = 0; return; }
            if (fingerprint != _candidateFingerprint) { _candidateFingerprint = fingerprint; _candidateObservations = 1; return; }
            if (++_candidateObservations < 2) return;
            _candidateFingerprint = ""; _candidateObservations = 0;
            var previous = _displays.ToArray();
            var changed = Games.Where(game =>
            {
                var profile = Profiles.FirstOrDefault(p => p.Id == game.ProfileId);
                return profile?.FpsMode == FpsLimitMode.DisplayFraction &&
                    DisplayFps.Resolve(_state, game, profile, previous).Fps !=
                    DisplayFps.Resolve(_state, game, profile, found).Fps;
            }).ToArray();
            _displays.Clear();
            foreach (var display in found) _displays.Add(display);
            _displayFingerprint = fingerprint;
            UpdateDisplayOptions(); UpdateDisplaySummary(); UpdateFpsFractions(); RefreshDisplays();
            DisplayStatus = found.Count == 0 ? "Windows не обнаружила активные экраны" :
                found.Any(d => d.DynamicRefresh) ? "Динамическая частота: автоматический лимит недоступен" :
                "Параметры экранов обновляются автоматически";
            if (changed.Length == 0) return;
            Run(() =>
            {
                foreach (var game in changed)
                {
                    _sync.ApplyOne(_state, game);
                }
                RefreshDisplays();
                Notice = changed.All(game => game.Status is ApplyStatus.Applied or ApplyStatus.RestartRequired)
                    ? "Частота экрана изменилась; новые лимиты FPS применены."
                    : "Частота экрана изменилась; проверьте статусы игр.";
            });
        }
        catch (Exception ex) { _store.Log(ex); DisplayStatus = "Ошибка проверки экрана: " + ex.Message; }
    }

    private static string Fingerprint(IEnumerable<DisplayInfo> displays) => string.Join("|",
        displays.OrderBy(display => display.Id, StringComparer.OrdinalIgnoreCase).Select(display =>
            $"{display.Id}:{display.Width}:{display.Height}:{display.RefreshNumerator}/{display.RefreshDenominator}:{display.IsPrimary}:{display.DynamicRefresh}"));

    private void UpdateDisplayOptions()
    {
        string? selected = SelectedDisplay?.Id;
        IsUpdatingDisplayOptions = true;
        try
        {
            DefaultDisplayOptions.Clear(); GameDisplayOptions.Clear();
            DefaultDisplayOptions.Add(new("", "Основной экран Windows"));
            GameDisplayOptions.Add(new("", "Экран по умолчанию"));
            foreach (var display in _displays)
            {
                string label = $"{display.Name} · {display.RefreshLabel}" + (display.IsPrimary ? " · основной" : "");
                DefaultDisplayOptions.Add(new(display.Id, label));
                GameDisplayOptions.Add(new(display.Id, label));
            }
            var available = _displays.Select(display => display.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(_state.DefaultDisplayId) && !available.Contains(_state.DefaultDisplayId))
                DefaultDisplayOptions.Add(new(_state.DefaultDisplayId, "Выбранный экран отключён"));
            foreach (string id in Games.Select(game => game.DisplayId).Where(id => !string.IsNullOrWhiteSpace(id) && !available.Contains(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                GameDisplayOptions.Add(new(id, "Выбранный экран отключён"));
        }
        finally { IsUpdatingDisplayOptions = false; }
        SelectedDisplay = _displays.FirstOrDefault(display => display.Id == selected) ?? OverviewDisplay;
        Changed(nameof(DefaultDisplayId));
    }

    private void UpdateDisplaySummary()
    {
        Changed(nameof(DefaultDisplay)); Changed(nameof(OverviewDisplay)); Changed(nameof(OverviewDisplayHeading));
        Changed(nameof(OverviewDisplayTitle)); Changed(nameof(OverviewDisplayRate)); Changed(nameof(OverviewDisplayDescription));
        Changed(nameof(SelectedProfileFpsDetail));
    }

    private void UpdateFpsFractions()
    {
        IsUpdatingFpsOptions = true;
        try
        {
            var available = new List<ModeOption<int>>();
            var display = DefaultDisplay;
            if (display is { DynamicRefresh: false })
            {
                for (int divisor = 1; divisor <= 12; divisor++)
                {
                    if (DisplayFps.ExactLimit(display, divisor) is int fps)
                        available.Add(new(divisor, $"1/{divisor} — {fps} FPS"));
                }
            }
            var values = available.Select(option => option.Value).ToHashSet();
            for (int i = FpsFractions.Count - 1; i >= 0; i--)
                if (!values.Contains(FpsFractions[i].Value)) FpsFractions.RemoveAt(i);
            for (int i = 0; i < available.Count; i++)
            {
                var next = available[i];
                if (i < FpsFractions.Count && FpsFractions[i].Value == next.Value)
                    FpsFractions[i].Label = next.Label;
                else
                    FpsFractions.Insert(i, next);
            }
        }
        finally { IsUpdatingFpsOptions = false; }
        Changed(nameof(SelectedProfileFpsDetail));
    }

    public void Dispose()
    {
        _rtssTimer.Stop();
        if (_saveTimer.IsEnabled) SaveOnly(false);
    }

    private void AddGame()
    {
        string? path = SelectLocalFile("Выберите игровой .exe", "*.exe", null);
        if (path is null) return;
        if (Games.Any(g => g.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase))) { Notice = "Этот .exe уже добавлен."; return; }
        var game = new GameEntry { Name = Path.GetFileNameWithoutExtension(path), ExePath = path, Detail = "Выберите профиль" };
        Games.Add(game); Hook(game); SelectedGame = game; RefreshDisplays(); SaveOnly(); UpdateSummary();
    }
    private void ChooseExe()
    {
        if (SelectedGame is null) { Notice = "Выберите игру."; return; }
        string? path = SelectLocalFile("Выберите игровой процесс", "*.exe", SelectedGame.InstallDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (path is null) return;
        if (!SelectedGame.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase) &&
            _state.Backups.Any(b => b.ExePath.Equals(SelectedGame.ExePath, StringComparison.OrdinalIgnoreCase)))
        {
            Run(() => _sync.Restore(_state, SelectedGame));
            if (SelectedGame.Status == ApplyStatus.Error) { Notice = SelectedGame.Detail; return; }
        }
        SelectedGame.ExePath = path;
        SelectedGame.Status = ApplyStatus.Pending;
        SelectedGame.Detail = "Нужно применить профиль";
        SaveOnly();
    }

    private string? SelectLocalFile(string title, string pattern, string? initialDirectory)
    {
        if (PickLocalFile is not null) return PickLocalFile(title, pattern, initialDirectory);
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = pattern == "*.exe" ? "Приложения (*.exe)|*.exe" : "RTSSHooks64.dll|RTSSHooks64.dll",
            InitialDirectory = initialDirectory ?? ""
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    private void ImportSteam()
    {
        Run(() =>
        {
            var found = SteamDiscovery.FindInstalled();
            int added = 0;
            int removed = 0;
            foreach (var utility in Games.Where(g => g.SteamAppId is not null && SteamDiscovery.NonGameAppIds.Contains(g.SteamAppId) &&
                         g.ProfileId is null && !_state.Backups.Any(b => b.ManagedGameId == g.Id)).ToArray())
            {
                Games.Remove(utility);
                if (SelectedGame == utility) SelectedGame = null;
                removed++;
            }
            foreach (var game in found)
            {
                var existing = Games.FirstOrDefault(g => g.SteamAppId == game.SteamAppId);
                var manuallyAdded = Games.FirstOrDefault(g => g.SteamAppId is null && !string.IsNullOrWhiteSpace(game.ExePath) &&
                    g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));
                if (manuallyAdded is not null && existing is not null && existing.ProfileId is null &&
                    !_state.Backups.Any(b => b.ManagedGameId == existing.Id))
                {
                    if (SelectedGame == existing) SelectedGame = manuallyAdded;
                    Games.Remove(existing);
                    existing = null;
                    removed++;
                }
                if (manuallyAdded is not null && existing is null)
                {
                    manuallyAdded.SteamAppId = game.SteamAppId;
                    manuallyAdded.InstallDirectory = game.InstallDirectory;
                    if (manuallyAdded.Name.Equals(Path.GetFileNameWithoutExtension(manuallyAdded.ExePath), StringComparison.OrdinalIgnoreCase))
                        manuallyAdded.Name = game.Name;
                    manuallyAdded.Icon = null;
                    continue;
                }
                if (existing is not null)
                {
                    if (string.IsNullOrWhiteSpace(existing.ExePath) && !string.IsNullOrWhiteSpace(game.ExePath)) existing.ExePath = game.ExePath;
                    continue;
                }
                Games.Add(game); Hook(game); added++;
            }
            Search = "";
            StatusFilter = "Все статусы";
            SelectedGame ??= Games.FirstOrDefault();
            RefreshDisplays(); SaveOnly(); UpdateSummary();
            Notice = found.Count == 0
                ? "Steam: установленные игры не найдены. Проверьте путь к библиотеке Steam."
                : $"Steam: найдено {found.Count}, добавлено {added}, убрано дублей и утилит {removed}. Для неоднозначных игр выберите .exe вручную.";
        });
    }
    private void ImportEpic()
    {
        Run(() =>
        {
            var found = EpicDiscovery.FindInstalled();
            int added = 0;
            int existingCount = 0;
            foreach (var game in found)
            {
                var existing = Games.FirstOrDefault(g => g.EpicAppName is not null &&
                    g.EpicAppName.Equals(game.EpicAppName, StringComparison.OrdinalIgnoreCase));
                existing ??= Games.FirstOrDefault(g => !string.IsNullOrWhiteSpace(game.ExePath) &&
                    g.ExePath.Equals(game.ExePath, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.EpicAppName = game.EpicAppName;
                    existing.InstallDirectory ??= game.InstallDirectory;
                    if (string.IsNullOrWhiteSpace(existing.ExePath) && !string.IsNullOrWhiteSpace(game.ExePath))
                        existing.ExePath = game.ExePath;
                    if (existing.Name.Equals(Path.GetFileNameWithoutExtension(existing.ExePath), StringComparison.OrdinalIgnoreCase))
                        existing.Name = game.Name;
                    existingCount++;
                    continue;
                }
                Games.Add(game); Hook(game); added++;
            }
            Search = "";
            StatusFilter = "Все статусы";
            SelectedGame ??= Games.FirstOrDefault();
            RefreshDisplays(); SaveOnly(); UpdateSummary();
            Notice = found.Count == 0
                ? "EGS: установленные игры не найдены. Проверьте установку Epic Games Launcher."
                : $"EGS: найдено {found.Count}, добавлено {added}, уже в библиотеке {existingCount}. Для неоднозначных игр выберите .exe вручную.";
        });
    }
    private void Hook(PerformanceProfile profile)
    {
        _effectiveRtssModes[profile.Id] = RtssLimiterModeResolver.Resolve(_state, profile);
        profile.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PerformanceProfile.RtssLimiterMode))
            {
                var effective = RtssLimiterModeResolver.Resolve(_state, profile);
                bool changed = !_effectiveRtssModes.TryGetValue(profile.Id, out var previous) || previous != effective;
                _effectiveRtssModes[profile.Id] = effective;
                if (profile == SelectedProfile)
                {
                    Changed(nameof(SelectedProfileRtssLimiterChoice));
                    Changed(nameof(SelectedProfileEffectiveRtssModeLabel));
                }
                if (changed)
                    foreach (var game in Games.Where(g => g.ProfileId == profile.Id))
                    { game.Status = ApplyStatus.Pending; game.Detail = "Режим лимитера RTSS изменён; примените профиль"; }
                RefreshDisplays();
                ScheduleSave();
                return;
            }
            if (e.PropertyName is nameof(PerformanceProfile.Name) or nameof(PerformanceProfile.FpsLimit) or
                nameof(PerformanceProfile.FpsMode) or nameof(PerformanceProfile.FpsDivisor) or
                nameof(PerformanceProfile.Vrr) or nameof(PerformanceProfile.VSync) or
                nameof(PerformanceProfile.Description))
            {
                if (e.PropertyName == nameof(PerformanceProfile.Name)) ProfileOptions.First(option => option.Id == profile.Id).RefreshName();
                if (e.PropertyName != nameof(PerformanceProfile.Description))
                {
                    if (profile == SelectedProfile) UpdateFpsFractions();
                    foreach (var game in Games.Where(g => g.ProfileId == profile.Id)) { game.Status = ApplyStatus.Pending; game.Detail = "Профиль изменён"; }
                    RefreshDisplays();
                }
                ScheduleSave();
            }
        };
    }
    private void Hook(GameEntry game) => game.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(GameEntry.Name)) { UpdateSummary(); ScheduleSave(); }
        if (e.PropertyName is nameof(GameEntry.ProfileId) or nameof(GameEntry.ExePath) or nameof(GameEntry.DisplayId))
        {
            if (e.PropertyName == nameof(GameEntry.ExePath)) game.Icon = null;
            game.Status = ApplyStatus.Pending;
            game.Detail = game.ProfileId is null
                ? _state.Backups.Any(b => b.ManagedGameId == game.Id)
                    ? "Профиль отключён. Нажмите «Применить», чтобы восстановить исходные параметры."
                    : "Профиль не выбран: параметры не применяются"
                : "Назначение изменено";
            RefreshDisplays();
            if (e.PropertyName is nameof(GameEntry.ProfileId) or nameof(GameEntry.DisplayId)) SaveOnly(false);
            else ScheduleSave();
        }
    };
    private void RefreshDisplays()
    {
        var displays = _displays.ToArray();
        foreach (var profile in Profiles)
        {
            var fps = DisplayFps.Resolve(_state, null, profile, displays);
            profile.FpsPreview = profile.FpsMode == FpsLimitMode.Manual ? $"{profile.FpsLimit} FPS" :
                fps.Fps is int value ? $"1/{profile.FpsDivisor} · {value} FPS" : $"1/{profile.FpsDivisor} · недоступно";
            profile.Changed(nameof(PerformanceProfile.FpsPreview));
        }
        foreach (var game in Games)
        {
            var profile = Profiles.FirstOrDefault(p => p.Id == game.ProfileId);
            game.ProfileDisplay = profile?.Name ?? "Без профиля";
            game.FpsDisplay = profile is null ? "—" : DisplayFps.Resolve(_state, game, profile, displays).DisplayText;
            game.VrrDisplay = profile is null ? "—" : ProfileLabels.Vrr(profile.Vrr);
            game.VSyncDisplay = profile is null ? "—" : ProfileLabels.VSync(profile.VSync);
            game.RtssLimiterDisplay = profile is null ? "—" :
                ProfileLabels.Rtss(RtssLimiterModeResolver.Resolve(_state, profile)) +
                (profile.RtssLimiterMode is null ? " · общий режим" : " · режим профиля");
            if (game.Icon is null) game.Icon = FileIcon.Load(game);
            game.Changed(nameof(GameEntry.ProfileDisplay)); game.Changed(nameof(GameEntry.FpsDisplay)); game.Changed(nameof(GameEntry.VrrDisplay)); game.Changed(nameof(GameEntry.VSyncDisplay));
            game.Changed(nameof(GameEntry.RtssLimiterDisplay));
            game.Changed(nameof(GameEntry.Icon));
        }
        if (StatusFilter != "Все статусы") GamesView.Refresh();
        Changed(nameof(SelectedProfileFpsDetail));
        UpdateSummary();
    }
    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }
    private void SaveOnly(bool announce = true)
    {
        _saveTimer.Stop();
        try { _store.Save(_state); if (announce) Notice = "Конфигурация сохранена."; }
        catch (Exception ex) { _store.Log(ex); Notice = "Ошибка сохранения: " + ex.Message; }
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

    private void RebuildAttentionItems()
    {
        AttentionItems.Clear();
        if (_sync.RtssStatus is not null && !_sync.RtssReady)
            AttentionItems.Add(new("ИНТЕГРАЦИЯ", "RTSS недоступен", _sync.RtssStatus,
                "Запустите RTSS. Если он не найден, укажите папку установки в настройках, затем проверьте игры.",
                5, "Открыть настройки", null));
        if (_sync.NvidiaStatus?.StartsWith("NVIDIA недоступна", StringComparison.OrdinalIgnoreCase) == true)
            AttentionItems.Add(new("ИНТЕГРАЦИЯ", "NVIDIA недоступна", _sync.NvidiaStatus,
                "Проверьте драйвер NVIDIA и повторите проверку. Пока DRS недоступен, профили не применяются.",
                5, "Открыть настройки", null));
        foreach (var game in Games)
        {
            bool hasBackup = _state.Backups.Any(backup => backup.ManagedGameId == game.Id);
            if (game.ProfileId is null)
            {
                if (hasBackup)
                    AttentionItems.Add(new("ВОССТАНОВЛЕНИЕ", game.Name, game.Detail,
                        "Профиль снят. Нажмите «Применить» у игры, чтобы восстановить исходные значения RTSS и NVIDIA.",
                        2, "Открыть игру", game));
                continue;
            }
            if (string.IsNullOrWhiteSpace(game.ExePath))
            {
                AttentionItems.Add(new("НУЖЕН .EXE", game.Name, game.Detail,
                    "Выберите игровой исполняемый файл. Лаунчер и служебный процесс для профиля не подходят.",
                    2, "Выбрать .exe", game));
                continue;
            }
            if (game.Status == ApplyStatus.Applied) continue;
            string advice = game.Status switch
            {
                ApplyStatus.RestartRequired => "Настройки записаны. Закройте и снова запустите игру, затем проверьте статус.",
                ApplyStatus.Pending => "Настройки отличаются или ещё не применялись. Нажмите «Применить» в строке игры.",
                ApplyStatus.Unsupported when game.Detail.Contains("экран", StringComparison.OrdinalIgnoreCase) ||
                    game.Detail.Contains("частот", StringComparison.OrdinalIgnoreCase) ||
                    game.Detail.Contains("делител", StringComparison.OrdinalIgnoreCase) ||
                    game.Detail.Contains("разделить", StringComparison.OrdinalIgnoreCase) =>
                    "Проверьте экран игры и выберите точную долю герцовки либо ручной FPS в профиле.",
                ApplyStatus.Unsupported when game.Detail.Contains("RTSS", StringComparison.OrdinalIgnoreCase) =>
                    "Запустите RTSS и проверьте доступ к его папке, затем повторите применение.",
                ApplyStatus.Error when !File.Exists(game.ExePath) =>
                    "Файл игры не найден. Выберите актуальный игровой .exe в разделе «Мои игры».",
                _ => "Откройте игру, прочитайте причину и повторите проверку или применение после исправления."
            };
            AttentionItems.Add(new(game.StatusLabel.ToUpperInvariant(), game.Name, game.Detail,
                advice, 2, "Открыть игру", game));
        }
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
