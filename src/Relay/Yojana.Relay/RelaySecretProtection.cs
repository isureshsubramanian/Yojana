using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Yojana.Relay.Contracts;

namespace Yojana.Relay;

public interface IRelaySecretProtection
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class FileRelaySecretProtection : IRelaySecretProtection
{
    public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
    public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
}

[SupportedOSPlatform("windows")]
public sealed class DpapiRelaySecretProtection : IRelaySecretProtection
{
    public byte[] Protect(byte[] plaintext) => Transform(plaintext, true);
    public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var source = new DataBlob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        var entropyBytes = SHA256.HashData(Encoding.UTF8.GetBytes("YojanaRelay:machine-secrets:v1"));
        var entropy = new DataBlob { Size = entropyBytes.Length, Data = Marshal.AllocHGlobal(entropyBytes.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(entropyBytes, 0, entropy.Data, entropyBytes.Length);
            Marshal.Copy(input, 0, source.Data, input.Length);
            var success = protect
                ? CryptProtectData(ref source, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 0x5, out output)
                : CryptUnprotectData(ref source, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 0x1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[output.Size];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            for (var i = 0; i < source.Size; i++) Marshal.WriteByte(source.Data, i, 0);
            Marshal.FreeHGlobal(source.Data);
            Marshal.FreeHGlobal(entropy.Data);
            CryptographicOperations.ZeroMemory(entropyBytes);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                _ = LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>Provisioning never overwrites a secret file; rotation removes the old file first (see README).</summary>
public sealed class RelaySecretFileExistsException(string path) : IOException(
    $"{path} already exists. Stop the relay, remove that file, then provision the secrets again.");

public sealed class RelaySecretFile(IRelaySecretProtection protection)
{
    public void Write(string path, RelaySecrets secrets)
    {
        if (File.Exists(path)) throw new RelaySecretFileExistsException(path);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(secrets);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = OperatingSystem.IsWindows() ? CreateWindowsFile(path) : new FileStream(path, options);
            stream.Write(protection.Protect(plaintext));
            stream.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateWindowsFile(string path)
    {
        var security = WindowsSecurity();
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadPermissions,
            FileShare.None, 4096, FileOptions.None, security);
    }

    [SupportedOSPlatform("windows")]
    public static FileSecurity WindowsSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var service = (SecurityIdentifier)new NTAccount("NT SERVICE", "YojanaRelay").Translate(typeof(SecurityIdentifier));
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), service })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public RelaySecrets Read(string path)
    {
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            throw new InvalidOperationException("The relay secret file must have mode 0600.");
        var plaintext = protection.Unprotect(File.ReadAllBytes(path));
        try { return JsonSerializer.Deserialize<RelaySecrets>(plaintext) ?? throw new InvalidDataException("relay_secrets_invalid"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
