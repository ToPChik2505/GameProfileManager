using GameProfileManager.Core;
using GameProfileManager.Integrations;

namespace GameProfileManager;

internal static class SmokeAudit
{
    public static int WriteAttempts;
    public static void RejectWrite(string target)
    {
        Interlocked.Increment(ref WriteAttempts);
        throw new InvalidOperationException("UI Smoke forbids " + target + " writes.");
    }
}

internal sealed class SmokeRtssClient : IRtssSettings
{
    public string InstallDirectory => "UI Smoke (fake)";
    public bool IsRunning => false;
    public bool ProfileExists(string exeName) => false;
    public int? ReadFps(string exeName) => null;
    public int? ReadLimiterMode(string exeName) => null;
    public void ProbeWrite(string exeName, int? targetFps) => SmokeAudit.RejectWrite("RTSS");
    public void WriteSettings(string exeName, int fps, RtssLimiterMode mode) => SmokeAudit.RejectWrite("RTSS");
    public void Restore(string exeName, bool existed, int? previousFps, int? previousLimiterMode) => SmokeAudit.RejectWrite("RTSS");
    public void Dispose() { }
}

internal sealed class SmokeNvidiaClient : INvidiaSettings
{
    public NvidiaSettingsSnapshot Read(string exePath) => default;
    public bool Matches(NvidiaSettingsSnapshot current, PerformanceProfile desired) => false;
    public void ProbeWrite() => SmokeAudit.RejectWrite("NVIDIA");
    public void Apply(GameEntry game, PerformanceProfile desired) => SmokeAudit.RejectWrite("NVIDIA");
    public void Restore(string exePath, SettingBackup backup) => SmokeAudit.RejectWrite("NVIDIA");
    public void Dispose() { }
}
