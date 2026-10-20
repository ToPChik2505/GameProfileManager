using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameProfileManager.Core;
using GameProfileManager.Integrations;

namespace GameProfileManager;

internal static class FileIcon
{
    private const int ArtworkDecodeWidth = 256;

    public static ImageSource? Load(string exePath, string? steamAppId)
    {
        string? artwork = SteamDiscovery.FindArtwork(steamAppId);
        if (artwork is not null)
        {
            try
            {
                int sourceWidth = GetImageWidth(artwork);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                // 92 DIP в инспекторе при масштабе 225% занимают около 207 физических пикселей.
                image.DecodePixelWidth = ArtworkDecodeWidth;
                image.UriSource = new Uri(artwork);
                image.EndInit();
                image.Freeze();
                // Низкое разрешение значка из кэша Steam заменяем значком EXE, если он есть.
                if (sourceWidth >= ArtworkDecodeWidth && image.PixelHeight >= 96) return image;
                return Load(exePath) ?? image;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or
                FileFormatException or ArgumentException) { }
        }
        return Load(exePath);
    }

    private static int GetImageWidth(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return decoder.Frames.Count == 0 ? 0 : decoder.Frames[0].PixelWidth;
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
