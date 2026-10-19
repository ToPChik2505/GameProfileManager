using GameProfileManager.Core;

namespace GameProfileManager.Integrations;

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
    (bool ProfileExisted, uint? Vrr, bool VrrExplicit, uint? VSync, bool VSyncExplicit, uint? Fps, bool FpsExplicit) Read(string exePath);
    void ProbeWrite();
    void Apply(GameEntry game, PerformanceProfile desired);
    void Restore(string exePath, SettingBackup backup);
}
