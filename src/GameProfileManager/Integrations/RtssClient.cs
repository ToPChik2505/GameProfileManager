using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using GameProfileManager.Core;

namespace GameProfileManager.Integrations;

public sealed class RtssClient : IRtssSettings
{
    private readonly IntPtr _library;
    private readonly LoadProfile _load;
    private readonly SaveProfile _save;
    private readonly GetProperty _get;
    private readonly SetProperty _set;
    private readonly UpdateProfiles _update;
    private readonly DeleteProfile _delete;

    public string InstallDirectory { get; }
    public bool IsRunning => ProcessIsRunning;
    public static bool ProcessIsRunning
    {
        get
        {
            var processes = Process.GetProcessesByName("RTSS");
            try { return processes.Length > 0; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }
    public static string? DetectDirectory()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Unwinder\RTSS")
            ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Unwinder\RTSS");
        return key?.GetValue("InstallDir") as string;
    }

    public RtssClient(string? preferredDirectory)
    {
        InstallDirectory = string.IsNullOrWhiteSpace(preferredDirectory) ? DetectDirectory() ?? "" : preferredDirectory;
        string library = Path.Combine(InstallDirectory, "RTSSHooks64.dll");
        if (!File.Exists(library)) throw new FileNotFoundException("RTSSHooks64.dll не найден. Укажите папку RTSS в настройках.", library);
        _library = NativeLibrary.Load(library);
        _load = Resolve<LoadProfile>("LoadProfile");
        _save = Resolve<SaveProfile>("SaveProfile");
        _get = Resolve<GetProperty>("GetProfileProperty");
        _set = Resolve<SetProperty>("SetProfileProperty");
        _update = Resolve<UpdateProfiles>("UpdateProfiles");
        _delete = Resolve<DeleteProfile>("DeleteProfile");
    }

    public bool ProfileExists(string exeName)
    {
        return File.Exists(ProfilePath(exeName));
    }

    public int? ReadFps(string exeName)
    {
        _load(Ansi(exeName));
        byte[] value = new byte[4];
        return _get(Ansi("FramerateLimit"), value, 4) ? BitConverter.ToInt32(value) : null;
    }

    public int? ReadLimiterMode(string exeName)
    {
        _load(Ansi(exeName));
        byte[] value = new byte[4];
        if (_get(Ansi("SyncLimiter"), value, 4)) return BitConverter.ToInt32(value);
        int? explicitValue = ReadProfileValue(ProfilePath(exeName), "SyncLimiter");
        return explicitValue ?? ReadProfileValue(Path.Combine(InstallDirectory, "Profiles", "Global"), "SyncLimiter");
    }

    public void ProbeWrite(string exeName, int? targetFps)
    {
        if (!IsRunning) throw new NotSupportedException("RTSS закрыт. Запустите RTSS перед применением профиля.");
        // An existing RTSS profile may have an unlimited (zero) FPS limit. Preserve it on restore.
        if (targetFps is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(targetFps));
        string profiles = Path.Combine(InstallDirectory, "Profiles");
        if (!Directory.Exists(profiles)) throw new DirectoryNotFoundException("Папка профилей RTSS не найдена: " + profiles);

        string path = ProfilePath(exeName);
        string source = File.Exists(path) ? path : Path.Combine(profiles, "Global");
        if (targetFps is > 0 && ReadProfileValue(source, "LimitDenominator") is int denominator && denominator != 1)
            throw new NotSupportedException("RTSS использует LimitDenominator ≠ 1; запись FPS заблокирована.");
        if (File.Exists(path))
        {
            if (ReadFps(exeName) is null)
                throw new NotSupportedException("Текущее значение FPS в RTSS неизвестно; безопасное восстановление невозможно.");
            using var target = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        string probe = Path.Combine(profiles, ".gpm-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.DeleteOnClose)) { }
        if (!IsRunning) throw new NotSupportedException("RTSS был закрыт во время проверки. Запустите его снова.");
    }

    public void WriteSettings(string exeName, int fps, RtssLimiterMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (fps is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(fps));
        WriteCore(exeName, fps, (int)mode);
    }

    private void WriteCore(string exeName, int fps, int? limiterMode)
    {
        if (!IsRunning) throw new NotSupportedException("RTSS закрыт. Применение остановлено.");
        if (fps is < 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(fps));
        string source = ProfileExists(exeName) ? ProfilePath(exeName) : Path.Combine(InstallDirectory, "Profiles", "Global");
        if (fps > 0 && ReadProfileValue(source, "LimitDenominator") is int denominator && denominator != 1)
            throw new NotSupportedException("RTSS использует LimitDenominator ≠ 1; приложение не будет записывать неверный эффективный FPS.");
        _load(Ansi(exeName));
        if (!_set(Ansi("FramerateLimit"), BitConverter.GetBytes(fps), 4))
            throw new InvalidOperationException("RTSS отклонил запись FramerateLimit.");
        if (limiterMode.HasValue && !_set(Ansi("SyncLimiter"), BitConverter.GetBytes(limiterMode.Value), 4))
            throw new InvalidOperationException("RTSS отклонил запись SyncLimiter.");
        _save(Ansi(exeName));
        _update();
        if (!ProfileExists(exeName) || ReadFps(exeName) != fps || ReadProfileValue(ProfilePath(exeName), "Limit") != fps ||
            fps > 0 && ReadProfileValue(ProfilePath(exeName), "LimitDenominator") is int savedDenominator && savedDenominator != 1)
            throw new InvalidOperationException("RTSS не подтвердил FPS в файле профиля после сохранения. Если RTSS установлен в Program Files, запустите Game Profile Manager от имени администратора при применении профилей.");
        if (limiterMode.HasValue && (ReadLimiterMode(exeName) != limiterMode.Value ||
            ReadProfileValue(ProfilePath(exeName), "SyncLimiter") != limiterMode.Value))
            throw new InvalidOperationException("RTSS не подтвердил режим ограничения кадров в файле профиля после сохранения.");
    }

    public void Restore(string exeName, bool existed, int? previousFps, int? previousLimiterMode)
    {
        if (!IsRunning) throw new NotSupportedException("RTSS закрыт. Восстановление остановлено.");
        if (!existed)
        {
            _delete(Ansi(exeName)); _update();
            if (ProfileExists(exeName)) throw new InvalidOperationException("RTSS не удалил созданный профиль.");
            return;
        }
        if (previousFps.HasValue) WriteCore(exeName, previousFps.Value, previousLimiterMode);
        else throw new InvalidOperationException("Исходное значение RTSS неизвестно; профиль не изменён.");
    }

    private T Resolve<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    private string ProfilePath(string exeName) => Path.Combine(InstallDirectory, "Profiles", exeName + ".cfg");
    private static int? ReadProfileValue(string path, string key)
    {
        if (!File.Exists(path)) return null;
        string section = "";
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) section = line;
            if (section.Equals("[Framerate]", StringComparison.OrdinalIgnoreCase) && line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.AsSpan(key.Length + 1), out int value)) return value;
        }
        return null;
    }
    private static byte[] Ansi(string text)
    {
        IntPtr ptr = Marshal.StringToHGlobalAnsi(text);
        try
        {
            int length = 0;
            while (Marshal.ReadByte(ptr, length) != 0) length++;
            byte[] bytes = new byte[length + 1];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }
    public void Dispose() => NativeLibrary.Free(_library);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void LoadProfile(byte[] profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SaveProfile(byte[] profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool GetProperty(byte[] property, byte[] data, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate bool SetProperty(byte[] property, byte[] data, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UpdateProfiles();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeleteProfile(byte[] profile);
}
