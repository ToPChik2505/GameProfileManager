using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using GameProfileManager.Core;
using GameProfileManager.Application;
using GameProfileManager.Infrastructure;
using GameProfileManager.Integrations;
using GameProfileManager.Presentation;
using Microsoft.Win32;

namespace GameProfileManager;

public sealed partial class MainViewModel : Observable, IDisposable
{
    private readonly IStateStore _store;
    private readonly SyncService _sync;
    private readonly SyncOperationCoordinator _syncCoordinator;
    private readonly IGameConfigSnapshotStore _gameConfigStore;
    private readonly GameConfigSnapshotService _gameConfigSnapshotService;
    private readonly GameConfigArchiveService _gameConfigArchiveService;
    private readonly GameConfigTransactionManager _gameConfigTransactions;
    private readonly GameProcessGuard _gameProcessGuard;
    private readonly IPcgamingWikiCredentialStore _pcgamingWikiCredentialStore;
    private readonly IGameConfigDiscovery _gameConfigDiscovery;
    private readonly SemaphoreSlim _gameConfigReconcileGate = new(1, 1);
    private int _gameConfigReconcileRequested;
    private long _gameConfigStateVersion;
    private DiagnosticsProbeSnapshot? _latestDiagnosticsProbe;
    private string _diagnosticsStatus = "Диагностика ещё не запускалась";
    private readonly GameIconLoader _iconLoader;
    private readonly AppState _state;
    private DisplayInfo[] _displaySnapshot = [];
    private readonly DisplayChangeMonitor _displayMonitor = new();
    private bool _displayPollInFlight;
    private bool _rtssPollInFlight;
    private bool _disposed;
    private readonly Dictionary<Guid, RtssLimiterMode> _effectiveRtssModes = new();
    private readonly Dictionary<PerformanceProfile, PropertyChangedEventHandler> _profileHandlers = new();
    private readonly Dictionary<GameEntry, PropertyChangedEventHandler> _gameHandlers = new();
    private readonly Dictionary<GameEntry, GameEntryViewModel> _gameCards = new();
    private readonly ObservableCollection<DisplayInfo> _displays = new();
    private DisplayInfo? _selectedDisplay;
    private string _displayStatus = "Проверка экранов...";
    private string _search = "";
    private string _statusFilter = "Все статусы";
    private string _notice = "Готово";
    private GameEntry? _selectedGame;
    private GameEntryViewModel? _selectedGameCard;
    private PerformanceProfile? _selectedProfile;
    private bool _busy;
    private bool _updatingGameAssignment;
    private bool _isApplyingAll;
    private bool _isActivityVisible;
    private string _syncProgressMessage = "";
    private double _syncProgressPercent;
    private int _syncProgressTotal;
    private readonly DispatcherTimer _rtssTimer = new();
    private readonly DispatcherTimer _activityDelayTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private bool _lastRtssProcessState;

