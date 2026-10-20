using System.Runtime.InteropServices;
using GameProfileManager.Core;

namespace GameProfileManager.Integrations;

// Разметка DRS и ID QueryInterface взяты из опубликованных заголовков NVIDIA NVAPI.
public sealed class NvidiaClient : INvidiaSettings
{
    public const uint VrrId = 0x10A879CF;
    public const uint VSyncId = 0x00A879CF;
    public const uint NvidiaFpsId = 0x10835002;
    public const uint FixedRefresh = 4;
    public const uint AllowGSync = 0;
    public const uint VSyncOn = 0x47814940;
    public const uint VSyncOff = 0x08416747;
    public const uint VSyncApplication = 0x60925292;
    private const int AppSize = 20492; // NVDRS_APPLICATION_V4, UnicodeString[2048]
    private const int ProfileSize = 4116;
    private const int SettingSize = 12320; // NVDRS_SETTING_V1, pack(4)
    private const int CurrentValueOffset = 8220;
    private readonly IntPtr _library;
    private readonly Initialize _initialize;
    private readonly Unload _unload;
    private readonly CreateSession _createSession;
    private readonly DestroySession _destroySession;
    private readonly SessionOnly _loadSettings;
    private readonly SessionOnly _saveSettings;
    private readonly FindApplication _findApplication;
    private readonly CreateProfile _createProfile;
    private readonly CreateApplication _createApplication;
    private readonly FindProfileByName _findProfile;
    private readonly DeleteProfile _deleteProfile;
    private readonly GetSetting _getSetting;
    private readonly SetSetting _setSetting;
    private readonly DeleteSetting _deleteSetting;

