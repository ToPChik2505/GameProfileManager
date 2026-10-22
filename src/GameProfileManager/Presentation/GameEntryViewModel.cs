using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;
using GameProfileManager.Core;

namespace GameProfileManager.Presentation;

/// <summary>Представляет игровую запись для WinUI и хранит только UI-состояние иконки.</summary>
public sealed class GameEntryViewModel : Observable, IDisposable
{
    private readonly GameIconLoader _iconLoader;
    private WeakReference<ImageSource>? _icon;
    private bool _iconResolved;
    private bool _disposed;
    private string _profileChoiceDetail = "Профиль ещё не выбран";

    internal GameEntryViewModel(GameEntry model, GameIconLoader iconLoader)
    {
        Model = model;
        _iconLoader = iconLoader;
        Model.PropertyChanged += ModelPropertyChanged;
    }

    /// <summary>Связанная запись состояния, используемая сценариями приложения.</summary>
    public GameEntry Model { get; }
    public Guid Id => Model.Id;
    public string Name { get => Model.Name; set => Model.Name = value; }
    public string ExePath { get => Model.ExePath; set => Model.ExePath = value; }
    public string ExeDisplay => Model.ExeDisplay;
    public string ExeName => string.IsNullOrWhiteSpace(Model.ExePath)
        ? "EXE не выбран" : Path.GetFileName(Model.ExePath);
    public Guid? ProfileId { get => Model.ProfileId; set => Model.ProfileId = value; }
    public Guid ProfileSelectionId { get => Model.ProfileSelectionId; set => Model.ProfileSelectionId = value; }
    public string DisplayId { get => Model.DisplayId; set => Model.DisplayId = value; }
    public bool AutoSwitchProfile { get => Model.AutoSwitchProfile; set => Model.AutoSwitchProfile = value; }
    public DisplayProfilePolicy DisplayProfilePolicy { get => Model.DisplayProfilePolicy; set => Model.DisplayProfilePolicy = value; }
    public string ProfileModeDescription => AutoSwitchProfile
        ? "Для экрана без назначения используется профиль игры."
        : "Этот профиль не меняется при смене монитора.";
    public string DisplayPolicyDescription => DisplayProfilePolicy switch
    {
        DisplayProfilePolicy.WindowsPrimary => "Профиль и доля FPS следуют за основным экраном Windows, а не за окном игры.",
        DisplayProfilePolicy.PinnedDisplay => "Используется только экран, выбранный ниже. Если он отключён, настройки не переключаются.",
        _ => "Берётся экран, выбранный ниже. Если он не задан — общий экран по умолчанию, затем основной экран Windows. Отключённый выбранный экран не подменяется."
    };
    public string FpsScreenDescription => AutoSwitchProfile switch
    {
        false => "Если в профиле задана доля частоты, лимит FPS рассчитывается по этому экрану. Профиль игры не меняется.",
        true when DisplayProfilePolicy == DisplayProfilePolicy.WindowsPrimary => "При этом правиле профиль и доля FPS берутся с основного экрана Windows. Выбор ниже понадобится для других правил.",
        _ => "Этот экран определяет профиль и лимит FPS по доле частоты. Если лимит задан числом, экран не влияет на FPS."
    };
    public string ProfileChoiceDetail { get => _profileChoiceDetail; internal set => Set(ref _profileChoiceDetail, value); }
    public ObservableCollection<DisplayProfileAssignmentRow> DisplayAssignmentRows { get; } = new();
    public bool HasDisplayAssignmentRows => DisplayAssignmentRows.Count > 0;
    public string DisplayProfileTargetId { get => Model.DisplayProfileTargetId; set => Model.DisplayProfileTargetId = value; }
    public Guid DisplayAssignmentProfileSelectionId =>
        Model.DisplayProfileAssignments.FirstOrDefault(assignment =>
            assignment.DisplayId.Equals(Model.DisplayProfileTargetId, StringComparison.OrdinalIgnoreCase))?.ProfileId ?? Guid.Empty;
    public ApplyStatus Status => Model.Status;
    public string StatusLabel => Model.StatusLabel;
    public string Detail { get => Model.Detail; set => Model.Detail = value; }
    public string ProfileDisplay => Model.ProfileDisplay;
    public string FpsDisplay => Model.FpsDisplay;
    public string VrrDisplay => Model.VrrDisplay;
    public string VSyncDisplay => Model.VSyncDisplay;
    public string RtssLimiterDisplay => Model.RtssLimiterDisplay;

    internal void UpdateDisplayAssignmentRows(AppState state, IReadOnlyList<DisplayInfo> displays)
    {
        var wanted = new List<(string Id, string Name, string Detail)>();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var display in displays)
            if (!string.IsNullOrWhiteSpace(display.Id) && known.Add(display.Id))
                wanted.Add((display.Id, display.Name,
                    (display.IsPrimary ? "Основной экран Windows" : "Подключён") + " · " + display.RefreshLabel));
        foreach (var id in Model.DisplayProfileAssignments.Select(assignment => assignment.DisplayId)
                     .Append(Model.DisplayId).Append(Model.DisplayProfileTargetId))
            if (!string.IsNullOrWhiteSpace(id) && known.Add(id))
                wanted.Add((id, DisplayNameCache.Get(state, id), "Отключён"));

