using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using GameProfileManager.Core;
using GameProfileManager.Infrastructure;
using GameProfileManager.Integrations;
using Microsoft.Win32;

namespace GameProfileManager;

public sealed class MainViewModel : Observable
{
    private readonly SqliteStateStore _store;
    private readonly SyncService _sync;
    private readonly AppState _state;
    private string _search = "";
    private string _statusFilter = "Все статусы";
    private string _notice = "Готово";
    private GameEntry? _selectedGame;
    private PerformanceProfile? _selectedProfile;
    private bool _busy;

    public ObservableCollection<GameEntry> Games => _state.Games;
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
    public bool SelectedGameHasBackup => SelectedGame is not null && _state.Backups.Any(b => b.ExePath.Equals(SelectedGame.ExePath, StringComparison.OrdinalIgnoreCase));
    public PerformanceProfile? SelectedProfile { get => _selectedProfile; set => Set(ref _selectedProfile, value); }
    public string RtssDirectory { get => _state.RtssDirectory; set { _state.RtssDirectory = value; Changed(nameof(RtssDirectory)); } }
    public bool CheckAtStartup { get => _state.CheckAtStartup; set { _state.CheckAtStartup = value; Changed(nameof(CheckAtStartup)); } }
    public bool AutoStart
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("GameProfileManager") is not null; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue("GameProfileManager", '"' + Environment.ProcessPath! + '"');
            else key.DeleteValue("GameProfileManager", false);
            Changed(nameof(AutoStart));
        }
    }
    public string NvidiaStatus => _sync.NvidiaStatus ?? "Проверка...";
    public string RtssStatus => _sync.RtssStatus ?? "Проверка...";
    public string LastSync => _state.LastSync?.ToLocalTime().ToString("g") ?? "Никогда";
    public int ErrorCount => Games.Count(g => g.Status is ApplyStatus.Error or ApplyStatus.Unsupported);
    public int GameCount => Games.Count;
    public int ProfileCount => Profiles.Count;
    public string DataDirectory => _store.DirectoryPath;

    public ICommand AddProfileCommand { get; }
    public ICommand RemoveProfileCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand RemoveGameCommand { get; }
    public ICommand ChooseExeCommand { get; }
    public ICommand ImportSteamCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand VerifyCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand BackupCommand { get; }
    public ICommand BrowseRtssCommand { get; }

    public MainViewModel(SqliteStateStore? store = null)
    {
        _store = store ?? new SqliteStateStore();
        _sync = new SyncService(_store);
        try { _state = _store.Load(); }
        catch (Exception ex) { _store.Log(ex); _state = new AppState(); Notice = "Ошибка чтения SQLite: " + ex.Message; }
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
        AddProfileCommand = new Command(() => { var p = new PerformanceProfile(); Profiles.Add(p); ProfileOptions.Add(new ProfileOption(p)); Hook(p); SelectedProfile = p; SaveOnly(); UpdateSummary(); });
        RemoveProfileCommand = new Command(() =>
        {
            if (SelectedProfile is null) return;
            if (Games.Any(g => g.ProfileId == SelectedProfile.Id)) { Notice = "Сначала переназначьте игры этого профиля."; return; }
            ProfileOptions.Remove(ProfileOptions.Single(option => option.Id == SelectedProfile.Id));
            Profiles.Remove(SelectedProfile); SelectedProfile = null; SaveOnly(); UpdateSummary();
        });
        AddGameCommand = new Command(AddGame);
        RemoveGameCommand = new Command(() =>
        {
            if (SelectedGame is null) return;
            if (_state.Backups.Any(b => b.ExePath.Equals(SelectedGame.ExePath, StringComparison.OrdinalIgnoreCase)))
            {
                Run(() => _sync.Restore(_state, SelectedGame));
                if (SelectedGame.Status == ApplyStatus.Error) { Notice = SelectedGame.Detail; return; }
            }
            Games.Remove(SelectedGame); SelectedGame = null; SaveOnly(); UpdateSummary();
        });
        ChooseExeCommand = new Command(ChooseExe);
        ImportSteamCommand = new Command(ImportSteam);
        SaveCommand = new Command(SaveOnly);
        ApplyCommand = new Command(() => Run(() => { SaveOnly(); _sync.ApplyAll(_state); RefreshDisplays(); Notice = "Применение завершено. Проверьте статусы игр."; }));
        VerifyCommand = new Command(() => Run(() => { _sync.CheckSaved(_state); RefreshDisplays(); SaveOnly(); Notice = "Проверка сохранённых параметров завершена."; }));
        RestoreCommand = new Command(() => Run(() =>
        {
            if (SelectedGame is null) { Notice = "Выберите игру."; return; }
            _sync.Restore(_state, SelectedGame); RefreshDisplays(); Notice = SelectedGame.Status == ApplyStatus.Error ? SelectedGame.Detail : "Исходные параметры восстановлены.";
        }));
        BackupCommand = new Command(() => Run(() => Notice = "Резервная копия: " + _store.ExportBackup(_state)));
        BrowseRtssCommand = new Command(() =>
        {
            var dialog = new OpenFileDialog { Title = "Выберите RTSSHooks64.dll", Filter = "RTSSHooks64.dll|RTSSHooks64.dll" };
            if (dialog.ShowDialog() == true) { RtssDirectory = Path.GetDirectoryName(dialog.FileName)!; SaveOnly(); _sync.CheckIntegrations(_state); UpdateSummary(); }
        });
        foreach (var p in Profiles) Hook(p);
        foreach (var g in Games) Hook(g);
        SelectedProfile = Profiles.FirstOrDefault();
        SelectedGame = Games.FirstOrDefault();
        RefreshDisplays();
        _sync.CheckIntegrations(_state);
        UpdateSummary();
        if (_state.CheckAtStartup)
        {
            Run(() =>
            {
                _sync.CheckSaved(_state);
                if (Games.Any(g => g.Status != ApplyStatus.Applied && g.ProfileId is not null && !string.IsNullOrWhiteSpace(g.ExePath)) ||
                    Games.Any(g => g.ProfileId is null && _state.Backups.Any(b => b.ManagedGameId == g.Id)))
                    _sync.ApplyAll(_state);
                RefreshDisplays(); SaveOnly();
            });
        }
    }

    private void AddGame()
    {
        var dialog = new OpenFileDialog { Title = "Выберите игровой .exe", Filter = "Приложения (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true) return;
        if (Games.Any(g => g.ExePath.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase))) { Notice = "Этот .exe уже добавлен."; return; }
        var game = new GameEntry { Name = Path.GetFileNameWithoutExtension(dialog.FileName), ExePath = dialog.FileName, Detail = "Выберите профиль" };
        Games.Add(game); Hook(game); SelectedGame = game; RefreshDisplays(); SaveOnly(); UpdateSummary();
    }
    private void ChooseExe()
    {
        if (SelectedGame is null) { Notice = "Выберите игру."; return; }
        var dialog = new OpenFileDialog { Title = "Выберите игровой процесс", Filter = "Приложения (*.exe)|*.exe", InitialDirectory = SelectedGame.InstallDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
        if (dialog.ShowDialog() != true) return;
        if (!SelectedGame.ExePath.Equals(dialog.FileName, StringComparison.OrdinalIgnoreCase) &&
            _state.Backups.Any(b => b.ExePath.Equals(SelectedGame.ExePath, StringComparison.OrdinalIgnoreCase)))
        {
            Run(() => _sync.Restore(_state, SelectedGame));
            if (SelectedGame.Status == ApplyStatus.Error) { Notice = SelectedGame.Detail; return; }
        }
        SelectedGame.ExePath = dialog.FileName;
        SelectedGame.Status = ApplyStatus.Pending;
        SelectedGame.Detail = "Нужно применить профиль";
        SaveOnly();
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
    private void Hook(PerformanceProfile profile) => profile.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(PerformanceProfile.Name) or nameof(PerformanceProfile.FpsLimit) or nameof(PerformanceProfile.Vrr) or nameof(PerformanceProfile.VSync))
        {
            if (e.PropertyName == nameof(PerformanceProfile.Name)) ProfileOptions.First(option => option.Id == profile.Id).RefreshName();
            foreach (var game in Games.Where(g => g.ProfileId == profile.Id)) { game.Status = ApplyStatus.Pending; game.Detail = "Профиль изменён"; }
            RefreshDisplays();
        }
    };
    private void Hook(GameEntry game) => game.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(GameEntry.ProfileId) or nameof(GameEntry.ExePath))
        {
            if (e.PropertyName == nameof(GameEntry.ExePath)) game.Icon = null;
            game.Status = ApplyStatus.Pending;
            game.Detail = game.ProfileId is null
                ? _state.Backups.Any(b => b.ManagedGameId == game.Id)
                    ? "Профиль отключён. Нажмите «Применить», чтобы восстановить исходные параметры."
                    : "Профиль не выбран: параметры не применяются"
                : "Назначение изменено";
            RefreshDisplays();
            if (e.PropertyName == nameof(GameEntry.ProfileId)) SaveOnly();
        }
    };
    private void RefreshDisplays()
    {
        foreach (var game in Games)
        {
            var profile = Profiles.FirstOrDefault(p => p.Id == game.ProfileId);
            game.FpsDisplay = profile?.FpsLimit.ToString() ?? "—";
            game.VrrDisplay = profile is null ? "—" : ProfileLabels.Vrr(profile.Vrr);
            game.VSyncDisplay = profile is null ? "—" : ProfileLabels.VSync(profile.VSync);
            if (game.Icon is null) game.Icon = FileIcon.Load(game);
            game.Changed(nameof(GameEntry.FpsDisplay)); game.Changed(nameof(GameEntry.VrrDisplay)); game.Changed(nameof(GameEntry.VSyncDisplay));
            game.Changed(nameof(GameEntry.Icon));
        }
        if (StatusFilter != "Все статусы") GamesView.Refresh();
        UpdateSummary();
    }
    private void SaveOnly()
    {
        try { _store.Save(_state); Notice = "Конфигурация сохранена."; }
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
        Changed(nameof(GameCount)); Changed(nameof(ProfileCount)); Changed(nameof(ErrorCount));
        Changed(nameof(LastSync)); Changed(nameof(NvidiaStatus)); Changed(nameof(RtssStatus));
        Changed(nameof(SelectedGameHasBackup));
    }
}

public sealed class Command(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}

public sealed record ModeOption<T>(T Value, string Label);
