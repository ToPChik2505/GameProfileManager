using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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
public enum FpsLimitMode { Manual, DisplayFraction }
public enum RtssLimiterMode { Async = 0, FrontEdgeSync = 1, BackEdgeSync = 2, NvidiaReflex = 3 }
public enum VSyncMode { ApplicationControlled, On, Off }
public enum ApplyStatus { Pending, Applied, RestartRequired, Unsupported, Error }
public enum DisplayProfilePolicy { PreferredDisplay, WindowsPrimary, PinnedDisplay }

public sealed class DisplayProfileAssignment
{
    public string DisplayId { get; set; } = "";
    public Guid ProfileId { get; set; }
}

public static class ProfileLabels
{
    public static string Rtss(RtssLimiterMode mode) => mode switch
    {
        RtssLimiterMode.Async => "Асинхронный (Async)",
        RtssLimiterMode.FrontEdgeSync => "Передний фронт (Front Edge Sync)",
        RtssLimiterMode.BackEdgeSync => "Задний фронт (Back Edge Sync)",
        RtssLimiterMode.NvidiaReflex => "NVIDIA Reflex",
        _ => $"Неизвестный режим ({(int)mode})"
    };

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

public static class ProfilePresets
{
    private static readonly HashSet<string> LegacyNames = new(StringComparer.Ordinal)
    {
        "Кинематографичный", "Сбалансированный", "Соревновательный",
        "Cinematic", "Balanced", "Competitive"
    };

    public static bool ProtectLegacy(AppState state)
    {
        if (state.PresetProtectionMigrated) return false;
        foreach (var profile in state.Profiles.Take(3))
            if (LegacyNames.Contains(profile.Name)) profile.IsBuiltIn = true;
        state.PresetProtectionMigrated = true;
        return true;
    }

    public static PerformanceProfile EditableCopy(PerformanceProfile source) => new()
    {
        Name = source.Name + " — копия",
        FpsLimit = source.FpsLimit,
        FpsMode = source.FpsMode,
        FpsDivisor = source.FpsDivisor,
        Vrr = source.Vrr,
        VSync = source.VSync,
        RtssLimiterMode = source.RtssLimiterMode,
        Description = source.Description
    };
}

public static class RtssLimiterModeResolver
{
    public static RtssLimiterMode Resolve(AppState state, PerformanceProfile profile) =>
        profile.RtssLimiterMode ?? state.RtssLimiterMode;
}

public sealed class PerformanceProfile : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _name = "Новый профиль";
    private int _fpsLimit = 60;
    private FpsLimitMode _fpsMode;
    private int _fpsDivisor = 2;
    private VrrMode _vrr = VrrMode.GSync;
    private VSyncMode _vSync = VSyncMode.On;
    private RtssLimiterMode? _rtssLimiterMode;
    private string _description = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public int FpsLimit { get => _fpsLimit; set => Set(ref _fpsLimit, value); }
    public FpsLimitMode FpsMode { get => _fpsMode; set => Set(ref _fpsMode, value); }
    public int FpsDivisor { get => _fpsDivisor; set => Set(ref _fpsDivisor, value); }
    public VrrMode Vrr { get => _vrr; set { if (Set(ref _vrr, value)) Changed(nameof(VrrDisplay)); } }
    public VSyncMode VSync { get => _vSync; set => Set(ref _vSync, value); }
    public RtssLimiterMode? RtssLimiterMode { get => _rtssLimiterMode; set => Set(ref _rtssLimiterMode, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public bool IsBuiltIn { get; set; }
    [JsonIgnore] public string VrrDisplay => ProfileLabels.Vrr(Vrr);
    [JsonIgnore] public string FpsPreview { get; set; } = "—";
}

public sealed class GameEntry : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    private string _name = "";
    private string _exePath = "";
    private Guid? _profileId;
    private string _displayId = "";
    private ApplyStatus _status = ApplyStatus.Pending;
    private string _detail = "Исполняемый файл и профиль ещё не назначены";
    private bool _autoSwitchProfile;
    private DisplayProfilePolicy _displayProfilePolicy;
    private string _displayProfileTargetId = "";
    public ObservableCollection<DisplayProfileAssignment> DisplayProfileAssignments { get; set; } = new();
    public bool AutoSwitchProfile { get => _autoSwitchProfile; set { if (Set(ref _autoSwitchProfile, value)) Changed(nameof(StatusLabel)); } }
    public DisplayProfilePolicy DisplayProfilePolicy { get => _displayProfilePolicy; set => Set(ref _displayProfilePolicy, value); }
    public string DisplayProfileTargetId { get => _displayProfileTargetId; set => Set(ref _displayProfileTargetId, value ?? ""); }
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
        _ when ProfileId is null && !AutoSwitchProfile => "Без профиля",
        _ when string.IsNullOrWhiteSpace(ExePath) => "Нужен .exe",
        ApplyStatus.Applied => "Применён",
        ApplyStatus.RestartRequired => "Перезапустите",
        _ => "Ожидает"
    };
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    private string? _steamAppId;
    private string? _epicAppName;
    private string? _installDirectory;
    public string? SteamAppId { get => _steamAppId; set => Set(ref _steamAppId, value); }
    public string? EpicAppName { get => _epicAppName; set => Set(ref _epicAppName, value); }
    public string? InstallDirectory { get => _installDirectory; set => Set(ref _installDirectory, value); }
    public string DisplayId { get => _displayId; set => Set(ref _displayId, value ?? ""); }
    public DateTimeOffset? LastApplied { get; set; }
    [JsonIgnore] public string FpsDisplay { get; set; } = "—";
    [JsonIgnore] public string ProfileDisplay { get; set; } = "Без профиля";
    [JsonIgnore] public string VrrDisplay { get; set; } = "—";
    [JsonIgnore] public string VSyncDisplay { get; set; } = "—";
    [JsonIgnore] public string RtssLimiterDisplay { get; set; } = "—";
}

public sealed class SettingBackup
{
    public string ExePath { get; set; } = "";
    public int? RtssFps { get; set; }
    public int? RtssSyncLimiter { get; set; }
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
    public bool PresetProtectionMigrated { get; set; }
    public string RtssDirectory { get; set; } = "";
    public RtssLimiterMode RtssLimiterMode { get; set; } = RtssLimiterMode.FrontEdgeSync;
    public bool CheckAtStartup { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public string Theme { get; set; } = "Dark";
    public string DefaultDisplayId { get; set; } = "";
    public Dictionary<string, string> DisplayNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastSync { get; set; }
}

public static class DisplayNameCache
{
    public static string Get(AppState state, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Неизвестный монитор";
        var pair = state.DisplayNames.FirstOrDefault(entry => entry.Key.Equals(id, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(pair.Value) ? id : pair.Value;
    }
}
