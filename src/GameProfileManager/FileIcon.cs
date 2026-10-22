using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GameProfileManager.Integrations;

namespace GameProfileManager;

internal static class FileIcon
{
    private const int ArtworkDecodeWidth = 256;

    public static string? LoadPath(string exePath, string? steamAppId)
    {
        string? artwork = SteamDiscovery.FindArtwork(steamAppId);
        if (artwork is not null)
        {
            try
            {
                using var source = Image.FromFile(artwork);
                if (source.Width >= ArtworkDecodeWidth && source.Height >= 96) return artwork;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return ExtractExeIcon(exePath) ?? artwork;
    }

    private static string? ExtractExeIcon(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        string key = exePath + "|" + File.GetLastWriteTimeUtc(exePath).Ticks;
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".png";
        string directory = Path.Combine(Path.GetTempPath(), "GameProfileManager", "icons");
        string destination = Path.Combine(directory, name);
        if (File.Exists(destination)) return destination;

        var handles = new IntPtr[1];
        uint count = PrivateExtractIcons(exePath, 0, 256, 256, handles, new uint[1], 1, 0);
        if (count != 1 || handles[0] == IntPtr.Zero) return null;
        try
        {
            Directory.CreateDirectory(directory);
            using var icon = (Icon)Icon.FromHandle(handles[0]).Clone();
            using var bitmap = icon.ToBitmap();
            string pending = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bitmap.Save(pending, System.Drawing.Imaging.ImageFormat.Png);
            try { File.Move(pending, destination); }
            catch (IOException) { File.Delete(pending); }
            return destination;
        }
        finally { DestroyIcon(handles[0]); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
    private static extern uint PrivateExtractIcons(string file, int index, int width, int height,
        [Out] IntPtr[] icons, [Out] uint[] iconIds, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
