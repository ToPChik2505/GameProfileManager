using System.Runtime.InteropServices;
using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GameProfileManager.Integrations;

/// <summary>Rejects elevated code loads from paths a standard Windows user can replace.</summary>
public static class ProtectedInstallPath
{
    private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    // Composite rights such as FullControl also contain read bits. Testing their masks with
    // bitwise AND would reject an ordinary Users:ReadAndExecute rule in Program Files.
    private static readonly FileSystemRights DangerousRights = FileSystemRights.WriteData |
        FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.WriteAttributes |
        FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership |
        (FileSystemRights)0x40000000 | // GENERIC_WRITE
        (FileSystemRights)0x10000000;  // GENERIC_ALL

    public static string Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new UnauthorizedAccessException("Путь установки пуст.");
        string full = Path.GetFullPath(path);
        string? root = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)))
            .FirstOrDefault(value => full.StartsWith(value + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
        if (root is null || !File.Exists(full))
            throw new UnauthorizedAccessException("Файл должен находиться в защищённой установке Program Files.");

        // A reparse point in any component can redirect the load to a user-writable tree.
        string current = root;
        CheckDirectory(current);
        string relative = Path.GetRelativePath(root, full);
        string[] parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            current = Path.Combine(current, parts[i]);
            CheckDirectory(current);
        }
        var file = new FileInfo(full);
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0) Reject();
        CheckAcl(file.GetAccessControl());

        using SafeFileHandle handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        var final = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) Reject();
        string actual = final.ToString();
        if (!actual.StartsWith(@"\\?\", StringComparison.Ordinal)) Reject();
        actual = Path.GetFullPath(actual[4..]);
        if (!string.Equals(actual, full, StringComparison.OrdinalIgnoreCase)) Reject();
        return full;
    }

    private static void CheckDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) Reject();
        CheckAcl(directory.GetAccessControl());
    }

    private static void CheckAcl(FileSystemSecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrusted(owner.Value)) Reject();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.FileSystemRights & DangerousRights) == 0) continue;
            string sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (!IsTrusted(sid) && !(sid == "S-1-3-0" && IsTrusted(owner.Value))) Reject();
        }
    }

    private static bool IsTrusted(string sid) => sid is "S-1-5-18" or "S-1-5-32-544" or TrustedInstallerSid;

    [DoesNotReturn]
    private static void Reject() => throw new UnauthorizedAccessException(
        "Каталог или файл доступен для подмены либо перенаправлен. Повышенная загрузка заблокирована.");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
