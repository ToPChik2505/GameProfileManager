using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace GameProfileManager.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    public void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum VrrMode { GSync, FixedRefresh }
public enum VSyncMode { ApplicationControlled, On, Off }
public enum ApplyStatus { Pending, Applied, RestartRequired, Unsupported, Error }

public static class ProfileLabels
{
    public static string Vrr(VrrMode mode) => mode switch
    {
        VrrMode.GSync => "G-SYNC",
        VrrMode.FixedRefresh => "Фиксированная частота",
        _ => mode.ToString()
    };

    public static string VSync(VSyncMode mode) => mode switch
    {
        VSyncMode.ApplicationControlled => "Управляется игрой",
        VSyncMode.On => "Включена",
        VSyncMode.Off => "Выключена",
        _ => mode.ToString()
    };
}

public sealed class PerformanceProfile : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _name = "Новый профиль";
    private int _fpsLimit = 60;
    private VrrMode _vrr = VrrMode.GSync;
    private VSyncMode _vSync = VSyncMode.On;
    private string _description = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public int FpsLimit { get => _fpsLimit; set => Set(ref _fpsLimit, value); }
    public VrrMode Vrr { get => _vrr; set { if (Set(ref _vrr, value)) Changed(nameof(VrrDisplay)); } }
    public VSyncMode VSync { get => _vSync; set => Set(ref _vSync, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    [JsonIgnore] public string VrrDisplay => ProfileLabels.Vrr(Vrr);
}

public sealed class GameEntry : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _name = "";
    private string _exePath = "";
    private Guid? _profileId;
    private ApplyStatus _status = ApplyStatus.Pending;
    private string _detail = "Исполняемый файл и профиль ещё не назначены";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string ExePath { get => _exePath; set { if (Set(ref _exePath, value)) { Changed(nameof(ExeDisplay)); Changed(nameof(StatusLabel)); } } }
    [JsonIgnore] public string ExeDisplay => string.IsNullOrWhiteSpace(ExePath) ? "Нужно выбрать игровой .exe" : ExePath;
    public Guid? ProfileId { get => _profileId; set { if (Set(ref _profileId, value)) { Changed(nameof(ProfileSelectionId)); Changed(nameof(StatusLabel)); } } }
    [JsonIgnore] public Guid ProfileSelectionId { get => ProfileId ?? Guid.Empty; set => ProfileId = value == Guid.Empty ? null : value; }
    public ApplyStatus Status { get => _status; set { if (Set(ref _status, value)) Changed(nameof(StatusLabel)); } }
    [JsonIgnore] public string StatusLabel => Status switch
    {
        ApplyStatus.Error => "Ошибка",
        ApplyStatus.Unsupported => "Недоступно",
        _ when ProfileId is null => "Без профиля",
        _ when string.IsNullOrWhiteSpace(ExePath) => "Нужен .exe",
        ApplyStatus.Applied => "Применён",
        ApplyStatus.RestartRequired => "Перезапустите",
        _ => "Ожидает"
    };
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string? SteamAppId { get; set; }
    public string? InstallDirectory { get; set; }
    public DateTimeOffset? LastApplied { get; set; }
    [JsonIgnore] public string FpsDisplay { get; set; } = "—";
    [JsonIgnore] public string VrrDisplay { get; set; } = "—";
    [JsonIgnore] public string VSyncDisplay { get; set; } = "—";
    [JsonIgnore] public ImageSource? Icon { get; set; }
}

public sealed class SettingBackup
{
    public string ExePath { get; set; } = "";
    public int? RtssFps { get; set; }
    public bool RtssProfileExisted { get; set; }
    public bool RtssCaptured { get; set; }
    public uint? NvidiaVrr { get; set; }
    public uint? NvidiaVSync { get; set; }
    public uint? NvidiaFpsLimit { get; set; }
    public bool NvidiaVrrExplicit { get; set; }
    public bool NvidiaVSyncExplicit { get; set; }
    public bool NvidiaFpsLimitExplicit { get; set; }
    public bool NvidiaProfileExisted { get; set; }
    public bool NvidiaCaptured { get; set; }
    public Guid ManagedGameId { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
}

public sealed class AppState
{
    public ObservableCollection<PerformanceProfile> Profiles { get; set; } = new();
    public ObservableCollection<GameEntry> Games { get; set; } = new();
    public List<SettingBackup> Backups { get; set; } = new();
    public bool PresetNamesLocalized { get; set; }
    public string RtssDirectory { get; set; } = "";
    public bool CheckAtStartup { get; set; } = true;
    public DateTimeOffset? LastSync { get; set; }
}
