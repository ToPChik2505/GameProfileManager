using System.Diagnostics;
using Microsoft.Win32;
using GameProfileManager.Core;

namespace GameProfileManager.Infrastructure;

public sealed record UnityPlayerPrefsRegistryCandidate(string KeyPath, string ValueName, string Description);

/// <summary>
/// Discovers Unity PlayerPrefs under the company/product key recorded in the game's executable
/// metadata. It inspects value names and types only; actual settings values are read only after
/// the user selects a candidate and creates a snapshot.
/// </summary>
public sealed class UnityPlayerPrefsRegistryDiscovery
{
    private const int MaxCandidates = 32;
    private static readonly string[] GraphicsValueMarkers =
    [
        "screenmanager", "unityselectmonitor", "resolution", "fullscreen", "refresh", "monitor",
        "display", "graphic", "quality", "vsync", "verticalsync", "antialias", "texture", "shadow",
        "brightness", "hdr", "render", "window", "stereo", "aspectratio", "targetframerate", "framecap"
    ];

    public IReadOnlyList<UnityPlayerPrefsRegistryCandidate> FindCandidates(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.ExePath) || !File.Exists(game.ExePath)) return [];
        if (!LooksLikeUnityGame(game)) return [];

        string company = ReadMetadata(game.ExePath, productName: false);
        string product = ReadMetadata(game.ExePath, productName: true);
        if (!IsSafeRegistrySegment(company)) company = "";
        if (!IsSafeRegistrySegment(product)) product = "";
        var identity = new[]
            {
                product, game.Name, Path.GetFileNameWithoutExtension(game.ExePath),
                Path.GetFileName(game.InstallDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "")
            }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalize)
            .Where(value => value.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (identity.Count == 0) return [];

        var keyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using RegistryKey? software = Registry.CurrentUser.OpenSubKey("Software", writable: false);
            if (software is null) return [];

            if (!string.IsNullOrWhiteSpace(company))
            {
                string companyKeyName = software.GetSubKeyNames()
                    .FirstOrDefault(name => Normalize(name).Equals(Normalize(company), StringComparison.OrdinalIgnoreCase))
                    ?? company;
                using RegistryKey? companyKey = software.OpenSubKey(companyKeyName, writable: false);
                if (companyKey is not null)
                {
                    foreach (string productKey in companyKey.GetSubKeyNames())
                        if (identity.Contains(Normalize(productKey)))
                            keyPaths.Add($"HKCU\\Software\\{companyKeyName}\\{productKey}");
                }

                // Also check the exact metadata-derived path in case the product key is not listed
                // in a stale or virtualized parent enumeration.
                if (product.Length > 0) keyPaths.Add($"HKCU\\Software\\{companyKeyName}\\{product}");
            }
            else
            {
                foreach (string productKey in software.GetSubKeyNames())
                    if (identity.Contains(Normalize(productKey))) keyPaths.Add($"HKCU\\Software\\{productKey}");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return [];
        }

        var candidates = new List<UnityPlayerPrefsRegistryCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string keyPath in keyPaths)
        {
            if (candidates.Count >= MaxCandidates) break;
            string subKey = keyPath["HKCU\\".Length..];
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
                if (key is null) continue;
                foreach (string valueName in key.GetValueNames())
                {
                    if (candidates.Count >= MaxCandidates || !LooksLikeGraphicsPreference(valueName)) continue;
                    RegistryValueKind kind;
                    try { kind = key.GetValueKind(valueName); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    if (kind is not (RegistryValueKind.DWord or RegistryValueKind.QWord or RegistryValueKind.String or RegistryValueKind.ExpandString))
                        continue;
                    if (!seen.Add(keyPath + "|" + valueName)) continue;
                    candidates.Add(new UnityPlayerPrefsRegistryCandidate(keyPath, valueName,
                        $"Unity PlayerPrefs · графический параметр ({kind})"));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // One inaccessible candidate key must not prevent discovery in the other exact keys.
            }
        }
        return candidates;
    }

    private static bool LooksLikeGraphicsPreference(string name)
    {
        string normalized = Normalize(name);
        return GraphicsValueMarkers.Any(normalized.Contains);
    }

    private static bool LooksLikeUnityGame(GameEntry game)
    {
        var roots = new[]
            {
                !string.IsNullOrWhiteSpace(game.InstallDirectory) ? game.InstallDirectory : null,
                Path.GetDirectoryName(game.ExePath)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            try
            {
                if (File.Exists(Path.Combine(root, "UnityPlayer.dll"))) return true;
                foreach (string dataDirectory in Directory.EnumerateDirectories(root, "*_Data", SearchOption.TopDirectoryOnly).Take(128))
                    if (File.Exists(Path.Combine(dataDirectory, "globalgamemanagers"))) return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // An inaccessible install path is not evidence that this is a Unity title.
            }
        }
        return false;
    }

    private static string ReadMetadata(string executablePath, bool productName)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return (productName ? info.ProductName : info.CompanyName)?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    private static bool IsSafeRegistrySegment(string value) => !string.IsNullOrWhiteSpace(value) &&
        !value.Contains('\\') && !value.Contains('/') && value is not ("." or "..");
}