    public ObservableCollection<GameEntry> Games => _state.Games;
    public ObservableCollection<GameEntryViewModel> GameCards { get; } = new();
    public ObservableCollection<DisplayInfo> Displays => _displays;
    public ObservableCollection<ModeOption<string>> DefaultDisplayOptions { get; } = new();
    public ObservableCollection<ModeOption<string>> GameDisplayOptions { get; } = new();
    public ObservableCollection<ModeOption<string>> ProfileAssignmentDisplayOptions { get; } = new();
    public ObservableCollection<ModeOption<int>> FpsFractions { get; } = new();
    public IReadOnlyList<ModeOption<string>> ThemeOptions { get; } =
    [
        new("System", "Системная"),
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
    public IReadOnlyList<ModeOption<DisplayProfilePolicy>> DisplayProfilePolicies { get; } =
    [
        new(DisplayProfilePolicy.WindowsPrimary, "Основной экран Windows"),
        new(DisplayProfilePolicy.PreferredDisplay, "Выбранный экран, иначе общий"),
        new(DisplayProfilePolicy.PinnedDisplay, "Только выбранный экран")
    ];
    public ObservableCollection<AttentionItem> AttentionItems { get; } = new();
    public bool IsUpdatingDisplayOptions { get; private set; }
    public bool IsUpdatingFpsOptions { get; private set; }
    public IReadOnlyList<ModeOption<FpsLimitMode>> FpsModes { get; } =
    [
        new(FpsLimitMode.Manual, "Ввести FPS вручную"),
        new(FpsLimitMode.DisplayFraction, "Доля частоты экрана")
    ];
    public void SetDisplayProfileAssignment(GameEntry game, string displayId, Guid? profileId)
    {
        if (_busy || string.IsNullOrWhiteSpace(displayId)) return;
        var existing = game.DisplayProfileAssignments.FirstOrDefault(item =>
            item.DisplayId.Equals(displayId, StringComparison.OrdinalIgnoreCase));
        if (existing?.ProfileId == profileId) return;
        if (profileId is null)
        {
            if (existing is not null) game.DisplayProfileAssignments.Remove(existing);
        }
        else if (existing is null)
            game.DisplayProfileAssignments.Add(new DisplayProfileAssignment { DisplayId = displayId, ProfileId = profileId.Value });
        else existing.ProfileId = profileId.Value;
        if (game.AutoSwitchProfile)
        {
            game.Status = ApplyStatus.Pending;
            game.Detail = "Назначение профиля для дисплея изменено";
        }
        _latestDiagnosticsProbe = null;
        if (_gameCards.TryGetValue(game, out var card))
        {
            card.Changed(nameof(GameEntryViewModel.DisplayAssignmentProfileSelectionId));
            card.UpdateDisplayAssignmentRows(_state, _displays.ToArray());
            card.RefreshDisplayAssignment(displayId);
        }
        RefreshDisplays(); SaveOnly(false);
    }
    public ObservableCollection<PerformanceProfile> Profiles => _state.Profiles;
    public bool SaveProfileDraft(Guid? profileId, PerformanceProfile draft, out string error)
    {
        error = "";
        if (_busy) { error = "Дождитесь завершения текущей операции."; return false; }
        if (string.IsNullOrWhiteSpace(draft.Name)) { error = "Введите название профиля."; return false; }
        if (draft.FpsMode == FpsLimitMode.Manual && (draft.FpsLimit < 1 || draft.FpsLimit > 1000))
        { error = "Введите FPS от 1 до 1000."; return false; }
        if (draft.FpsMode == FpsLimitMode.DisplayFraction && (draft.FpsDivisor < 1 || draft.FpsDivisor > 12))
        { error = "Выберите долю частоты от 1/1 до 1/12."; return false; }
        PerformanceProfile target;
        if (profileId is null)
        {
            target = new PerformanceProfile();
            Profiles.Add(target);
            ProfileOptions.Add(new ProfileOption(target));
            Hook(target);
        }
        else
        {
            target = Profiles.FirstOrDefault(item => item.Id == profileId.Value)!;
            if (target is null) { error = "Профиль не найден."; return false; }
        }
        target.Name = draft.Name.Trim();
        target.Description = draft.Description;
        target.FpsMode = draft.FpsMode;
        target.FpsLimit = draft.FpsLimit;
        target.FpsDivisor = draft.FpsDivisor;
        target.Vrr = draft.Vrr;
        target.VSync = draft.VSync;
        target.RtssLimiterMode = draft.RtssLimiterMode;
        SelectedProfile = target;
        SaveOnly(false);
        RefreshDisplays();
        UpdateSummary();
        Notice = "Профиль сохранён. Для записи в RTSS/NVIDIA примените его к игре.";
        return true;
    }
    public ObservableCollection<ProfileOption> ProfileOptions { get; } = new();
    public ObservableCollection<GameEntryViewModel> GamesView { get; } = new();
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
    public string Search { get => _search; set { if (Set(ref _search, value)) RefreshGamesView(); } }
    public string StatusFilter { get => _statusFilter; set { if (Set(ref _statusFilter, value)) RefreshGamesView(); } }
    public string[] StatusFilters { get; } = ["Все статусы", "Без профиля", "Нужен .exe", "Ожидает", "Применён", "Перезапустите", "Недоступно", "Ошибка"];
    public string Notice
    {
        get => _notice;
        set
        {
            // Notices are UI events: re-emit identical messages so a dismissed InfoBar can be reopened.
            if (!Set(ref _notice, value)) Changed(nameof(Notice));
        }
    }
    public GameEntry? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!Set(ref _selectedGame, value)) return;
            _selectedGameCard = value is not null && _gameCards.TryGetValue(value, out var card) ? card : null;
            Changed(nameof(SelectedGameCard));
            Changed(nameof(SelectedGameHasBackup));
        }
    }
    public GameEntryViewModel? SelectedGameCard
    {
        get => _selectedGameCard;
        set => SelectedGame = value?.Model;
    }
    public bool SelectedGameHasBackup => !IsApplyingAll && SelectedGame is not null &&
        SettingBackupOwnership.HasForGame(_state, SelectedGame);
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
    public bool SelectedProfileIsEditable => !IsApplyingAll && SelectedProfile is not null;
    public int SelectedProfileRtssLimiterChoice
    {
        get => SelectedProfile?.RtssLimiterMode is { } mode ? (int)mode : -1;
        set
        {
            if (SelectedProfile is not { } profile || !CanEdit ||
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
            if (value)
            {
                IsActivityVisible = false;
                _activityDelayTimer.Stop();
                _activityDelayTimer.Start();
            }
            else
            {
                _activityDelayTimer.Stop();
                IsActivityVisible = false;
            }
            Changed(nameof(CanEdit));
            Changed(nameof(SelectedGameHasBackup));
            Changed(nameof(SelectedProfileIsEditable));
            RaiseCommandState();
        }
    }
    public bool CanEdit => !IsApplyingAll;
    public bool IsActivityVisible { get => _isActivityVisible; private set => Set(ref _isActivityVisible, value); }
    public string SyncProgressMessage { get => _syncProgressMessage; private set => Set(ref _syncProgressMessage, value); }
    public double SyncProgressPercent { get => _syncProgressPercent; private set => Set(ref _syncProgressPercent, value); }
    public int SyncProgressTotal { get => _syncProgressTotal; private set => Set(ref _syncProgressTotal, value); }
    public string SelectedProfileHelp => SelectedProfile is { IsBuiltIn: true }
        ? "Начальный профиль можно изменить, но нельзя удалить. Его параметры применятся ко всем назначенным играм после отдельного применения."
        : "Параметры применятся ко всем играм, которым назначен этот профиль.";
    public DisplayInfo? SelectedDisplay { get => _selectedDisplay; set => Set(ref _selectedDisplay, value); }
    public string DisplayStatus { get => _displayStatus; private set => Set(ref _displayStatus, value); }
    public string DefaultDisplayId
    {
        get => _state.DefaultDisplayId;
        set
        {
            if (_state.DefaultDisplayId == value) return;
            var displays = _displays.ToArray();
            var oldConfigurations = Games.Where(game => game.AutoSwitchProfile &&
                    game.DisplayProfilePolicy == DisplayProfilePolicy.PreferredDisplay && string.IsNullOrWhiteSpace(game.DisplayId))
                .ToDictionary(game => game.Id, game => ProfileResolver.ResolveConfiguration(_state, game, displays));
            _state.DefaultDisplayId = value ?? "";
            Changed(nameof(DefaultDisplayId));
            foreach (var game in Games.Where(g => string.IsNullOrWhiteSpace(g.DisplayId) &&
                         (!g.AutoSwitchProfile || g.DisplayProfilePolicy == DisplayProfilePolicy.PreferredDisplay) &&
                         ActiveProfileIds(g).Any(id => Profiles.Any(p => p.Id == id && p.FpsMode == FpsLimitMode.DisplayFraction))))
            { game.Status = ApplyStatus.Pending; game.Detail = "Экран по умолчанию изменён; примените профиль"; }
            var autoChanged = Games.Where(game => oldConfigurations.TryGetValue(game.Id, out var previous) &&
                previous != ProfileResolver.ResolveConfiguration(_state, game, displays)).ToArray();
            RefreshDisplays(); UpdateFpsFractions(); UpdateDisplaySummary(); SaveOnly();
            if (autoChanged.Length > 0) _ = ApplyDisplayChangesAsync(autoChanged);
            _ = ReconcileGameConfigsAsync();
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
            string theme = value is "Light" or "Dark" ? value : "System";
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
            foreach (var game in Games.Where(game => ActiveProfileIds(game).Any(inherited.Contains)))
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
                if (value) key.SetValue("GameProfileManager", AutoStartCommand);
                else key.DeleteValue("GameProfileManager", false);
            }
            catch (Exception ex) { _store.Log(ex); Notice = "Не удалось изменить автозапуск: " + ex.Message; }
            Changed(nameof(AutoStart));
        }
    }
    private static string AutoStartCommand => '"' + Environment.ProcessPath! + '"' + " --autostart";

    public static void UpgradeAutoStartEntry()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (string.Equals(key?.GetValue("GameProfileManager") as string,
                    '"' + exe + '"', StringComparison.OrdinalIgnoreCase))
                key!.SetValue("GameProfileManager", AutoStartCommand);
        }
        catch (Exception) { }
    }
    public string NvidiaStatus => _sync.NvidiaStatus ?? "Проверка...";
    public string RtssStatus => _sync.RtssStatus ?? "Проверка...";
    public bool RtssWarningVisible => _sync.RtssStatus is not null && !_sync.RtssReady;
    public string RtssWarning
    {
        get
        {
            string status = _sync.RtssStatus ?? "";
            if (status.StartsWith("RTSS закрыт: ", StringComparison.Ordinal))
                status = "Папка: " + status["RTSS закрыт: ".Length..];
            else if (status.StartsWith("RTSS недоступен: ", StringComparison.Ordinal))
                status = status["RTSS недоступен: ".Length..];
            return status + ". Ограничение FPS сейчас недоступно.";
        }
    }
    public string LastSync => _state.LastSync?.ToLocalTime().ToString("g") ?? "Никогда";
    public int ErrorCount => AttentionItems.Count;
    public int GameCount => Games.Count;
    public int ProfileCount => Profiles.Count;
    public string DataDirectory => _store.DirectoryPath;
    public string DiagnosticsStatus { get => _diagnosticsStatus; private set => Set(ref _diagnosticsStatus, value); }

    public ICommand AddProfileCommand { get; }
    public ICommand DuplicateProfileCommand { get; }
    public ICommand RemoveProfileCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand ChooseExeCommand { get; }
    public Func<string, string, string?, Task<string?>>? PickLocalFileAsync { get; set; }
    public Func<string, string, string?, Task<IReadOnlyList<string>>>? PickLocalFilesAsync { get; set; }
    public ICommand ImportSteamCommand { get; }
    public ICommand ImportEpicCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ApplyGameCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand BrowseRtssCommand { get; }
    public ICommand StartRtssCommand { get; }
    public ICommand DiagnosticsCommand { get; }

    public MainViewModel(IStateStore? store = null, SyncService? sync = null)
    {
        _store = store ?? new SqliteStateStore();
        _pcgamingWikiCredentialStore = new PcgamingWikiCredentialStore();
        _gameConfigDiscovery = new GameConfigDiscovery(_pcgamingWikiCredentialStore);
        _saveTimer.Tick += (_, _) => SaveOnly(false);
        _sync = sync ?? SyncServiceFactory.Create(_store, () => Volatile.Read(ref _displaySnapshot));
        _syncCoordinator = new SyncOperationCoordinator(_sync);
        // Ошибка чтения должна прервать запуск: пустое состояние нельзя сохранять поверх существующей базы.
        _state = LoadStateOrThrow(_store);
        _gameConfigStore = new GameConfigSnapshotStore(_store.DirectoryPath);
        _gameProcessGuard = new GameProcessGuard();
        _gameConfigSnapshotService = new GameConfigSnapshotService(_gameConfigStore, _gameProcessGuard);
        _gameConfigArchiveService = new GameConfigArchiveService(_gameConfigStore);
        _gameConfigTransactions = new GameConfigTransactionManager(_gameConfigStore, _gameProcessGuard);
        int unfinishedGameConfigTransactions = _gameConfigTransactions.IncompleteTransactions().Count;
        bool recoveredGameConfigState = _gameConfigTransactions.RecoverIncomplete(_state);
        if (unfinishedGameConfigTransactions > 0 || recoveredGameConfigState)
        {
            try { _store.Save(_state); }
            catch (Exception ex) { _store.Log(ex); }
        }
        _iconLoader = new GameIconLoader(DispatcherQueue.GetForCurrentThread(), _store.Log);
        _state.Theme = _state.Theme is "Light" or "Dark" ? _state.Theme : "System";
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
                     g.ProfileId is null && !g.AutoSwitchProfile && g.DisplayProfileAssignments.Count == 0 &&
                     !SettingBackupOwnership.HasForGame(_state, g) && !HasGameConfigData(g.Id)).ToArray())
        {
            Games.Remove(utility);
            obsoleteUtilities++;
        }
        if (obsoleteUtilities > 0)
        {
            try { _store.Save(_state); Notice = $"Убрано служебных приложений Steam: {obsoleteUtilities}."; }
            catch (Exception ex) { _store.Log(ex); Notice = "Ошибка сохранения: " + ex.Message; }
        }
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
            if (Games.Any(g => EffectiveProfileIds(g).Contains(SelectedProfile.Id))) { Notice = "Сначала переназначьте игры этого профиля."; return; }
            _effectiveRtssModes.Remove(SelectedProfile.Id);
            ProfileOptions.Remove(ProfileOptions.Single(option => option.Id == SelectedProfile.Id));
            Unhook(SelectedProfile);
            Profiles.Remove(SelectedProfile); SelectedProfile = null; SaveOnly(); UpdateSummary();
        }, () => !_busy);
        AddGameCommand = new Command(() => _ = AddGameAsync(), () => !_busy);
        ChooseExeCommand = new Command(() => _ = ChooseExeAsync(), () => !_busy);
        ImportSteamCommand = new Command(() => _ = ImportSteamAsync(), () => !_busy);
        ImportEpicCommand = new Command(() => _ = ImportEpicAsync(), () => !_busy);
        ApplyCommand = new Command(() => _ = ApplyAllAsync(), () => !_busy);
        ApplyGameCommand = new ParameterCommand(parameter =>
        {
            GameEntry? game = parameter switch
            {
                GameEntryViewModel card => card.Model,
                GameEntry entry => entry,
                _ => null
            };
            if (game is null) { Notice = "Выберите игру."; return; }
            _ = RunSyncAsync((service, state) => service.ApplyOne(state, state.Games.Single(item => item.Id == game.Id)),
                () => Notice = game.Name + ": " + game.Detail, "Применяем профиль игры…");
        }, () => !_busy);
        VerifyCommand = new Command(() => _ = RunSyncAsync((service, state) => service.CheckSaved(state),
            () => Notice = "Проверка сохранённых параметров завершена.", "Проверяем сохранённые параметры…"), () => !_busy);
        RestoreCommand = new Command(() => _ = RestoreSelectedGameAsync(), () => !_busy);
        BackupCommand = new Command(() => _ = RunUserActionAsync("Создаём резервную копию…", async () =>
        {
            var snapshot = AppStateSnapshot.Capture(_state);
            string path = await Task.Run(() => _store.ExportBackup(snapshot));
            Notice = "Резервная копия: " + path;
        }), () => !_busy);
        BrowseRtssCommand = new Command(async () =>
        {
            string? path = await SelectLocalFileAsync("Выберите RTSSHooks64.dll", "RTSSHooks64.dll", RtssDirectory);
            if (path is not null) { RtssDirectory = Path.GetDirectoryName(path)!; SaveOnly(); _ = RefreshIntegrationStatusAsync(); }
        }, () => !_busy);
        StartRtssCommand = new Command(() => _ = StartRtssAsync(), () => !_busy);
        DiagnosticsCommand = new Command(() => _ = RunDiagnosticsAsync(), () => !_busy);
        foreach (var p in Profiles) Hook(p);
        foreach (var g in Games) Hook(g);
        LoadDisplays();
        SelectedProfile = Profiles.FirstOrDefault();
        SelectedGame = Games.FirstOrDefault();
        RefreshDisplays();
        UpdateSummary();
        _ = RefreshIntegrationStatusAsync();
        if (_state.CheckAtStartup)
            DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low,
                () => _ = ApplyAllAsync(checkSavedFirst: true));
        _lastRtssProcessState = RtssClient.ProcessIsRunning;
        _rtssTimer.Interval = TimeSpan.FromSeconds(2);
        _rtssTimer.Tick += (_, _) => { PollRtss(); PollDisplays(); PollPendingGameConfigs(); };
        _rtssTimer.Start();
        _ = ReconcileGameConfigsAsync(forceCheck: true);
        _activityDelayTimer.Tick += (_, _) =>
        {
            _activityDelayTimer.Stop();
            if (IsApplyingAll) IsActivityVisible = true;
        };
    }

    public static AppState LoadStateOrThrow(IStateStore store)
    {
        try { return store.Load(); }
        catch (Exception ex) { throw new StateLoadException(Path.Combine(store.DirectoryPath, "profiles.db"), ex); }
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
                () =>
                {
                    if (_sync.RtssReady)
                        Notice = "RTSS снова запущен. Проверьте профили и при необходимости примените их.";
                });
            if (completed) _lastRtssProcessState = running;
        }
        catch (Exception ex) { _store.Log(ex); Notice = "Ошибка проверки RTSS: " + ex.Message; }
        finally { _rtssPollInFlight = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        _rtssTimer.Stop();
        _activityDelayTimer.Stop();
        if (_saveTimer.IsEnabled) SaveOnly(false);
        foreach (var pair in _profileHandlers.ToArray()) Unhook(pair.Key);
        foreach (var pair in _gameHandlers.ToArray()) Unhook(pair.Key);
    }

    private void RefreshGamesView()
    {
        var desired = GameCards.Where(card =>
            (string.IsNullOrWhiteSpace(Search) || card.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             card.ExePath.Contains(Search, StringComparison.OrdinalIgnoreCase)) &&
            (StatusFilter == "Все статусы" || card.StatusLabel == StatusFilter)).ToArray();
        for (int index = 0; index < desired.Length; index++)
        {
            int old = GamesView.IndexOf(desired[index]);
            if (old < 0) GamesView.Insert(index, desired[index]);
            else if (old != index) GamesView.Move(old, index);
        }
        while (GamesView.Count > desired.Length) GamesView.RemoveAt(GamesView.Count - 1);
        if (SelectedGameCard is not null && !GamesView.Contains(SelectedGameCard))
            SelectedGame = null;
    }

    private void RaiseCommandState()
    {
        foreach (var command in new ICommand[] { AddProfileCommand, DuplicateProfileCommand, RemoveProfileCommand,
                     AddGameCommand, ChooseExeCommand, ImportSteamCommand, ImportEpicCommand,
                     ApplyCommand, ApplyGameCommand, VerifyCommand, RestoreCommand, BackupCommand,
                     BrowseRtssCommand, StartRtssCommand, DiagnosticsCommand })
            if (command is IRefreshableCommand refreshable) refreshable.Refresh();
    }

    private void ScheduleSave()
    {
        if (_busy) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private bool BeginActivity(string message)
    {
        if (_busy || _disposed) return false;
        _busy = true;
        SyncProgressMessage = message;
        SyncProgressPercent = 0;
        SyncProgressTotal = 0;
        IsApplyingAll = true;
        return true;
    }

    private void EndActivity()
    {
        _busy = false;
        IsApplyingAll = false;
        UpdateSummary();
    }

    public async Task RunUserActionAsync(string message, Func<Task> operation)
    {
        if (!BeginActivity(message)) return;
        try { await operation(); }
        catch (Exception ex)
        {
            _store.Log(ex);
            Notice = "Ошибка операции: " + ex.Message;
        }
        finally { EndActivity(); }
    }

    private Task StartRtssAsync() => RunUserActionAsync("Запускаем RTSS…", async () =>
    {
        if (await Task.Run(() => RtssClient.ProcessIsRunning))
        {
            await RefreshIntegrationStatusAsync();
            Notice = RtssWarningVisible ? "RTSS уже запущен, но интеграция недоступна. Проверьте папку RTSS в настройках."
                : "RTSS уже запущен.";
            return;
        }
        string directory = string.IsNullOrWhiteSpace(RtssDirectory) ? RtssClient.DetectDirectory() ?? "" : RtssDirectory;
        string exe = Path.Combine(directory, "RTSS.exe");
        if (!File.Exists(exe))
        {
            Notice = "RTSS.exe не найден. Укажите папку RTSS в настройках.";
            return;
        }
        await Task.Run(() => Process.Start(new ProcessStartInfo(exe)
            { UseShellExecute = true, WorkingDirectory = directory }));
        for (int attempt = 0; attempt < 16 && !await Task.Run(() => RtssClient.ProcessIsRunning); attempt++)
            await Task.Delay(500);
        await RefreshIntegrationStatusAsync();
        Notice = RtssWarningVisible ? "RTSS ещё запускается. Состояние обновится автоматически."
            : "RTSS запущен. Проверьте профили и при необходимости примените их.";
    });
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
    /// только после возврата на поток WinUI. Блокировка команд действует до записи
    /// окончательного состояния и исключает конкурирующее сохранение SQLite.
    /// </summary>
    private async Task<bool> RunSyncAsync(Action<SyncService, AppState> operation, Action? completed = null,
        string message = "Работаем с профилем игры…")
    {
        if (!BeginActivity(message)) return false;
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
            EndActivity();
            SaveOnly(false);
            RefreshDisplays();
            UpdateSummary();
            _ = ReconcileGameConfigsAsync();
        }
    }

    private async Task RefreshIntegrationStatusAsync()
    {
        if (_disposed) return;
        var snapshot = AppStateSnapshot.Capture(_state);
        var error = await _syncCoordinator.ExecuteAsync(snapshot, (service, state) => service.CheckIntegrations(state));
        if (_disposed) return;
        if (error is not null) _store.Log(error);
        Changed(nameof(NvidiaStatus));
        Changed(nameof(RtssStatus));
        Changed(nameof(RtssWarningVisible));
        Changed(nameof(RtssWarning));
        UpdateSummary();
    }

    private async Task RunDiagnosticsAsync()
    {
        if (!BeginActivity("Проверяем игровые профили и подключения…")) return;
        DiagnosticsStatus = "Выполняется чтение RTSS, NVIDIA DRS и локальных параметров…";
        try
        {
            SaveOnly(false, duringSync: true);
            var stateSnapshot = AppStateSnapshot.Capture(_state);
            DiagnosticsProbeSnapshot? probe = null;
            var error = await _syncCoordinator.ExecuteAsync(stateSnapshot,
                (service, state) => probe = service.CheckDiagnostics(state));
            if (error is not null) throw error;
            if (_disposed || probe is null) return;

            AppStateSnapshot.PublishResults(stateSnapshot, _state);
            _latestDiagnosticsProbe = probe;
            DiagnosticsStatus = $"Проверено {probe.CheckedAt.ToLocalTime():g}. Проверка только читает настройки.";
            Notice = "Диагностика завершена. Настройки RTSS и NVIDIA не изменялись.";
        }
        catch (Exception ex)
        {
            _store.Log(ex);
            DiagnosticsStatus = "Проверка не завершена: " + ex.Message;
            Notice = "Ошибка диагностики: " + ex.Message;
        }
        finally
        {
            EndActivity();
            if (!_disposed)
            {
                SaveOnly(false);
                UpdateSummary();
            }
        }
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

public sealed record AttentionItem(string Category, string Subject, string Title, string Detail, string Advice,
    int PageIndex, string ActionLabel, GameEntry? Game, DiagnosticSeverity Severity,
    DiagnosticRule RuleId, bool CanAutoFix);

public sealed class Command(Action execute, Func<bool>? canExecute = null) : ICommand, IRefreshableCommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ParameterCommand(Action<object?> execute, Func<bool>? canExecute = null) : ICommand, IRefreshableCommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public interface IRefreshableCommand { void Refresh(); }

public sealed class ModeOption<T>(T value, string label) : Observable
{
    private string _label = label;
    public T Value { get; } = value;
    public string Label { get => _label; set => Set(ref _label, value); }
}
