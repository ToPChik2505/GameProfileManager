using System.Diagnostics;
using System.Runtime.InteropServices;
using GameProfileManager.Core;

namespace GameProfileManager.Application;

public enum GameProcessState { Closed, Running, Unknown }
public sealed record GameProcessCheck(GameProcessState State, string Detail);

/// <summary>Checks full executable paths; inaccessible matching processes are treated as uncertain.</summary>
public sealed class GameProcessGuard
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public GameProcessCheck Check(GameEntry game)
    {
        string[] paths = new[] { game.ExePath }.Concat(game.GameConfigSettings.AdditionalProcessPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return new(GameProcessState.Unknown, "Не задан путь игрового процесса.");
        string[] names = paths.Select(path => Path.GetFileNameWithoutExtension(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool unknown = false;
        foreach (string name in names)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { unknown = true; continue; }
            foreach (var process in processes)
            {
                using (process)
                {
                    if (!TryGetExecutablePath(process.Id, out string? actual) || actual is null)
                    { unknown = true; continue; }
                    if (paths.Any(path => actual.Equals(path, StringComparison.OrdinalIgnoreCase)))
                        return new(GameProcessState.Running, "Процесс игры ещё работает.");
                }
            }
        }
        return unknown
            ? new(GameProcessState.Unknown, "Невозможно надёжно проверить совпавший процесс; применение отложено.")
            : new(GameProcessState.Closed, "Игровые процессы закрыты.");
    }

    private static bool TryGetExecutablePath(int processId, out string? path)
    {
        path = null;
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return false;
        try
        {
            var buffer = new System.Text.StringBuilder(32768);
            int size = buffer.Capacity;
            if (!QueryFullProcessImageName(handle, 0, buffer, ref size)) return false;
            path = Path.GetFullPath(buffer.ToString());
            return true;
        }
        catch { return false; }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