    public NvidiaClient()
    {
        _library = NativeLibrary.Load("nvapi64.dll");
        try
        {
            var query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(_library, "nvapi_QueryInterface"));
            T Resolve<T>(uint id) where T : Delegate
            {
                IntPtr ptr = query(id);
                if (ptr == IntPtr.Zero) throw new NotSupportedException($"NVAPI interface 0x{id:X8} отсутствует.");
                return Marshal.GetDelegateForFunctionPointer<T>(ptr);
            }
            _initialize = Resolve<Initialize>(0x0150E828);
            _unload = Resolve<Unload>(0xD22BDD7E);
            _createSession = Resolve<CreateSession>(0x0694D52E);
            _destroySession = Resolve<DestroySession>(0xDAD9CFF8);
            _loadSettings = Resolve<SessionOnly>(0x375DBD6B);
            _saveSettings = Resolve<SessionOnly>(0xFCBC7E14);
            _findApplication = Resolve<FindApplication>(0xEEE566B2);
            _createProfile = Resolve<CreateProfile>(0xCC176068);
            _createApplication = Resolve<CreateApplication>(0x4347A9DE);
            _findProfile = Resolve<FindProfileByName>(0x7E4A9A0B);
            _deleteProfile = Resolve<DeleteProfile>(0x17093206);
            _getSetting = Resolve<GetSetting>(0x73BF8338);
            _setSetting = Resolve<SetSetting>(0x577DD202);
            _deleteSetting = Resolve<DeleteSetting>(0xE4A26362);
            Check(_initialize(), "NvAPI_Initialize");
        }
        catch
        {
            // Если NvAPI_Initialize или получение экспортов завершилось ошибкой,
            // нативная библиотека не должна оставаться загруженной до завершения процесса.
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public NvidiaSettingsSnapshot Read(string exePath)
    {
        using var session = OpenSession();
        IntPtr profile = FindProfile(session.Handle, exePath);
        if (profile == IntPtr.Zero) return default;
        var vrr = ReadSetting(session.Handle, profile, VrrId);
        var vsync = ReadSetting(session.Handle, profile, VSyncId);
        var fps = ReadSetting(session.Handle, profile, NvidiaFpsId);
        return new NvidiaSettingsSnapshot(true, vrr.Value, vrr.Explicit, vsync.Value, vsync.Explicit, fps.Value, fps.Explicit);
    }

    public bool Matches(NvidiaSettingsSnapshot current, PerformanceProfile desired) =>
        current.ProfileExisted && current.VrrExplicit && current.VSyncExplicit && current.FpsExplicit &&
        current.Vrr == (desired.Vrr == VrrMode.FixedRefresh ? FixedRefresh : AllowGSync) &&
        current.VSync == (desired.VSync switch
        {
            VSyncMode.On => VSyncOn,
            VSyncMode.Off => VSyncOff,
            _ => VSyncApplication
        }) && current.Fps == 0;

    public void ProbeWrite()
    {
        using var session = OpenSession();
        Check(_saveSettings(session.Handle), "NvAPI_DRS_SaveSettings (проверка доступа)");
    }

    public void Apply(GameEntry game, PerformanceProfile desired)
    {
        using (var session = OpenSession())
        {
            IntPtr profile = FindProfile(session.Handle, game.ExePath);
            if (profile == IntPtr.Zero) profile = MakeProfile(session.Handle, game);
            SetDword(session.Handle, profile, VrrId, desired.Vrr == VrrMode.FixedRefresh ? FixedRefresh : AllowGSync);
            SetDword(session.Handle, profile, VSyncId, desired.VSync switch
            {
                VSyncMode.On => VSyncOn, VSyncMode.Off => VSyncOff, _ => VSyncApplication
            });
            // RTSS остаётся единственным источником FPS-лимита, которым управляет приложение.
            SetDword(session.Handle, profile, NvidiaFpsId, 0);
            Check(_saveSettings(session.Handle), "NvAPI_DRS_SaveSettings");
        }
        var read = Read(game.ExePath);
        if (!Matches(read, desired))
            throw new InvalidOperationException("NVIDIA DRS не подтвердил сохранённые значения.");
    }

    public void Restore(string exePath, SettingBackup backup)
    {
        if (!backup.NvidiaProfileExisted)
        {
            DeleteManagedProfile(backup.ManagedGameId);
            if (Read(exePath).ProfileExisted) throw new InvalidOperationException("Профиль NVIDIA остался после удаления.");
            return;
        }
        using (var session = OpenSession())
        {
            IntPtr profile = FindProfile(session.Handle, exePath);
            if (profile == IntPtr.Zero) throw new InvalidOperationException("Профиль NVIDIA для восстановления не найден.");
            RestoreOne(session.Handle, profile, VrrId, backup.NvidiaVrrExplicit, backup.NvidiaVrr);
            RestoreOne(session.Handle, profile, VSyncId, backup.NvidiaVSyncExplicit, backup.NvidiaVSync);
            RestoreOne(session.Handle, profile, NvidiaFpsId, backup.NvidiaFpsLimitExplicit, backup.NvidiaFpsLimit);
            Check(_saveSettings(session.Handle), "NvAPI_DRS_SaveSettings");
        }
        var read = Read(exePath);
        if (!read.ProfileExisted || read.VrrExplicit != backup.NvidiaVrrExplicit ||
            read.VSyncExplicit != backup.NvidiaVSyncExplicit || read.FpsExplicit != backup.NvidiaFpsLimitExplicit ||
            (backup.NvidiaVrrExplicit && read.Vrr != backup.NvidiaVrr) ||
            (backup.NvidiaVSyncExplicit && read.VSync != backup.NvidiaVSync) ||
            (backup.NvidiaFpsLimitExplicit && read.Fps != backup.NvidiaFpsLimit))
            throw new InvalidOperationException("NVIDIA DRS не подтвердил восстановление.");
    }

    public void DeleteManagedProfile(Guid gameId)
    {
        using var session = OpenSession();
        int status = _findProfile(session.Handle, Unicode(ManagedName(gameId)), out var profile);
        if (status == -163) return;
        Check(status, "NvAPI_DRS_FindProfileByName");
        Check(_deleteProfile(session.Handle, profile), "NvAPI_DRS_DeleteProfile");
        Check(_saveSettings(session.Handle), "NvAPI_DRS_SaveSettings");
    }

    private Session OpenSession()
    {
        Check(_createSession(out var handle), "NvAPI_DRS_CreateSession");
        try { Check(_loadSettings(handle), "NvAPI_DRS_LoadSettings"); return new Session(handle, _destroySession); }
        catch { _destroySession(handle); throw; }
    }
    private IntPtr FindProfile(IntPtr session, string path)
    {
        IntPtr app = Allocate(AppSize, 4);
        try
        {
            // V4 требуется для поиска по полному пути исполняемого файла.
            int status = _findApplication(session, Unicode(path), out var profile, app);
            if (status == -166) return IntPtr.Zero; // NVAPI_EXECUTABLE_NOT_FOUND
            Check(status, "NvAPI_DRS_FindApplicationByName");
            return profile;
        }
        finally { Marshal.FreeHGlobal(app); }
    }
    private IntPtr MakeProfile(IntPtr session, GameEntry game)
    {
        IntPtr profileInfo = Allocate(ProfileSize, 1);
        IntPtr appInfo = Allocate(AppSize, 4);
        try
        {
            WriteUnicode(profileInfo, 4, ManagedName(game.Id));
            Check(_createProfile(session, profileInfo, out var profile), "NvAPI_DRS_CreateProfile");
            WriteUnicode(appInfo, 8, game.ExePath);
            WriteUnicode(appInfo, 4104, game.Name);
            Check(_createApplication(session, profile, appInfo), "NvAPI_DRS_CreateApplication");
            return profile;
        }
        finally { Marshal.FreeHGlobal(profileInfo); Marshal.FreeHGlobal(appInfo); }
    }
    private static string ManagedName(Guid gameId) => "Game Profile Manager " + gameId.ToString("N");
    private (uint? Value, bool Explicit) ReadSetting(IntPtr session, IntPtr profile, uint id)
    {
        IntPtr setting = Allocate(SettingSize, 1);
        try
        {
            int status = _getSetting(session, profile, id, setting);
            if (status == -160) return (null, false);
            Check(status, $"NvAPI_DRS_GetSetting 0x{id:X8}");
            int type = Marshal.ReadInt32(setting, 4104);
            if (type != 0) throw new NotSupportedException($"NVIDIA setting 0x{id:X8} не DWORD.");
            return (unchecked((uint)Marshal.ReadInt32(setting, CurrentValueOffset)), Marshal.ReadInt32(setting, 4108) == 0);
        }
        finally { Marshal.FreeHGlobal(setting); }
    }
    private void SetDword(IntPtr session, IntPtr profile, uint id, uint value)
    {
        IntPtr setting = Allocate(SettingSize, 1);
        try
        {
            Marshal.WriteInt32(setting, 4100, unchecked((int)id));
            Marshal.WriteInt32(setting, 4104, 0); // NVDRS_DWORD_TYPE
            Marshal.WriteInt32(setting, CurrentValueOffset, unchecked((int)value));
            Check(_setSetting(session, profile, setting), $"NvAPI_DRS_SetSetting 0x{id:X8}");
        }
        finally { Marshal.FreeHGlobal(setting); }
    }
    private void RestoreOne(IntPtr session, IntPtr profile, uint id, bool explicitValue, uint? value)
    {
        if (explicitValue && value.HasValue) SetDword(session, profile, id, value.Value);
        else Check(_deleteSetting(session, profile, id), $"NvAPI_DRS_DeleteProfileSetting 0x{id:X8}");
    }
    private static IntPtr Allocate(int size, int version)
    {
        IntPtr ptr = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i += 4) Marshal.WriteInt32(ptr, i, 0);
        Marshal.WriteInt32(ptr, size | (version << 16));
        return ptr;
    }
    private static byte[] Unicode(string value)
    {
        if (value.Length >= 2048) throw new ArgumentException("Путь слишком длинный для NVAPI.");
        var bytes = new byte[4096];
        System.Text.Encoding.Unicode.GetBytes(value).CopyTo(bytes, 0);
        return bytes;
    }
    private static void WriteUnicode(IntPtr ptr, int offset, string value) => Marshal.Copy(Unicode(value), 0, IntPtr.Add(ptr, offset), 4096);
    private static void Check(int status, string call)
    {
        if (status != 0) throw new InvalidOperationException($"{call}: NVAPI status {status}");
    }
    public void Dispose() { _unload(); NativeLibrary.Free(_library); }
    private sealed class Session(IntPtr handle, DestroySession destroy) : IDisposable
    {
        public IntPtr Handle { get; } = handle;
        public void Dispose() => destroy(Handle);
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Initialize();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Unload();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSession(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DestroySession(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionOnly(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindApplication(IntPtr session, byte[] appName, out IntPtr profile, IntPtr application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateProfile(IntPtr session, IntPtr profileInfo, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateApplication(IntPtr session, IntPtr profile, IntPtr application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindProfileByName(IntPtr session, byte[] name, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteProfile(IntPtr session, IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSetting(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteSetting(IntPtr session, IntPtr profile, uint id);
}
