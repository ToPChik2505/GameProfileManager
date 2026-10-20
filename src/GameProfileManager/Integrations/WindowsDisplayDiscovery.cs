using System.ComponentModel;
using System.Runtime.InteropServices;
using GameProfileManager.Core;

namespace GameProfileManager.Integrations;

public static class WindowsDisplayDiscovery
{
    private const uint QueryFlags = 0x2 | 0x10 | 0x40; // Активные пути, виртуальные режимы и динамическая частота Windows 11.

    public static IReadOnlyList<DisplayInfo> Read()
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int result = GetDisplayConfigBufferSizes(QueryFlags, out uint pathCount, out uint modeCount);
            if (result != 0) throw new Win32Exception(result, "Не удалось получить список экранов Windows.");
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            result = QueryDisplayConfig(QueryFlags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == 122) continue; // Топология изменилась во время выделения буферов.
            if (result != 0) throw new Win32Exception(result, "Не удалось прочитать параметры экранов Windows.");
            var displays = new List<DisplayInfo>();
            foreach (var path in paths.Take((int)pathCount))
            {
                if ((path.Flags & 1) == 0) continue;
                int sourceIndex = (path.Flags & 0x8) != 0 || (path.Source.ModeIndex & 0xffff) == 0xffff
                    ? (int)(path.Source.ModeIndex >> 16) : (int)path.Source.ModeIndex;
                int targetIndex = (path.Flags & 0x8) != 0 || (path.Target.ModeIndex & 0xffff) == 0xffff
                    ? (int)(path.Target.ModeIndex >> 16) : (int)path.Target.ModeIndex;
                if (sourceIndex < 0 || sourceIndex >= modeCount || targetIndex < 0 || targetIndex >= modeCount) continue;
                ModeInfo sourceMode = modes[sourceIndex];
                ModeInfo targetMode = modes[targetIndex];
                if (sourceMode.Type != 1 || targetMode.Type != 2) continue;

                string deviceName = GetSourceName(path.Source.Adapter, path.Source.Id);
                string name = GetTargetName(path.Target.Adapter, path.Target.Id, out string devicePath);
                string id = !string.IsNullOrWhiteSpace(devicePath)
                    ? devicePath : $"{path.Target.Adapter.High:X8}{path.Target.Adapter.Low:X8}:{path.Target.Id}";
                uint numerator = targetMode.VSync.Numerator != 0 ? targetMode.VSync.Numerator : path.Target.Refresh.Numerator;
                uint denominator = targetMode.VSync.Denominator != 0 ? targetMode.VSync.Denominator : path.Target.Refresh.Denominator;
                if (numerator == 0 || denominator == 0) continue;
                bool primary = IsPrimary(deviceName);
                displays.Add(new DisplayInfo(id, string.IsNullOrWhiteSpace(name) ? deviceName : name,
                    deviceName, (int)sourceMode.Width, (int)sourceMode.Height,
                    numerator, denominator, primary, Connection(path.Target.OutputTechnology),
                    (path.Flags & 0x10) != 0, GetAdvancedColor(path.Target.Adapter, path.Target.Id)));
            }
            return displays;
        }
        throw new Win32Exception(122, "Конфигурация экранов меняется. Повторите проверку.");
    }

    private static string GetSourceName(AdapterId adapter, uint id)
    {
        var request = new SourceName
        {
            Header = new DeviceHeader { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = adapter, Id = id },
            View = ""
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.View.TrimEnd('\0') : "";
    }

    private static string GetTargetName(AdapterId adapter, uint id, out string path)
    {
        var request = new TargetName
        {
            Header = new DeviceHeader { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(), Adapter = adapter, Id = id },
            FriendlyName = "", DevicePath = ""
        };
        if (DisplayConfigGetDeviceInfo(ref request) != 0) { path = ""; return ""; }
        path = request.DevicePath.TrimEnd('\0');
        return request.FriendlyName.TrimEnd('\0');
    }

    private static bool? GetAdvancedColor(AdapterId adapter, uint id)
    {
        var request = new AdvancedColor
        {
            Header = new DeviceHeader { Type = 9, Size = (uint)Marshal.SizeOf<AdvancedColor>(), Adapter = adapter, Id = id }
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? (request.Flags & 2) != 0 : null;
    }

    private static bool IsPrimary(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return false;
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>(), Name = "", Description = "", DeviceId = "", DeviceKey = "" };
        for (uint index = 0; EnumDisplayDevices(null, index, ref device, 0); index++)
        {
            if (device.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                return (device.StateFlags & 4) != 0;
            device.Size = Marshal.SizeOf<DisplayDevice>();
        }
        return false;
    }

    private static string Connection(int value) => value switch
    {
        5 => "HDMI", 10 => "DisplayPort", 11 => "Встроенный DisplayPort",
        4 => "DVI", 0 => "VGA", 6 => "Встроенный экран", 15 => "Miracast", _ => "Подключение не определено"
    };

    [StructLayout(LayoutKind.Sequential)] private struct AdapterId { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator; public uint Denominator; }
    [StructLayout(LayoutKind.Sequential)] private struct SourceInfo
    { public AdapterId Adapter; public uint Id; public uint ModeIndex; public uint StatusFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct TargetInfo
    {
        public AdapterId Adapter; public uint Id; public uint ModeIndex; public int OutputTechnology;
        public int Rotation; public int Scaling; public Rational Refresh; public int ScanLineOrdering;
        public int Available; public uint StatusFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo
    { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)] private struct ModeInfo
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint Width;
        [FieldOffset(20)] public uint Height;
        [FieldOffset(32)] public Rational VSync;
    }
    [StructLayout(LayoutKind.Sequential)] private struct DeviceHeader
    { public uint Type; public uint Size; public AdapterId Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct SourceName
    {
        public DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string View;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TargetName
    {
        public DeviceHeader Header; public uint Flags; public int OutputTechnology;
        public ushort Manufacturer; public ushort Product; public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AdvancedColor
    { public DeviceHeader Header; public uint Flags; public int Encoding; public uint BitsPerColorChannel; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
        ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DisplayConfigGetDeviceInfo(ref SourceName request);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DisplayConfigGetDeviceInfo(ref TargetName request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor request);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice result, uint flags);
}
