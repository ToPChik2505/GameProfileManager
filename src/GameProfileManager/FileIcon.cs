using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameProfileManager.Core;
using GameProfileManager.Integrations;

namespace GameProfileManager;

internal static class FileIcon
{
    public static ImageSource? Load(GameEntry game)
    {
        string? artwork = SteamDiscovery.FindArtwork(game.SteamAppId);
        if (artwork is not null)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                // 92 DIP в инспекторе при масштабе Windows 200% требуют около 184 физических пикселей.
                // Декодирование до 192 уменьшает память изображения по сравнению с 256 без потери детализации в этом размере.
                image.DecodePixelWidth = 192;
                image.UriSource = new Uri(artwork);
                image.EndInit();
                image.Freeze();
                // Иногда Steam хранит только значок 32x32. В этом случае
                // используем встроенный значок игры вместо растягивания миниатюры.
                if (image.PixelWidth >= 96 && image.PixelHeight >= 96) return image;
                return Load(game.ExePath) ?? image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException) { }
        }
        return Load(game.ExePath);
    }

    public static ImageSource? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var icons = new IntPtr[1];
        uint extracted = PrivateExtractIcons(path, 0, 256, 256, icons, new uint[1], 1, 0);
        if (extracted == 1 && icons[0] != IntPtr.Zero)
        {
            try
            {
                var large = Imaging.CreateBitmapSourceFromHIcon(icons[0], System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                large.Freeze();
                return large;
            }
            finally { DestroyIcon(icons[0]); }
        }
        var info = new ShellFileInfo();
        IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x1);
        if (result == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally { DestroyIcon(info.Icon); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height,
        [Out] IntPtr[] icons, [Out] uint[] iconIds, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
