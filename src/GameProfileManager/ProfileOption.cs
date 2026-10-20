using GameProfileManager.Core;

namespace GameProfileManager;

public sealed class ProfileOption(PerformanceProfile? profile) : Observable
{
    public Guid Id => profile?.Id ?? Guid.Empty;
    public string Name => profile?.Name ?? "Без профиля";
    public void RefreshName() => Changed(nameof(Name));
}

