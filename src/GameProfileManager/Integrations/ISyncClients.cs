using GameProfileManager.Core;

namespace GameProfileManager.Integrations;

/// <summary>
/// Снимок значений NVIDIA DRS с признаками явно заданных параметров.
/// Различие между унаследованным и явным значением важно для правильного восстановления.
/// </summary>
public readonly record struct NvidiaSettingsSnapshot(
    bool ProfileExisted,
    uint? Vrr,
    bool VrrExplicit,
    uint? VSync,
    bool VSyncExplicit,
    uint? Fps,
    bool FpsExplicit);

public interface IRtssSettings : IDisposable
{
    string InstallDirectory { get; }
    bool IsRunning { get; }
    bool ProfileExists(string exeName);
    int? ReadFps(string exeName);
    int? ReadLimiterMode(string exeName);
    void ProbeWrite(string exeName, int? targetFps);
    void WriteSettings(string exeName, int fps, RtssLimiterMode mode);
    void Restore(string exeName, bool existed, int? previousFps, int? previousLimiterMode);
}

public interface INvidiaSettings : IDisposable
{
    NvidiaSettingsSnapshot Read(string exePath);
    void ProbeWrite();
    void Apply(GameEntry game, PerformanceProfile desired);
    void Restore(string exePath, SettingBackup backup);
}
