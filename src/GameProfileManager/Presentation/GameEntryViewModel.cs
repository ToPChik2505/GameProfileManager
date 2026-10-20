using System.ComponentModel;
using System.Windows.Media;
using GameProfileManager.Core;

namespace GameProfileManager.Presentation;

/// <summary>Представляет игровую запись для WPF и хранит только UI-состояние иконки.</summary>
public sealed class GameEntryViewModel : Observable, IDisposable
{
    private readonly GameIconLoader _iconLoader;
    private WeakReference<ImageSource>? _icon;
    private bool _iconResolved;
    private bool _disposed;

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
    public Guid? ProfileId { get => Model.ProfileId; set => Model.ProfileId = value; }
    public Guid ProfileSelectionId { get => Model.ProfileSelectionId; set => Model.ProfileSelectionId = value; }
    public string DisplayId { get => Model.DisplayId; set => Model.DisplayId = value; }
    public ApplyStatus Status => Model.Status;
    public string StatusLabel => Model.StatusLabel;
    public string Detail { get => Model.Detail; set => Model.Detail = value; }
    public string ProfileDisplay => Model.ProfileDisplay;
    public string FpsDisplay => Model.FpsDisplay;
    public string VrrDisplay => Model.VrrDisplay;
    public string VSyncDisplay => Model.VSyncDisplay;
    public string RtssLimiterDisplay => Model.RtssLimiterDisplay;

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
            Changed(nameof(Name)); Changed(nameof(ExeDisplay)); Changed(nameof(ProfileSelectionId));
            Changed(nameof(StatusLabel)); Changed(nameof(Detail)); Changed(nameof(ProfileDisplay));
            Changed(nameof(FpsDisplay)); Changed(nameof(VrrDisplay)); Changed(nameof(VSyncDisplay));
            Changed(nameof(RtssLimiterDisplay));
            return;
        }
        Changed(e.PropertyName switch
        {
            nameof(GameEntry.Name) => nameof(Name),
            nameof(GameEntry.ExePath) => nameof(ExePath),
            nameof(GameEntry.ProfileId) => nameof(ProfileId),
            nameof(GameEntry.ProfileSelectionId) => nameof(ProfileSelectionId),
            nameof(GameEntry.DisplayId) => nameof(DisplayId),
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
        if (e.PropertyName == nameof(GameEntry.ExePath)) Changed(nameof(ExeDisplay));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Model.PropertyChanged -= ModelPropertyChanged;
    }
}
