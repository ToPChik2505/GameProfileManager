using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GameProfileManager.Application;

namespace GameProfileManager.Infrastructure;

/// <summary>Stores the PCGamingWiki bot password in the current Windows user's Credential Manager.</summary>
public sealed class PcgamingWikiCredentialStore : IPcgamingWikiCredentialStore
{
    private const string TargetName = "GameProfileManager.PCGamingWiki.BotPassword";
    private const uint GenericCredential = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;
    private const int MaxCredentialBlobBytes = 2560;

    public PcgamingWikiCredentials? Read()
    {
        if (!CredRead(TargetName, GenericCredential, 0, out IntPtr credential))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error, "Не удалось прочитать учётные данные PCGamingWiki из Windows Credential Manager.");
        }

        try
        {
            NativeCredential value = Marshal.PtrToStructure<NativeCredential>(credential);
            string username = Marshal.PtrToStringUni(value.UserName) ?? "";
            if (value.CredentialBlobSize == 0 || value.CredentialBlobSize > MaxCredentialBlobBytes || value.CredentialBlob == IntPtr.Zero)
                throw new InvalidDataException("Сохранённый bot-пароль PCGamingWiki повреждён. Удалите его и сохраните заново.");

            byte[] passwordBytes = new byte[value.CredentialBlobSize];
            try
            {
                Marshal.Copy(value.CredentialBlob, passwordBytes, 0, passwordBytes.Length);
                string password = new UTF8Encoding(false, true).GetString(passwordBytes);
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                    throw new InvalidDataException("Сохранённые данные PCGamingWiki неполны. Сохраните bot-логин и пароль заново.");
                return new PcgamingWikiCredentials(username, password);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Сохранённый bot-пароль PCGamingWiki повреждён. Удалите его и сохраните заново.", ex);
            }
            finally { CryptographicOperations.ZeroMemory(passwordBytes); }
        }
        finally { CredFree(credential); }
    }

    public void Save(PcgamingWikiCredentials credentials)
    {
        string username = credentials.Username.Trim();
        if (username.Length is 0 or > 256)
            throw new ArgumentException("Введите bot-имя пользователя PCGamingWiki, выданное при создании bot-пароля.", nameof(credentials));
        if (string.IsNullOrWhiteSpace(credentials.Password))
            throw new ArgumentException("Введите bot-пароль PCGamingWiki.", nameof(credentials));

        byte[] passwordBytes = Encoding.UTF8.GetBytes(credentials.Password);
        if (passwordBytes.Length > MaxCredentialBlobBytes)
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            throw new ArgumentException("Bot-пароль превышает поддерживаемый размер Windows Credential Manager.", nameof(credentials));
        }

        IntPtr target = IntPtr.Zero;
        IntPtr comment = IntPtr.Zero;
        IntPtr user = IntPtr.Zero;
        IntPtr secret = IntPtr.Zero;
        try
        {
            target = Marshal.StringToCoTaskMemUni(TargetName);
            comment = Marshal.StringToCoTaskMemUni("PCGamingWiki Cargo API access for GameProfileManager");
            user = Marshal.StringToCoTaskMemUni(username);
            secret = Marshal.AllocHGlobal(passwordBytes.Length);
            Marshal.Copy(passwordBytes, 0, secret, passwordBytes.Length);

            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)passwordBytes.Length,
                CredentialBlob = secret,
                Persist = PersistLocalMachine,
                UserName = user
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось сохранить bot-пароль в Windows Credential Manager.");
        }
        finally
        {
            if (secret != IntPtr.Zero)
            {
                Marshal.Copy(new byte[passwordBytes.Length], 0, secret, passwordBytes.Length);
                Marshal.FreeHGlobal(secret);
            }
            if (user != IntPtr.Zero) Marshal.FreeCoTaskMem(user);
            if (comment != IntPtr.Zero) Marshal.FreeCoTaskMem(comment);
            if (target != IntPtr.Zero) Marshal.FreeCoTaskMem(target);
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public void Delete()
    {
        if (CredDelete(TargetName, GenericCredential, 0)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != NotFound)
            throw new Win32Exception(error, "Не удалось удалить bot-пароль из Windows Credential Manager.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