        for (int index = 0; index < wanted.Count; index++)
        {
            var item = wanted[index];
            int existing = -1;
            for (int i = index; i < DisplayAssignmentRows.Count; i++)
                if (DisplayAssignmentRows[i].DisplayId.Equals(item.Id, StringComparison.OrdinalIgnoreCase))
                { existing = i; break; }
            if (existing < 0) DisplayAssignmentRows.Insert(index, new DisplayProfileAssignmentRow(Model, item.Id));
            else if (existing != index) DisplayAssignmentRows.Move(existing, index);
            DisplayAssignmentRows[index].Update(item.Name, item.Detail);
        }
        while (DisplayAssignmentRows.Count > wanted.Count) DisplayAssignmentRows.RemoveAt(DisplayAssignmentRows.Count - 1);
        Changed(nameof(HasDisplayAssignmentRows));
    }

    internal void RefreshDisplayAssignment(string displayId)
    {
        foreach (var row in DisplayAssignmentRows.Where(row =>
                     row.DisplayId.Equals(displayId, StringComparison.OrdinalIgnoreCase)))
            row.Changed(nameof(DisplayProfileAssignmentRow.ProfileSelectionId));
    }

    /// <summary>Запрашивает изображение лениво; файловые и нативные вызовы выполняются фоновым сервисом.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (_icon is not null && _icon.TryGetTarget(out var image)) return image;
            if (!_iconResolved || _icon is not null)
            {
                _iconResolved = true;
                string exePath = Model.ExePath;
                string? steamAppId = Model.SteamAppId;
                _iconLoader.Request(Model, image =>
                {
                    if (Model.ExePath.Equals(exePath, StringComparison.OrdinalIgnoreCase) && Model.SteamAppId == steamAppId)
                        PublishIcon(image);
                });
            }
            return _icon is not null && _icon.TryGetTarget(out var loaded) ? loaded : null;
        }
    }

    private void PublishIcon(ImageSource? image)
    {
        if (_disposed) return;
        _icon = image is null ? null : new WeakReference<ImageSource>(image);
        _iconResolved = true;
        Changed(nameof(Icon));
    }

    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameEntry.ExePath) or nameof(GameEntry.SteamAppId))
        {
            _icon = null;
            _iconResolved = false;
            Changed(nameof(Icon));
        }
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            Changed(nameof(Name)); Changed(nameof(ExeDisplay)); Changed(nameof(ExeName)); Changed(nameof(ProfileSelectionId));
            Changed(nameof(ProfileModeDescription)); Changed(nameof(DisplayPolicyDescription)); Changed(nameof(FpsScreenDescription));
            Changed(nameof(StatusLabel)); Changed(nameof(Detail)); Changed(nameof(ProfileDisplay));
            Changed(nameof(FpsDisplay)); Changed(nameof(VrrDisplay)); Changed(nameof(VSyncDisplay));
            Changed(nameof(RtssLimiterDisplay));
            return;
        }
        if (e.PropertyName == nameof(GameEntry.DisplayProfileTargetId)) Changed(nameof(DisplayAssignmentProfileSelectionId));
        if (e.PropertyName == nameof(GameEntry.AutoSwitchProfile))
        {
            Changed(nameof(ProfileModeDescription));
            Changed(nameof(FpsScreenDescription));
        }
        if (e.PropertyName == nameof(GameEntry.DisplayProfilePolicy))
        {
            Changed(nameof(DisplayPolicyDescription));
            Changed(nameof(FpsScreenDescription));
        }
        Changed(e.PropertyName switch
        {
            nameof(GameEntry.Name) => nameof(Name),
            nameof(GameEntry.ExePath) => nameof(ExePath),
            nameof(GameEntry.ProfileId) => nameof(ProfileId),
            nameof(GameEntry.ProfileSelectionId) => nameof(ProfileSelectionId),
            nameof(GameEntry.DisplayId) => nameof(DisplayId),
            nameof(GameEntry.DisplayProfileTargetId) => nameof(DisplayProfileTargetId),
            nameof(GameEntry.AutoSwitchProfile) => nameof(AutoSwitchProfile),
            nameof(GameEntry.DisplayProfilePolicy) => nameof(DisplayProfilePolicy),
            nameof(GameEntry.Status) => nameof(Status),
            nameof(GameEntry.StatusLabel) => nameof(StatusLabel),
            nameof(GameEntry.Detail) => nameof(Detail),
            nameof(GameEntry.ProfileDisplay) => nameof(ProfileDisplay),
            nameof(GameEntry.FpsDisplay) => nameof(FpsDisplay),
            nameof(GameEntry.VrrDisplay) => nameof(VrrDisplay),
            nameof(GameEntry.VSyncDisplay) => nameof(VSyncDisplay),
            nameof(GameEntry.RtssLimiterDisplay) => nameof(RtssLimiterDisplay),
            _ => e.PropertyName
        });
        if (e.PropertyName == nameof(GameEntry.ExePath))
        { Changed(nameof(ExeDisplay)); Changed(nameof(ExeName)); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Model.PropertyChanged -= ModelPropertyChanged;
    }
}

public sealed class DisplayProfileAssignmentRow(GameEntry game, string displayId) : Observable
{
    private string _name = "";
    private string _detail = "";
    public GameEntry Game => game;
    public string DisplayId => displayId;
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public Guid ProfileSelectionId => game.DisplayProfileAssignments.FirstOrDefault(assignment =>
        assignment.DisplayId.Equals(displayId, StringComparison.OrdinalIgnoreCase))?.ProfileId ?? Guid.Empty;
    internal void Update(string name, string detail) { Name = name; Detail = detail; }
}
