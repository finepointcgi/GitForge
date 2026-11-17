using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using GitForge.Core.Abstractions;

namespace GitForge.Platform.Secrets;

/// <summary>
/// Windows Credential Manager (DPAPI-protected, per-user). Entries are "Generic" credentials named
/// <c>GitForge:{key}</c>, persisted for the local machine only (never roamed with the profile).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ISecretStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxBlobBytes = 5 * 512; // CRED_MAX_CREDENTIAL_BLOB_SIZE
    private const string Prefix = "GitForge:";

    public string BackendName => "Windows Credential Manager";
    public bool IsSupported => true;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!CredRead(Prefix + key, CredTypeGeneric, 0, out var ptr))
        {
            var err = Marshal.GetLastPInvokeError();
            return err == ErrorNotFound ? Task.FromResult<string?>(null) : throw new InvalidOperationException($"CredRead failed (Win32 error {err}).");
        }

        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return Task.FromResult<string?>(string.Empty);
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            var secret = Encoding.Unicode.GetString(bytes);
            Array.Clear(bytes);
            return Task.FromResult<string?>(secret);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public Task SetAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(secret);
        var bytes = Encoding.Unicode.GetBytes(secret);
        if (bytes.Length > MaxBlobBytes) throw new ArgumentException("Secret exceeds the Windows Credential Manager size limit (2,560 bytes).", nameof(secret));

        var target = Marshal.StringToCoTaskMemUni(Prefix + key);
        var user = Marshal.StringToCoTaskMemUni(Environment.UserName);
        var blob = Marshal.AllocCoTaskMem(Math.Max(bytes.Length, 1));
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = user,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CredPersistLocalMachine,
            };
            var credPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Credential>());
            try
            {
                Marshal.StructureToPtr(cred, credPtr, fDeleteOld: false);
                if (!CredWrite(credPtr, 0)) throw new InvalidOperationException($"CredWrite failed (Win32 error {Marshal.GetLastPInvokeError()}).");
            }
            finally
            {
                Marshal.FreeHGlobal(credPtr);
            }
        }
        finally
        {
            // Scrub plaintext copies before releasing memory.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Array.Clear(bytes);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!CredDelete(Prefix + key, CredTypeGeneric, 0))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err != ErrorNotFound) throw new InvalidOperationException($"CredDelete failed (Win32 error {err}).");
        }

        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(IntPtr credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
